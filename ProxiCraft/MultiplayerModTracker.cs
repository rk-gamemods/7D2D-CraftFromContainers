using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProxiCraft;

/// <summary>
/// Tracks container-related mods running on other players in multiplayer.
/// Detects potential conflicts and logs warnings to help diagnose CTD issues.
///
/// STABILITY GUARANTEES:
/// - All public methods are wrapped in try-catch - cannot throw
/// - Uses thread-safe ConcurrentDictionary for multiplayer safety
/// - All network packet operations are defensive (null checks, try-catch)
/// - Feature is passive (logging only) - cannot affect gameplay
///
/// MULTIPLAYER SAFETY LOCK:
/// - In multiplayer, mod functionality is DISABLED by default until server confirms ProxiCraft
/// - Single-player games bypass this check entirely (no lock needed)
/// - When client joins server, sends handshake and waits for response
/// - If server responds: unlock mod functionality
/// - If timeout (server doesn't have ProxiCraft): keep locked + show warning
/// - This prevents CTD from client/server state mismatch
///
/// SERVER DETECTION:
/// - Tracks when we send a handshake and whether server responds
/// - If no response within timeout, warns user that server may not have ProxiCraft
/// - This helps diagnose CTD issues from client/server mod mismatch
/// </summary>
public static class MultiplayerModTracker
{
    // Multiplayer safety lock - mod is disabled until server confirms ProxiCraft
    private static bool _isMultiplayerSession;
    private static bool _multiplayerUnlocked;

    // Server response tracking
    private static DateTime? _handshakeSentTime;
    private static bool _serverResponseReceived;
    private static bool _serverWarningShown;
    private const float SERVER_RESPONSE_TIMEOUT_SECONDS = 10f;

    /// <summary>
    /// Information about a player's container mods
    /// </summary>
    public class PlayerModInfo
    {
        public int EntityId { get; set; }
        public string PlayerName { get; set; }
        public string ModName { get; set; }
        public string ModVersion { get; set; }
        public List<string> DetectedConflictingMods { get; set; } = new List<string>();
        public DateTime JoinTime { get; set; }
    }

    // Thread-safe tracking of mod info by entity ID
    private static readonly ConcurrentDictionary<int, PlayerModInfo> _playerMods =
        new ConcurrentDictionary<int, PlayerModInfo>();

    // Known conflicting mod identifiers
    private static readonly string[] ConflictingModIdentifiers =
    {
        "BeyondStorage",
        "CraftFromContainers",
        "CraftFromContainersPlus",
        "CraftFromChests",
        "PullFromContainers"
    };

