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
/// </summary>
public static class MultiplayerModTracker
{
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
    /// Clears all tracked player data.
    /// </summary>
    public static void Clear()
    {
        try
        {
            _playerMods.Clear();
        }
        catch
        {
            // Silent fail
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
