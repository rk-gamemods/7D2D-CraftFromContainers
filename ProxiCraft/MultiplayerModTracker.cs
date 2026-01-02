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
/// MULTIPLAYER SAFETY LOCK (CLIENT-SIDE):
/// - In multiplayer, mod functionality is DISABLED by default until server confirms ProxiCraft
/// - Single-player games bypass this check entirely (no lock needed)
/// - When client joins server, sends handshake and waits for response
/// - If server responds: unlock mod functionality
/// - If timeout (server doesn't have ProxiCraft): keep locked + show warning
/// - This prevents CTD from client/server state mismatch
///
/// HOST-SIDE SAFETY LOCK ("GUILTY UNTIL PROVEN INNOCENT"):
/// - When ANY client connects, mod is IMMEDIATELY disabled for the host
/// - This prevents crashes during the verification window (typically ~100-300ms)
/// - Client must send handshake to prove they have ProxiCraft installed
/// - Only after ALL clients are verified does the mod re-enable
/// - If timeout occurs before handshake, the offending player is identified
/// - Mod auto-re-enables when all unverified clients disconnect
/// 
/// CONFIGURABLE SAFETY:
/// - multiplayerImmediateLock (default: true) - Lock mod immediately when client connects
/// - multiplayerHandshakeTimeoutSeconds (default: 10) - Timeout before confirming no mod
/// - Set multiplayerImmediateLock=false only on trusted/moderated servers (honor system)
/// </summary>
public static class MultiplayerModTracker
{
    // Multiplayer safety lock - mod is disabled until server confirms ProxiCraft
    private static bool _isMultiplayerSession;
    private static bool _multiplayerUnlocked;

    // Server response tracking (for clients)
    private static DateTime? _handshakeSentTime;
    private static bool _serverResponseReceived;
    private static bool _serverWarningShown;
    
    // Configurable timeout - read from config, with fallback
    private static float GetHandshakeTimeout() => 
        Math.Max(3f, Math.Min(30f, ProxiCraft.Config?.multiplayerHandshakeTimeoutSeconds ?? 10f));

    // Host-side safety lock - "Guilty Until Proven Innocent"
    private static bool _isHosting;
    private static bool _hostSafetyLockTriggered;      // Confirmed client without mod
    private static string _hostLockCulprit;            // Name of confirmed bad client
    private static bool _immediatelyLockMod;           // Lock active while ANY client unverified
    private static int _unverifiedClientCount;         // Number of clients pending verification
    private static bool _immediateLockEnabled;         // Cached config setting for this session
    
    // Track pending and verified clients
    private static readonly ConcurrentDictionary<int, PendingClientInfo> _pendingClients = 
        new ConcurrentDictionary<int, PendingClientInfo>();
    private static readonly ConcurrentDictionary<int, bool> _verifiedClients =
        new ConcurrentDictionary<int, bool>();
    private static readonly ConcurrentDictionary<string, DateTime> _pendingConnections =
        new ConcurrentDictionary<string, DateTime>();

    /// <summary>
    /// Information about a client waiting to confirm ProxiCraft installation
    /// </summary>
    private class PendingClientInfo
    {
        public int EntityId { get; set; }
        public string PlayerName { get; set; }
        public string ConnectionId { get; set; }
        public DateTime JoinTime { get; set; }
        public bool HandshakeReceived { get; set; }
    }

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

    #region Host-Side Safety Lock

    /// <summary>
    /// Gets whether immediate lock is enabled (from config).
    /// </summary>
    public static bool IsImmediateLockConfigEnabled => _immediateLockEnabled;