    /// <summary>
    /// Called when we receive a handshake from another player.
    /// </summary>
    public static void OnHandshakeReceived(int entityId, string playerName, string modName, string modVersion, List<string> detectedConflicts)
    {
        try
        {
            var info = new PlayerModInfo
            {
                EntityId = entityId,
                PlayerName = playerName ?? "Unknown",
                ModName = modName ?? "Unknown",
                ModVersion = modVersion ?? "0.0.0",
                DetectedConflictingMods = detectedConflicts ?? new List<string>(),
                JoinTime = DateTime.Now
            };

            _playerMods[entityId] = info;

            ProxiCraft.Log($"[Multiplayer] Player '{info.PlayerName}' joined with {info.ModName} v{info.ModVersion}");

            CheckForConflicts(info);
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] Error in OnHandshakeReceived: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when a player disconnects.
    /// </summary>
    public static void OnPlayerDisconnected(int entityId)
    {
        try
        {
            if (_playerMods.TryRemove(entityId, out var info))
            {
                ProxiCraft.LogDebug($"[Multiplayer] Player '{info.PlayerName}' disconnected");
            }
        }
        catch
        {
            // Silent fail - cleanup is best-effort
        }
    }

    /// <summary>
    /// Checks for conflicts. Logs warnings but never throws.
    /// </summary>
    private static void CheckForConflicts(PlayerModInfo remotePlayer)
    {
        try
        {
            if (remotePlayer.ModName == ProxiCraft.MOD_NAME)
            {
                if (remotePlayer.ModVersion != ProxiCraft.MOD_VERSION)
                {
                    ProxiCraft.LogWarning($"[Multiplayer] Version mismatch: '{remotePlayer.PlayerName}' has v{remotePlayer.ModVersion}, local is v{ProxiCraft.MOD_VERSION}");
                }
                return;
            }

            // Different container mod - log warning
            ProxiCraft.LogWarning("======================================================================");
            ProxiCraft.LogWarning($"[Multiplayer Conflict] POTENTIAL CTD WARNING!");
            ProxiCraft.LogWarning($"  Player '{remotePlayer.PlayerName}' is using: {remotePlayer.ModName} v{remotePlayer.ModVersion}");
            ProxiCraft.LogWarning($"  Local player is using: {ProxiCraft.MOD_NAME} v{ProxiCraft.MOD_VERSION}");
            ProxiCraft.LogWarning($"  Different container mods may cause crashes.");
            ProxiCraft.LogWarning("======================================================================");
        }
        catch
        {
            // Silent fail - conflict detection is best-effort
        }
    }

    /// <summary>
    /// Gets the list of conflicting mod identifiers.
    /// </summary>
    public static string[] GetConflictingModIdentifiers() => ConflictingModIdentifiers;

    /// <summary>
    /// Gets all tracked player mod info (thread-safe copy).
    /// </summary>
    public static Dictionary<int, PlayerModInfo> GetTrackedPlayers()
    {
        try
        {
            return new Dictionary<int, PlayerModInfo>(_playerMods);
        }
        catch
        {
            return new Dictionary<int, PlayerModInfo>();
        }
    }

    /// <summary>
    /// Checks if any tracked players have conflicting mods.
    /// </summary>
    public static bool HasAnyConflicts()
    {
        try
        {
            return _playerMods.Values.Any(p => p.ModName != ProxiCraft.MOD_NAME);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clears all tracked player data and resets multiplayer state.
    /// Called when leaving a server or starting a new game.
    /// </summary>
    public static void Clear()
    {
        try
        {
            _playerMods.Clear();
            _handshakeSentTime = null;
            _serverResponseReceived = false;
            _serverWarningShown = false;
            _isMultiplayerSession = false;
            _multiplayerUnlocked = false;
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Called when entering a multiplayer session. Enables the safety lock.
    /// Mod functionality will be disabled until server confirms ProxiCraft.
    /// </summary>
    public static void OnMultiplayerSessionStart()
    {
        try
        {
            _isMultiplayerSession = true;
            _multiplayerUnlocked = false;
            ProxiCraft.Log("[Multiplayer] Joined server - mod functionality locked until server confirmation");
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Checks if mod functionality should be allowed.
    /// Returns true for single-player, or if multiplayer server has confirmed ProxiCraft.
    /// </summary>
    public static bool IsModAllowed()
    {
        try
        {
            // Single-player: always allowed
            if (!_isMultiplayerSession)
                return true;

            // Multiplayer: only allowed if server confirmed
            return _multiplayerUnlocked;
        }
        catch
        {
            // On error, default to allowed (don't break single-player)
            return true;
        }
    }

    /// <summary>
    /// Gets the reason why mod is currently locked (for UI/logging).
    /// Returns null if mod is allowed.
    /// </summary>
    public static string GetLockReason()
    {
        if (!_isMultiplayerSession)
            return null;

        if (_multiplayerUnlocked)
            return null;

        if (_serverWarningShown)
            return "Server does not have ProxiCraft installed";

        return "Waiting for server confirmation...";
    }

    /// <summary>
    /// Called when we send a handshake to the server. Starts the timeout timer.
    /// </summary>
    public static void OnHandshakeSent()
    {
        try
        {
            _handshakeSentTime = DateTime.Now;
            _serverResponseReceived = false;
            _serverWarningShown = false;
            ProxiCraft.LogDebug("[Multiplayer] Handshake sent, waiting for server response...");
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Called when we receive ANY handshake response (from server or broadcast).
    /// This confirms the server has ProxiCraft installed and UNLOCKS mod functionality.
    /// </summary>
    public static void OnServerResponseReceived()
    {
        try
        {
            _serverResponseReceived = true;
            
            // UNLOCK mod functionality - server has ProxiCraft!
            if (_isMultiplayerSession && !_multiplayerUnlocked)
            {
                _multiplayerUnlocked = true;
                ProxiCraft.Log("[Multiplayer] Server confirmed ProxiCraft - mod functionality UNLOCKED");
            }
            else
            {
                ProxiCraft.LogDebug("[Multiplayer] Server response received - ProxiCraft confirmed on server");
            }
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Checks if server response timed out. Call this periodically (e.g., every few seconds).
    /// Returns true if warning was shown (so caller knows not to check again).
    /// If timeout, mod stays LOCKED to prevent CTD.
    /// </summary>
    public static bool CheckServerResponseTimeout()
    {
        try
        {
            // Already received response or already warned
            if (_serverResponseReceived || _serverWarningShown)
                return _serverWarningShown;

            // No handshake sent yet
            if (!_handshakeSentTime.HasValue)
                return false;

            // Check if timeout exceeded
            var elapsed = (DateTime.Now - _handshakeSentTime.Value).TotalSeconds;
            if (elapsed < SERVER_RESPONSE_TIMEOUT_SECONDS)
                return false;

            // Timeout! Show warning - mod stays LOCKED
            _serverWarningShown = true;

            ProxiCraft.Log("======================================================================");
            ProxiCraft.Log("[Multiplayer] ProxiCraft DISABLED - Server does not have it installed");
            ProxiCraft.Log("----------------------------------------------------------------------");
            ProxiCraft.Log("  The server does not appear to have ProxiCraft installed.");
            ProxiCraft.Log("  ");
            ProxiCraft.Log("  To prevent crashes, ProxiCraft functionality is DISABLED.");
            ProxiCraft.Log("  You can still play, but container features won't work.");
            ProxiCraft.Log("  ");
            ProxiCraft.Log("  TO FIX:");
            ProxiCraft.Log("  1. Install ProxiCraft on the server (same version as client)");
            ProxiCraft.Log("  2. OR if server runs Beyond Storage 2 or another container mod,");
            ProxiCraft.Log("     use that mod on your client instead - don't mix container mods.");
            ProxiCraft.Log("======================================================================");

            // Open the F1 console so the user sees the warning
            OpenConsoleWithWarning();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets whether the server has been confirmed to have ProxiCraft.
    /// </summary>
    public static bool IsServerConfirmed => _serverResponseReceived;

    /// <summary>
    /// Gets whether we're waiting for server response.
    /// </summary>
    public static bool IsWaitingForServer => _handshakeSentTime.HasValue && !_serverResponseReceived && !_serverWarningShown;

    /// <summary>
    /// Gets whether this is a multiplayer session.
    /// </summary>
    public static bool IsMultiplayerSession => _isMultiplayerSession;

    /// <summary>
    /// Gets whether mod is unlocked for multiplayer.
    /// </summary>
    public static bool IsMultiplayerUnlocked => _multiplayerUnlocked;

    /// <summary>
    /// Opens the F1 console window so the user sees the multiplayer warning.
    /// </summary>
    private static void OpenConsoleWithWarning()
    {
        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                return;

            var playerUI = LocalPlayerUI.GetUIForPlayer(player);
            if (playerUI?.nguiWindowManager?.WindowManager == null)
                return;

            // Open the console window (non-modal so gameplay isn't blocked)
            playerUI.nguiWindowManager.WindowManager.OpenIfNotOpen(GUIWindowConsole.ID, _bModal: false);
        }
        catch
        {
            // Silent fail - opening console is best-effort
        }
    }
}

/// <summary>
/// Network packet for ProxiCraft handshake in multiplayer.
///
/// STABILITY GUARANTEES:
/// - All read/write operations use defensive null checks
/// - ProcessPackage is wrapped in try-catch
/// - Packet processing failure cannot affect other packets or gameplay
/// </summary>
internal class NetPackagePCHandshake : NetPackage
{
    public int senderEntityId;
    public string senderName = "";
    public string modName = "";
    public string modVersion = "";
    public string detectedConflicts = "";

    public NetPackagePCHandshake Setup(int entityId, string playerName, string modNameParam, string modVersionParam, IEnumerable<string> conflicts)
    {
        senderEntityId = entityId;
        senderName = playerName ?? "Unknown";
        modName = modNameParam ?? "Unknown";
        modVersion = modVersionParam ?? "0.0.0";
        detectedConflicts = conflicts != null ? string.Join(",", conflicts) : "";
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        try
        {
            var reader = (BinaryReader)(object)_br;
            senderEntityId = reader.ReadInt32();
            senderName = reader.ReadString() ?? "";
            modName = reader.ReadString() ?? "";
            modVersion = reader.ReadString() ?? "";
            detectedConflicts = reader.ReadString() ?? "";
        }
        catch
        {
            // Failed to read - use defaults
            senderName = "";
            modName = "";
            modVersion = "";
            detectedConflicts = "";
        }
    }

    public override void write(PooledBinaryWriter _bw)
    {
        try
        {
            base.write(_bw);
            var writer = (BinaryWriter)(object)_bw;
            writer.Write(senderEntityId);
            writer.Write(senderName ?? "");
            writer.Write(modName ?? "");
            writer.Write(modVersion ?? "");
            writer.Write(detectedConflicts ?? "");
        }
        catch
        {
            // Write failed - packet will be malformed but won't crash
        }
    }

    public override int GetLength()
    {
        return sizeof(int) + 200; // Approximate
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        // Early exit if mod disabled
        if (ProxiCraft.Config?.modEnabled != true)
            return;

        try
        {
            var conflicts = string.IsNullOrEmpty(detectedConflicts)
                ? new List<string>()
                : detectedConflicts.Split(',').ToList();

            // Mark that we received a response - server has ProxiCraft!
            // This confirms the server understands our handshake packets.
            MultiplayerModTracker.OnServerResponseReceived();

            MultiplayerModTracker.OnHandshakeReceived(senderEntityId, senderName, modName, modVersion, conflicts);

            // Server broadcasts to other clients
            if (SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer == true)
            {
                BroadcastToOtherClients(conflicts);
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - packet processing must not crash
            ProxiCraft.LogDebug($"[Handshake] ProcessPackage error: {ex.Message}");
        }
    }

    private void BroadcastToOtherClients(List<string> conflicts)
    {
        try
        {
            var packet = NetPackageManager.GetPackage<NetPackagePCHandshake>()
                .Setup(senderEntityId, senderName, modName, modVersion, conflicts);

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                (NetPackage)(object)packet,
                false,           // Don't send to server
                senderEntityId,  // Exclude sender
                -1, -1, null, 192, false);
        }
        catch
        {
            // Broadcast failed - not critical
        }
    }
}