    /// <summary>
    /// Called when we start hosting a multiplayer game.
    /// Registers event handlers to track client joins.
    /// </summary>
    public static void OnStartHosting()
    {
        try
        {
            _isHosting = true;
            _hostSafetyLockTriggered = false;
            _hostLockCulprit = null;
            _immediatelyLockMod = false;
            _unverifiedClientCount = 0;
            _pendingClients.Clear();
            _verifiedClients.Clear();
            _pendingConnections.Clear();
            
            // Cache config setting for this session
            _immediateLockEnabled = ProxiCraft.Config?.multiplayerImmediateLock ?? true;
            var timeoutSeconds = GetHandshakeTimeout();
            
            // LOG THE SAFETY SETTING PROMINENTLY - this helps diagnose CTD in logs
            ProxiCraft.Log("======================================================================");
            ProxiCraft.Log("[Multiplayer] HOST MODE STARTING - Safety Settings:");
            ProxiCraft.Log($"  Immediate Client Lock: {(_immediateLockEnabled ? "ENABLED (safe)" : ">>> DISABLED <<< (honor system)")}");
            ProxiCraft.Log($"  Handshake Timeout: {timeoutSeconds} seconds");
            if (!_immediateLockEnabled)
            {
                ProxiCraft.Log("  ");
                ProxiCraft.Log("  ⚠️ WARNING: Immediate lock is DISABLED!");
                ProxiCraft.Log("  If a player joins WITHOUT ProxiCraft, the server may CRASH!");
                ProxiCraft.Log("  Set multiplayerImmediateLock=true in config.json for safety.");
            }
            ProxiCraft.Log("======================================================================");
            
            // Register for player spawn events (to get entityId correlation)
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnectedEvent);
            
            // Register for EARLIEST client connection - IMMEDIATELY lock when any client connects
            // (only if immediate lock is enabled)
            if (_immediateLockEnabled)
            {
                ConnectionManager.OnClientAdded += OnClientConnected;
            }
            
            ProxiCraft.LogDebug($"[Multiplayer] Host mode enabled - ImmediateLock={_immediateLockEnabled}");
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnStartHosting error: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when we stop hosting (leave game, shutdown, etc.)
    /// </summary>
    public static void OnStopHosting()
    {
        try
        {
            _isHosting = false;
            _hostSafetyLockTriggered = false;
            _hostLockCulprit = null;
            _immediatelyLockMod = false;
            _unverifiedClientCount = 0;
            _pendingClients.Clear();
            _verifiedClients.Clear();
            _pendingConnections.Clear();
            
            // Unregister ALL event handlers
            ModEvents.PlayerSpawnedInWorld.UnregisterHandler(OnPlayerSpawnedInWorld);
            ModEvents.PlayerDisconnected.UnregisterHandler(OnPlayerDisconnectedEvent);
            ConnectionManager.OnClientAdded -= OnClientConnected;
            
            ProxiCraft.LogDebug("[Multiplayer] Host mode disabled");
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnStopHosting error: {ex.Message}");
        }
    }

    /// <summary>
    /// Called IMMEDIATELY when a client's network connection is established.
    /// This is the EARLIEST point we can detect a new client - before any game data is sent.
    /// IMMEDIATELY locks mod to prevent crashes during the verification window.
    /// </summary>
    private static void OnClientConnected(ClientInfo clientInfo)
    {
        try
        {
            if (clientInfo == null) return;
            
            // IMMEDIATELY lock the mod - "Guilty Until Proven Innocent"
            _immediatelyLockMod = true;
            _unverifiedClientCount++;
            
            // Track this connection for later correlation with entityId
            var connectionId = clientInfo.InternalId?.CombinedString ?? Guid.NewGuid().ToString();
            _pendingConnections[connectionId] = DateTime.Now;

            ProxiCraft.Log("======================================================================");
            ProxiCraft.Log("[Multiplayer] NEW CLIENT CONNECTING - Mod IMMEDIATELY LOCKED");
            ProxiCraft.Log($"  Client: {clientInfo.playerName ?? connectionId}");
            ProxiCraft.Log($"  Reason: Waiting for ProxiCraft verification handshake");
            ProxiCraft.Log($"  Unverified clients: {_unverifiedClientCount}");
            ProxiCraft.Log("======================================================================");
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnClientConnected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Event handler for when a player spawns in the world.
    /// At this point we have their entityId - correlate with connection tracking.
    /// </summary>
    private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData data)
    {
        try
        {
            // Only process if we're the server/host
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer == true)
                return;

            // Skip local player (the host)
            if (data.IsLocalPlayer)
                return;

            // Skip if client info is null
            if (data.ClientInfo == null)
                return;

            var entityId = data.EntityId;
            var playerName = data.ClientInfo.playerName ?? "Unknown";
            var connectionId = data.ClientInfo.InternalId?.CombinedString ?? "";

            // Check if we already have a handshake from this player
            if (_verifiedClients.ContainsKey(entityId) || _playerMods.ContainsKey(entityId))
            {
                ProxiCraft.LogDebug($"[Multiplayer] Player '{playerName}' already confirmed ProxiCraft");
                return;
            }

            // Add to pending clients - correlate connectionId with entityId
            var pendingInfo = new PendingClientInfo
            {
                EntityId = entityId,
                PlayerName = playerName,
                ConnectionId = connectionId,
                JoinTime = DateTime.Now,
                HandshakeReceived = false
            };

            _pendingClients[entityId] = pendingInfo;

            ProxiCraft.Log($"[Multiplayer] Player '{playerName}' spawned (entityId={entityId}) - waiting for handshake...");

            // Schedule a timeout check - if they don't respond, they're confirmed without mod
            ThreadManager.StartCoroutine(CheckClientHandshakeTimeout(entityId, playerName));
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnPlayerSpawnedInWorld error: {ex.Message}");
        }
    }

    /// <summary>
    /// Event handler for when a player disconnects.
    /// </summary>
    private static void OnPlayerDisconnectedEvent(ref ModEvents.SPlayerDisconnectedData data)
    {
        try
        {
            if (data.ClientInfo == null)
                return;

            var entityId = data.ClientInfo.entityId;
            var playerName = data.ClientInfo.playerName ?? "Unknown";
            var connectionId = data.ClientInfo.InternalId?.CombinedString ?? "";
            
            // Check if this was a verified client
            bool wasVerified = _verifiedClients.TryRemove(entityId, out _);
            
            // Remove from pending clients
            bool wasPending = _pendingClients.TryRemove(entityId, out _);
            
            // Remove from pending connections
            _pendingConnections.TryRemove(connectionId, out _);
            
            // Remove from confirmed players
            OnPlayerDisconnected(entityId);

            // If they were unverified, decrement the counter
            if (!wasVerified && wasPending)
            {
                _unverifiedClientCount = Math.Max(0, _unverifiedClientCount - 1);
                ProxiCraft.Log($"[Multiplayer] Unverified player '{playerName}' disconnected (remaining unverified: {_unverifiedClientCount})");
            }

            // If the culprit disconnected, check if we can re-enable
            if (_hostSafetyLockTriggered && _hostLockCulprit == playerName)
            {
                // Check if there are any other unconfirmed clients
                bool anyUnconfirmed = _pendingClients.Values.Any(p => !p.HandshakeReceived);
                
                if (!anyUnconfirmed && _unverifiedClientCount <= 0)
                {
                    _hostSafetyLockTriggered = false;
                    _hostLockCulprit = null;
                    _immediatelyLockMod = false;
                    ProxiCraft.Log("======================================================================");
                    ProxiCraft.Log("[Multiplayer] ProxiCraft RE-ENABLED");
                    ProxiCraft.Log($"  The player without ProxiCraft ('{playerName}') has disconnected.");
                    ProxiCraft.Log("  All remaining players have ProxiCraft - mod functionality restored!");
                    ProxiCraft.Log("======================================================================");
                    OpenConsoleWithWarning();
                }
            }
            // Also re-enable if all unverified clients are now gone
            else if (_immediatelyLockMod && _unverifiedClientCount <= 0 && !_hostSafetyLockTriggered)
            {
                _immediatelyLockMod = false;
                ProxiCraft.Log("[Multiplayer] All clients verified or disconnected - Mod RE-ENABLED");
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnPlayerDisconnectedEvent error: {ex.Message}");
        }
    }

    /// <summary>
    /// Coroutine that checks if a client sent their handshake within the timeout period.
    /// By this point they're already blocked by the immediate lock - this just confirms
    /// whether they actually have the mod or not for reporting purposes.
    /// </summary>
    private static System.Collections.IEnumerator CheckClientHandshakeTimeout(int entityId, string playerName)
    {
        // Wait for configurable timeout period (+ 2s buffer)
        var timeoutSeconds = GetHandshakeTimeout() + 2f;
        yield return new UnityEngine.WaitForSeconds(timeoutSeconds);

        try
        {
            // Check if this client is still pending (no handshake received)
            if (_pendingClients.TryGetValue(entityId, out var pendingInfo) && !pendingInfo.HandshakeReceived)
            {
                // This client is CONFIRMED to not have ProxiCraft!
                // The immediate lock already protected us - now we know for certain who caused it
                TriggerHostSafetyLock(playerName);
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] CheckClientHandshakeTimeout error: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks a client as having confirmed ProxiCraft (received their handshake).
    /// This is the "innocent verdict" - they can now be trusted.
    /// </summary>
    public static void OnClientHandshakeReceived(int entityId)
    {
        try
        {
            string playerName = "Unknown";
            
            if (_pendingClients.TryGetValue(entityId, out var pendingInfo))
            {
                pendingInfo.HandshakeReceived = true;
                playerName = pendingInfo.PlayerName;
                
                // Remove from pending since they're now confirmed
                _pendingClients.TryRemove(entityId, out _);
            }
            
            // Mark as verified
            _verifiedClients[entityId] = true;
            
            // Decrement unverified count (clamped to 0)
            _unverifiedClientCount = Math.Max(0, _unverifiedClientCount - 1);
            
            ProxiCraft.Log($"[Multiplayer] Player '{playerName}' confirmed ProxiCraft ✓ (Unverified remaining: {_unverifiedClientCount})");
            
            // If all clients verified and no confirmed bad clients, re-enable mod
            if (_unverifiedClientCount <= 0 && !_hostSafetyLockTriggered)
            {
                _immediatelyLockMod = false;
                ProxiCraft.Log("======================================================================");
                ProxiCraft.Log("[Multiplayer] All clients verified - Mod RE-ENABLED");
                ProxiCraft.Log("======================================================================");
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] OnClientHandshakeReceived error: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers the host-side safety lock when a client without ProxiCraft is CONFIRMED.
    /// Note: By this point, _immediatelyLockMod already protected us during verification.
    /// This method identifies and logs the culprit for the host's awareness.
    /// </summary>
    private static void TriggerHostSafetyLock(string culpritPlayerName)
    {
        if (_hostSafetyLockTriggered)
            return; // Already triggered by another client

        _hostSafetyLockTriggered = true;
        _hostLockCulprit = culpritPlayerName;
        // _immediatelyLockMod stays true - redundant safety

        ProxiCraft.Log("======================================================================");
        ProxiCraft.Log("[Multiplayer] ProxiCraft DISABLED - Client WITHOUT mod CONFIRMED!");
        ProxiCraft.Log("----------------------------------------------------------------------");
        ProxiCraft.Log($"  CULPRIT: '{culpritPlayerName}' does NOT have ProxiCraft installed!");
        ProxiCraft.Log("  ");
        ProxiCraft.Log("  ProxiCraft was already locked when they connected.");
        ProxiCraft.Log("  This confirms they do not have the mod - crash prevented!");
        ProxiCraft.Log("  ");
        ProxiCraft.Log("  TO FIX:");
        ProxiCraft.Log($"  1. Ask '{culpritPlayerName}' to install ProxiCraft (same version as host)");
        ProxiCraft.Log($"  2. OR kick '{culpritPlayerName}' - mod will re-enable when they leave");
        ProxiCraft.Log("  3. OR all players must use the same container mod (or none)");
        ProxiCraft.Log("======================================================================");

        OpenConsoleWithWarning();
    }

    /// <summary>
    /// Gets whether the host safety lock is triggered (confirmed client without mod).
    /// </summary>
    public static bool IsHostSafetyLockTriggered => _hostSafetyLockTriggered;

    /// <summary>
    /// Gets whether the immediate lock is active (any unverified clients).
    /// </summary>
    public static bool IsImmediatelyLocked => _immediatelyLockMod;

    /// <summary>
    /// Gets the number of unverified clients.
    /// </summary>
    public static int UnverifiedClientCount => _unverifiedClientCount;

    /// <summary>
    /// Gets the name of the player who triggered the host safety lock.
    /// </summary>
    public static string HostLockCulprit => _hostLockCulprit;

    /// <summary>
    /// Gets whether we're currently hosting a multiplayer game.
    /// </summary>
    public static bool IsHosting => _isHosting;

    #endregion

    #region Config Sync
    
    private static bool _configSyncReceived;
    
    /// <summary>
    /// Gets whether config sync has been received from server.
    /// </summary>
    public static bool IsConfigSynced => _configSyncReceived;
    
    /// <summary>
    /// Called when client receives config sync from server.
    /// </summary>
    public static void OnConfigSyncReceived()
    {
        _configSyncReceived = true;
        ProxiCraft.LogDebug("[Multiplayer] Config sync complete - using server settings");
    }
    
    /// <summary>
    /// Sends server config to a specific client.
    /// Called when server receives a handshake from a client.
    /// </summary>
    public static void SendConfigToClient(ClientInfo clientInfo)
    {
        try
        {
            if (clientInfo == null) return;
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer == true) return;
            
            var config = ProxiCraft.Config;
            if (config == null) return;
            
            var packet = NetPackageManager.GetPackage<NetPackagePCConfigSync>().Setup(config);
            clientInfo.SendPackage((NetPackage)(object)packet);
            
            ProxiCraft.LogDebug($"[Multiplayer] Sent config sync to client '{clientInfo.playerName}'");
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Multiplayer] SendConfigToClient error: {ex.Message}");
        }
    }
    
    #endregion

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

            // If we're the host/server, mark this client as confirmed and send them our config
            if (_isHosting || SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer == true)
            {
                OnClientHandshakeReceived(entityId);
                
                // Send server config to this client so they use the same settings
                var clientInfo = SingletonMonoBehaviour<ConnectionManager>.Instance?.Clients?.ForEntityId(entityId);
                if (clientInfo != null)
                {
                    SendConfigToClient(clientInfo);
                }
            }

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
            _pendingClients.Clear();
            _verifiedClients.Clear();
            _pendingConnections.Clear();
            _handshakeSentTime = null;
            _serverResponseReceived = false;
            _serverWarningShown = false;
            _isMultiplayerSession = false;
            _multiplayerUnlocked = false;
            _isHosting = false;
            _hostSafetyLockTriggered = false;
            _hostLockCulprit = null;
            _immediatelyLockMod = false;
            _unverifiedClientCount = 0;
            _configSyncReceived = false;
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
    /// Returns true for single-player, or if multiplayer is safe.
    /// </summary>
    public static bool IsModAllowed()
    {
        try
        {
            // IMMEDIATE lock for any unverified clients - highest priority ("Guilty Until Proven Innocent")
            if (_immediatelyLockMod)
                return false;

            // Host safety lock triggered (confirmed client without mod)
            if (_hostSafetyLockTriggered)
                return false;

            // Single-player: always allowed
            if (!_isMultiplayerSession && !_isHosting)
                return true;

            // Hosting: allowed if no immediate lock (checked above)
            if (_isHosting)
                return true;

            // Client multiplayer: only allowed if server confirmed
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
        // Immediate lock takes priority - client(s) connecting
        if (_immediatelyLockMod)
        {
            if (_hostSafetyLockTriggered)
                return $"Player '{_hostLockCulprit}' does not have ProxiCraft (verified)";
            return $"Client(s) connecting - verifying ProxiCraft ({_unverifiedClientCount} pending)";
        }

        // Host safety lock (confirmed client without mod)
        if (_hostSafetyLockTriggered)
            return $"Player '{_hostLockCulprit}' does not have ProxiCraft";

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

            // Check if timeout exceeded (use configurable timeout)
            var elapsed = (DateTime.Now - _handshakeSentTime.Value).TotalSeconds;
            if (elapsed < GetHandshakeTimeout())
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

/// <summary>
/// Network packet for syncing server config to clients.
/// Server sends this to clients after handshake to ensure all players use the same settings.
///
/// CRITICAL FOR STABILITY:
/// - All clients MUST use server's range setting to avoid item visibility desync
/// - All clients MUST use server's feature toggles to avoid state mismatch
/// - Client settings are overridden with server settings on receipt
/// - Differences are logged to help diagnose issues
/// </summary>
internal class NetPackagePCConfigSync : NetPackage
{
    // Core settings that affect item visibility/behavior
    public float range;
    public bool pullFromVehicles;
    public bool pullFromDrones;
    public bool pullFromDewCollectors;
    public bool pullFromWorkstationOutputs;
    public bool allowLockedContainers;
    public bool respectLockedSlots;
    
    // Feature toggles
    public bool enableForCrafting;
    public bool enableForReload;
    public bool enableForRefuel;
    public bool enableForTrader;
    public bool enableForRepairAndUpgrade;
    public bool enableForQuests;
    public bool enableForPainting;
    public bool enableForLockpicking;
    public bool enableForGeneratorRefuel;
    public bool enableForItemRepair;
    public bool enableHudAmmoCounter;
    public bool enableRecipeTrackerUpdates;
    
    // Storage priority as comma-separated "key:value" pairs
    public string storagePriorityData = "";

    public NetPackagePCConfigSync Setup(ModConfig config)
    {
        if (config == null) return this;
        
        range = config.range;
        pullFromVehicles = config.pullFromVehicles;
        pullFromDrones = config.pullFromDrones;
        pullFromDewCollectors = config.pullFromDewCollectors;
        pullFromWorkstationOutputs = config.pullFromWorkstationOutputs;
        allowLockedContainers = config.allowLockedContainers;
        respectLockedSlots = config.respectLockedSlots;
        
        enableForCrafting = config.enableForCrafting;
        enableForReload = config.enableForReload;
        enableForRefuel = config.enableForRefuel;
        enableForTrader = config.enableForTrader;
        enableForRepairAndUpgrade = config.enableForRepairAndUpgrade;
        enableForQuests = config.enableForQuests;
        enableForPainting = config.enableForPainting;
        enableForLockpicking = config.enableForLockpicking;
        enableForGeneratorRefuel = config.enableForGeneratorRefuel;
        enableForItemRepair = config.enableForItemRepair;
        enableHudAmmoCounter = config.enableHudAmmoCounter;
        enableRecipeTrackerUpdates = config.enableRecipeTrackerUpdates;
        
        // Serialize storage priority
        if (config.storagePriority != null)
        {
            var pairs = new List<string>();
            foreach (var kvp in config.storagePriority)
            {
                pairs.Add($"{kvp.Key}:{kvp.Value}");
            }
            storagePriorityData = string.Join(",", pairs);
        }
        
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        try
        {
            var reader = (BinaryReader)(object)_br;
            range = reader.ReadSingle();
            pullFromVehicles = reader.ReadBoolean();
            pullFromDrones = reader.ReadBoolean();
            pullFromDewCollectors = reader.ReadBoolean();
            pullFromWorkstationOutputs = reader.ReadBoolean();
            allowLockedContainers = reader.ReadBoolean();
            respectLockedSlots = reader.ReadBoolean();
            
            enableForCrafting = reader.ReadBoolean();
            enableForReload = reader.ReadBoolean();
            enableForRefuel = reader.ReadBoolean();
            enableForTrader = reader.ReadBoolean();
            enableForRepairAndUpgrade = reader.ReadBoolean();
            enableForQuests = reader.ReadBoolean();
            enableForPainting = reader.ReadBoolean();
            enableForLockpicking = reader.ReadBoolean();
            enableForGeneratorRefuel = reader.ReadBoolean();
            enableForItemRepair = reader.ReadBoolean();
            enableHudAmmoCounter = reader.ReadBoolean();
            enableRecipeTrackerUpdates = reader.ReadBoolean();
            
            storagePriorityData = reader.ReadString() ?? "";
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[ConfigSync] Read error: {ex.Message}");
        }
    }

    public override void write(PooledBinaryWriter _bw)
    {
        try
        {
            base.write(_bw);
            var writer = (BinaryWriter)(object)_bw;
            writer.Write(range);
            writer.Write(pullFromVehicles);
            writer.Write(pullFromDrones);
            writer.Write(pullFromDewCollectors);
            writer.Write(pullFromWorkstationOutputs);
            writer.Write(allowLockedContainers);
            writer.Write(respectLockedSlots);
            
            writer.Write(enableForCrafting);
            writer.Write(enableForReload);
            writer.Write(enableForRefuel);
            writer.Write(enableForTrader);
            writer.Write(enableForRepairAndUpgrade);
            writer.Write(enableForQuests);
            writer.Write(enableForPainting);
            writer.Write(enableForLockpicking);
            writer.Write(enableForGeneratorRefuel);
            writer.Write(enableForItemRepair);
            writer.Write(enableHudAmmoCounter);
            writer.Write(enableRecipeTrackerUpdates);
            
            writer.Write(storagePriorityData ?? "");
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[ConfigSync] Write error: {ex.Message}");
        }
    }

    public override int GetLength()
    {
        return sizeof(float) + sizeof(bool) * 18 + 200; // Approximate
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        try
        {
            // Only clients process this - servers send it
            if (SingletonMonoBehaviour<ConnectionManager>.Instance?.IsServer == true)
                return;

            var localConfig = ProxiCraft.Config;
            if (localConfig == null)
            {
                ProxiCraft.LogWarning("[ConfigSync] Cannot apply server config - local config is null");
                return;
            }

            ProxiCraft.Log("======================================================================");
            ProxiCraft.Log("[Multiplayer] Received server configuration - synchronizing settings...");
            
            var differences = new List<string>();
            
            // Check and apply each setting, logging differences
            if (Math.Abs(localConfig.range - range) > 0.01f)
                differences.Add($"  range: {localConfig.range} → {range}");
            if (localConfig.pullFromVehicles != pullFromVehicles)
                differences.Add($"  pullFromVehicles: {localConfig.pullFromVehicles} → {pullFromVehicles}");
            if (localConfig.pullFromDrones != pullFromDrones)
                differences.Add($"  pullFromDrones: {localConfig.pullFromDrones} → {pullFromDrones}");
            if (localConfig.pullFromDewCollectors != pullFromDewCollectors)
                differences.Add($"  pullFromDewCollectors: {localConfig.pullFromDewCollectors} → {pullFromDewCollectors}");
            if (localConfig.pullFromWorkstationOutputs != pullFromWorkstationOutputs)
                differences.Add($"  pullFromWorkstationOutputs: {localConfig.pullFromWorkstationOutputs} → {pullFromWorkstationOutputs}");
            if (localConfig.allowLockedContainers != allowLockedContainers)
                differences.Add($"  allowLockedContainers: {localConfig.allowLockedContainers} → {allowLockedContainers}");
            if (localConfig.respectLockedSlots != respectLockedSlots)
                differences.Add($"  respectLockedSlots: {localConfig.respectLockedSlots} → {respectLockedSlots}");
            if (localConfig.enableForCrafting != enableForCrafting)
                differences.Add($"  enableForCrafting: {localConfig.enableForCrafting} → {enableForCrafting}");
            if (localConfig.enableForReload != enableForReload)
                differences.Add($"  enableForReload: {localConfig.enableForReload} → {enableForReload}");
            if (localConfig.enableForRefuel != enableForRefuel)
                differences.Add($"  enableForRefuel: {localConfig.enableForRefuel} → {enableForRefuel}");
            if (localConfig.enableForTrader != enableForTrader)
                differences.Add($"  enableForTrader: {localConfig.enableForTrader} → {enableForTrader}");
            if (localConfig.enableForRepairAndUpgrade != enableForRepairAndUpgrade)
                differences.Add($"  enableForRepairAndUpgrade: {localConfig.enableForRepairAndUpgrade} → {enableForRepairAndUpgrade}");
            if (localConfig.enableForQuests != enableForQuests)
                differences.Add($"  enableForQuests: {localConfig.enableForQuests} → {enableForQuests}");
            if (localConfig.enableForPainting != enableForPainting)
                differences.Add($"  enableForPainting: {localConfig.enableForPainting} → {enableForPainting}");
            if (localConfig.enableForLockpicking != enableForLockpicking)
                differences.Add($"  enableForLockpicking: {localConfig.enableForLockpicking} → {enableForLockpicking}");
            if (localConfig.enableForGeneratorRefuel != enableForGeneratorRefuel)
                differences.Add($"  enableForGeneratorRefuel: {localConfig.enableForGeneratorRefuel} → {enableForGeneratorRefuel}");
            if (localConfig.enableForItemRepair != enableForItemRepair)
                differences.Add($"  enableForItemRepair: {localConfig.enableForItemRepair} → {enableForItemRepair}");
            if (localConfig.enableHudAmmoCounter != enableHudAmmoCounter)
                differences.Add($"  enableHudAmmoCounter: {localConfig.enableHudAmmoCounter} → {enableHudAmmoCounter}");
            if (localConfig.enableRecipeTrackerUpdates != enableRecipeTrackerUpdates)
                differences.Add($"  enableRecipeTrackerUpdates: {localConfig.enableRecipeTrackerUpdates} → {enableRecipeTrackerUpdates}");
            
            // Apply all settings from server
            localConfig.range = range;
            localConfig.pullFromVehicles = pullFromVehicles;
            localConfig.pullFromDrones = pullFromDrones;
            localConfig.pullFromDewCollectors = pullFromDewCollectors;
            localConfig.pullFromWorkstationOutputs = pullFromWorkstationOutputs;
            localConfig.allowLockedContainers = allowLockedContainers;
            localConfig.respectLockedSlots = respectLockedSlots;
            
            localConfig.enableForCrafting = enableForCrafting;
            localConfig.enableForReload = enableForReload;
            localConfig.enableForRefuel = enableForRefuel;
            localConfig.enableForTrader = enableForTrader;
            localConfig.enableForRepairAndUpgrade = enableForRepairAndUpgrade;
            localConfig.enableForQuests = enableForQuests;
            localConfig.enableForPainting = enableForPainting;
            localConfig.enableForLockpicking = enableForLockpicking;
            localConfig.enableForGeneratorRefuel = enableForGeneratorRefuel;
            localConfig.enableForItemRepair = enableForItemRepair;
            localConfig.enableHudAmmoCounter = enableHudAmmoCounter;
            localConfig.enableRecipeTrackerUpdates = enableRecipeTrackerUpdates;
            
            // Parse and apply storage priority
            if (!string.IsNullOrEmpty(storagePriorityData))
            {
                var newPriority = new Dictionary<string, string>();
                foreach (var pair in storagePriorityData.Split(','))
                {
                    var parts = pair.Split(':');
                    if (parts.Length == 2)
                    {
                        newPriority[parts[0]] = parts[1];
                    }
                }
                if (newPriority.Count > 0)
                {
                    // Check for priority differences
                    bool priorityChanged = false;
                    if (localConfig.storagePriority == null || localConfig.storagePriority.Count != newPriority.Count)
                    {
                        priorityChanged = true;
                    }
                    else
                    {
                        foreach (var kvp in newPriority)
                        {
                            if (!localConfig.storagePriority.TryGetValue(kvp.Key, out var localVal) || localVal != kvp.Value)
                            {
                                priorityChanged = true;
                                break;
                            }
                        }
                    }
                    
                    if (priorityChanged)
                    {
                        differences.Add($"  storagePriority: (changed to server's priority order)");
                    }
                    
                    localConfig.storagePriority = newPriority;
                }
            }
            
            // Log results
            if (differences.Count > 0)
            {
                ProxiCraft.Log($"Settings changed from server ({differences.Count} differences):");
                foreach (var diff in differences)
                {
                    ProxiCraft.Log(diff);
                }
            }
            else
            {
                ProxiCraft.Log("All settings match server - no changes needed.");
            }
            ProxiCraft.Log("======================================================================");
            
            // Mark that config sync is complete
            MultiplayerModTracker.OnConfigSyncReceived();
        }
        catch (Exception ex)
        {
            ProxiCraft.LogWarning($"[ConfigSync] ProcessPackage error: {ex.Message}");
        }
    }
}
