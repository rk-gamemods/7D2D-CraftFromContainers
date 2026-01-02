using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using Platform;
using UnityEngine;

namespace ProxiCraft;

/// <summary>
/// Main mod class for ProxiCraft - 7 Days to Die Mod.
/// 
/// PURPOSE: Allows crafting, reloading, refueling, and repairs using items from nearby storage containers.
/// 
/// ARCHITECTURE:
/// - Uses Harmony 2.x for runtime patching of game methods
/// - ContainerManager.cs handles container discovery and item operations
/// - ModConfig.cs holds user-configurable settings
/// - SafePatcher.cs provides robust patching with error handling
/// 
/// KEY DESIGN DECISIONS:
/// 1. ADDITIVE PATCHING: All patches ADD to existing functionality rather than replacing it.
///    This ensures compatibility with backpack mods and other inventory modifications.
///    
/// 2. LOW PRIORITY: Patches use [HarmonyPriority(Priority.Low)] to run after other mods,
///    allowing us to ADD our container items to whatever inventory structure exists.
///    
/// 3. POSTFIX PREFERRED: Most patches use Postfix to modify results after vanilla code runs.
///    This is safer and more compatible than Prefix or Transpiler approaches.
///    
/// 4. SAFE EVENTS: For challenge tracker integration, we fire DragAndDropItemChanged
///    (which challenges already listen to) rather than OnBackpackItemsChangedInternal
///    (which causes item duplication during transfers).
///
/// CHALLENGE TRACKER INTEGRATION (Fix #8e):
/// The challenge system (ChallengeObjectiveGather) counts items when DragAndDropItemChanged fires.
/// Our patches:
/// 1. Cache open container reference in LootContainer_OnOpen_Patch
/// 2. Fire DragAndDropItemChanged when container slots change (LootContainer_SlotChanged_Patch)  
/// 3. Fire DragAndDropItemChanged when inventory slots change while container is open (ItemStack_SlotChanged_Patch)
/// 4. Add container items to the 'Current' count in HandleUpdatingCurrent_Patch
///
/// CRITICAL LESSONS LEARNED:
/// - NEVER fire OnBackpackItemsChangedInternal during item transfers - causes duplication!
/// - Only fire events in Postfix (after transfer completes)
/// - The challenge 'Current' field must be SET, not just calculated
/// - Open containers need special handling via cached reference for live item counting
///
/// See RESEARCH_NOTES.md and INVENTORY_EVENTS_GUIDE.md for detailed documentation.
/// </summary>
public class ProxiCraft : IModApi
{
    // Mod metadata
    public const string MOD_NAME = "ProxiCraft";
    public const string MOD_VERSION = "1.2.1";
    
    // Static references
    private static ProxiCraft _instance;
    private static Mod _mod;
    public static ModConfig Config { get; private set; }
    
    // Harmony instance ID (unique to prevent conflicts)
    private static readonly string HarmonyId = "rkgamemods.proxicraft";

    // Config file watcher for runtime config changes
    private static FileSystemWatcher _configWatcher;
    private static bool _reloadPending;

    #region IModApi Implementation
    
    public void InitMod(Mod modInstance)
    {
        _instance = this;
        _mod = modInstance;
        
        // Force log at very start to verify logging works
        Debug.Log("[ProxiCraft] InitMod starting...");
        
        LoadConfig();
        InitConfigWatcher();

        // Initialize storage priority ordering (reads from config once at startup)
        StoragePriority.Initialize(Config);

        // Initialize network diagnostics (clears log file for fresh session)
        NetworkDiagnostics.Init();

        // Run compatibility checks first
        ModCompatibility.ScanForConflicts();
        
        try
        {
            var harmony = new Harmony(HarmonyId);
            
            Debug.Log("[ProxiCraft] About to apply patches...");
            
            // Use safe patching that records successes and failures
            int patchCount = SafePatcher.ApplyPatches(harmony, Assembly.GetExecutingAssembly());
            
            Debug.Log($"[ProxiCraft] {MOD_NAME} v{MOD_VERSION} initialized with {patchCount} patches");

            // Run startup health check to validate all features
            StartupHealthCheck.RunHealthCheck(Config);

            // Register for player spawn event to pre-warm cache and send multiplayer handshake
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);

            // Register for player disconnect to clean up multiplayer mod tracking
            ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);

            // Report any compatibility issues
            if (ModCompatibility.HasCriticalConflicts())
            {
                LogWarning("Critical mod conflicts detected! Some features may not work.");
                LogWarning("Use 'pc conflicts' console command for details.");
            }
            else if (ModCompatibility.HasAnyConflicts())
            {
                Log("Potential mod conflicts detected - use 'pc conflicts' for details.");
            }
            
            // Log diagnostic info in debug mode
            if (Config?.isDebug == true)
            {
                Log(ModCompatibility.GetDiagnosticReport());
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProxiCraft] Failed to apply Harmony patches: {ex.Message}");
            Debug.LogError($"[ProxiCraft] {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Called when player spawns in world - triggers background cache pre-warming and multiplayer handshake.
    /// </summary>
    private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData data)
    {
        // Only handle for local player
        if (!data.IsLocalPlayer)
            return;

        // Schedule cache pre-warm after a short delay (let the world fully load)
        ThreadManager.StartCoroutine(PreWarmCacheDelayed());

        // Send multiplayer handshake to announce ProxiCraft presence
        // This helps detect conflicts with other players using different container mods
        ThreadManager.StartCoroutine(SendMultiplayerHandshakeDelayed());
    }

    /// <summary>
    /// Called when any player disconnects - cleans up multiplayer mod tracking.
    /// </summary>
    private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData data)
    {
        try
        {
            // SPlayerDisconnectedData contains ClientInfo and Shutdown properties
            if (data.ClientInfo?.entityId > 0)
            {
                MultiplayerModTracker.OnPlayerDisconnected(data.ClientInfo.entityId);
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Error handling player disconnect: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Pre-warms the container cache after a short delay post-spawn.
    /// This eliminates the lag spike on first container/crafting access.
    /// </summary>
    private static System.Collections.IEnumerator PreWarmCacheDelayed()
    {
        // Wait 2 seconds for world to fully load
        yield return new WaitForSeconds(2f);

        if (Config?.modEnabled != true)
            yield break;

        try
        {
            LogDebug("Pre-warming container cache...");
            ContainerManager.PreWarmCache(Config);
            LogDebug("Container cache pre-warmed");
        }
        catch (Exception ex)
        {
            LogWarning($"Cache pre-warm failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a multiplayer handshake packet to announce ProxiCraft presence.
    /// This allows detection of conflicting mods on other players.
    /// Also initiates the multiplayer safety lock - mod is disabled until server confirms.
    /// </summary>
    private static System.Collections.IEnumerator SendMultiplayerHandshakeDelayed()
    {
        // Wait for network to be ready
        yield return new WaitForSeconds(3f);

        if (Config?.modEnabled != true)
            yield break;

        // Only send handshake in multiplayer
        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsConnected)
            yield break;

        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        bool isSinglePlayer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsSinglePlayer;

        // True single-player: no network handling needed
        if (isSinglePlayer)
        {
            LogDebug("Single-player detected - no multiplayer handling needed");
            yield break;
        }

        // If we're the server/host, enable host-side safety lock
        if (isServer)
        {
            LogDebug("Hosting multiplayer game - enabling host-side client tracking");
            MultiplayerModTracker.OnStartHosting();
            
            // Host still sends handshake so clients know the server has ProxiCraft
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player != null)
            {
                var localConflicts = ModCompatibility.GetConflicts()
                    .Select(c => c.ModName)
                    .ToList();

                var packet = NetPackageManager.GetPackage<NetPackagePCHandshake>()
                    .Setup(player.entityId, player.PlayerDisplayName, MOD_NAME, MOD_VERSION, localConflicts);

                LogDebug($"Host broadcasting ProxiCraft presence: {MOD_NAME} v{MOD_VERSION}");
                
                // Broadcast to all connected clients
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                    (NetPackage)(object)packet, false, -1, -1, -1, null, 192, false);
            }
            yield break;
        }

        // We're a client joining a server - enable client-side safety lock
        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                yield break;

            // ENABLE MULTIPLAYER SAFETY LOCK
            // Mod functionality will be disabled until server confirms ProxiCraft
            MultiplayerModTracker.OnMultiplayerSessionStart();

            // Get list of locally detected conflicting mods to include in handshake
            var localConflicts = ModCompatibility.GetConflicts()
                .Select(c => c.ModName)
                .ToList();

            var packet = NetPackageManager.GetPackage<NetPackagePCHandshake>()
                .Setup(player.entityId, player.PlayerDisplayName, MOD_NAME, MOD_VERSION, localConflicts);

            LogDebug($"Sending multiplayer handshake: {MOD_NAME} v{MOD_VERSION}");

            // Send to server (and server will broadcast to other clients)
            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                (NetPackage)(object)packet, false);

            // Start tracking for server response timeout
            MultiplayerModTracker.OnHandshakeSent();

            // Schedule a check for server response after timeout
            ThreadManager.StartCoroutine(CheckServerResponseAfterDelay());
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to send multiplayer handshake: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for server response after the timeout period.
    /// If no response received, shows a notice to the user.
    /// </summary>
    private static System.Collections.IEnumerator CheckServerResponseAfterDelay()
    {
        // Wait for timeout period (plus a small buffer)
        yield return new WaitForSeconds(12f);

        // Check if server responded
        MultiplayerModTracker.CheckServerResponseTimeout();
    }

    #endregion

    #region Configuration
    
    private void LoadConfig()
    {
        try
        {
            string configPath = Path.Combine(GetModFolder(), "config.json");
            
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                Config = JsonConvert.DeserializeObject<ModConfig>(json) ?? new ModConfig();
                Config.MigrateDeprecatedSettings(); // Handle old config field names
                FileLogInternal("Configuration loaded from config.json");
            }
            else
            {
                Config = new ModConfig();
                FileLogInternal("Using default configuration");
            }
            
            // Always save config to update with any new fields
            string updatedJson = JsonConvert.SerializeObject(Config, Formatting.Indented);
            File.WriteAllText(configPath, updatedJson);
        }
        catch (Exception ex)
        {
            LogError($"Failed to load config: {ex.Message}");
            Config = new ModConfig();
        }
    }
    
    private string GetModFolder()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
    }

    /// <summary>
    /// Initializes the FileSystemWatcher to monitor config.json for changes.
    /// Allows runtime config changes without game restart.
    /// </summary>
    private static void InitConfigWatcher()
    {
        try
        {
            string configPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "config.json");
            string configDir = Path.GetDirectoryName(configPath);

            if (string.IsNullOrEmpty(configDir))
            {
                LogWarning("Could not determine config directory for file watcher");
                return;
            }

            _configWatcher = new FileSystemWatcher(configDir, "config.json");
            _configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            _configWatcher.Changed += OnConfigFileChanged;
            _configWatcher.EnableRaisingEvents = true;

            FileLogInternal("Config file watcher enabled - changes will auto-reload");
        }
        catch (Exception ex)
        {
            LogWarning($"Could not enable config file watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles config file change events with debouncing to avoid multiple reloads.
    /// </summary>
    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce - file may be written multiple times in rapid succession
        if (_reloadPending) return;
        _reloadPending = true;

        // Delay reload slightly to allow file write to complete
        // Use ThreadManager for Unity thread safety
        ThreadManager.AddSingleTask(info =>
        {
            System.Threading.Thread.Sleep(500);
            try
            {
                ReloadConfig();
            }
            catch (Exception ex)
            {
                LogError($"Config reload failed: {ex.Message}");
            }
            finally
            {
                _reloadPending = false;
            }
        });
    }

    /// <summary>
    /// Reloads configuration from config.json at runtime.
    /// Called by file watcher or console command.
    /// </summary>
    public static void ReloadConfig()
    {
        try
        {
            string configPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "config.json");

            if (!File.Exists(configPath))
            {
                LogWarning("Config file not found - using current settings");
                return;
            }

            string json = File.ReadAllText(configPath);
            var newConfig = JsonConvert.DeserializeObject<ModConfig>(json);

            if (newConfig == null)
            {
                LogWarning("Failed to parse config.json - using current settings");
                return;
            }

            // Store old values for comparison
            bool wasEnabled = Config?.modEnabled ?? false;
            float oldRange = Config?.range ?? 15f;

            // Apply new config
            newConfig.MigrateDeprecatedSettings();
            Config = newConfig;

            // Invalidate caches if range changed
            if (Math.Abs(Config.range - oldRange) > 0.01f)
            {
                ContainerManager.InvalidateCache();
                ContainerManager.ClearCache();
                ContainerManager.ResetScanMethodCache(); // Recalculate optimal scan method
                Log($"Range changed from {oldRange} to {Config.range} - cache cleared, scan method will be recalculated");
            }

            Log("Configuration reloaded successfully");

            if (Config.modEnabled != wasEnabled)
            {
                Log($"Mod is now {(Config.modEnabled ? "ENABLED" : "DISABLED")}");
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to reload config: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the config file path.
    /// </summary>
    public static string GetConfigPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
            "config.json");
    }

    #endregion

    #region Logging
    
    private static string _logFilePath;
    
    private static void InitFileLog()
    {
        if (_logFilePath == null)
        {
            _logFilePath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "pc_debug.log");
            // Clear old log on startup
            try { File.WriteAllText(_logFilePath, $"=== PC Debug Log Started {DateTime.Now} ===\n"); } catch { }
        }
    }
    
    /// <summary>
    /// Internal file logging - always writes to file regardless of debug settings.
    /// Use sparingly - only for errors, warnings, and critical startup messages.
    /// </summary>
    private static void FileLogInternal(string message)
    {
        InitFileLog();
        try
        {
            File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
    
    /// <summary>
    /// Debug file logging - only writes to file when debug mode is enabled.
    /// Use for verbose diagnostic output.
    /// </summary>
    public static void FileLog(string message)
    {
        // Only write to file when debug is enabled
        if (Config?.isDebug != true)
            return;
        FileLogInternal(message);
    }
    
    /// <summary>
    /// General info logging - writes to Unity console always, file only when debug enabled.
    /// Use for significant events users should see.
    /// </summary>
    public static void Log(string message)
    {
        Debug.Log($"[{MOD_NAME}] {message}");
        if (Config?.isDebug == true)
        {
            FileLogInternal(message);
        }
    }
    
    /// <summary>
    /// Debug logging - writes to file when debug enabled, Unity console only when debug enabled.
    /// Use for diagnostic messages.
    /// </summary>
    public static void LogDebug(string message)
    {
        if (Config?.isDebug != true)
            return;
        FileLogInternal($"[DEBUG] {message}");
        Debug.Log($"[{MOD_NAME}] [DEBUG] {message}");
    }
    
    /// <summary>
    /// Warning logging - always writes to both Unity console and file.
    /// Use for recoverable issues that users should be aware of.
    /// </summary>
    public static void LogWarning(string message)
    {
        Debug.LogWarning($"[{MOD_NAME}] {message}");
        FileLogInternal($"[WARN] {message}");
    }
    
    /// <summary>
    /// Error logging - always writes to both Unity console and file.
    /// Use for errors that may affect functionality.
    /// </summary>
    public static void LogError(string message)
    {
        Debug.LogError($"[{MOD_NAME}] {message}");
        FileLogInternal($"[ERROR] {message}");
    }
    
    #endregion

    #region Helper Methods for Patches

    /// <summary>
    /// Checks if the game state is ready for container operations.
    /// Returns false if world/player/UI not yet initialized.
    /// Also checks multiplayer safety lock - returns false if in multiplayer without server confirmation.
    /// </summary>
    private static bool IsGameReady()
    {
        try
        {
            // Check multiplayer safety lock first
            // In multiplayer, mod is disabled until server confirms ProxiCraft is installed
            if (!MultiplayerModTracker.IsModAllowed())
                return false;

            // Check GameManager exists
            var gm = GameManager.Instance;
            if (gm == null)
                return false;

            // Check World exists
            var world = gm.World;
            if (world == null)
                return false;

            // Check primary player exists
            var player = world.GetPrimaryPlayer();
            if (player == null)
                return false;

            // Check player is alive and in the world
            if (player.IsDead())
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Adds items from nearby containers to the player's item list.
    /// Used by crafting UI to show available materials.
    /// </summary>
    public static List<ItemStack> AddContainerItems(List<ItemStack> playerItems)
    {
        if (!Config?.modEnabled == true)
            return playerItems;

        // Safety check - don't run if game state isn't ready
        if (!IsGameReady())
            return playerItems;

        try
        {
            var containerItems = ContainerManager.GetStorageItems(Config);
            
            if (containerItems.Count > 0)
            {
                var combined = new List<ItemStack>(playerItems);
                combined.AddRange(containerItems);
                LogDebug($"Added {containerItems.Count} item stacks from containers");
                return combined;
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Error adding container items: {ex.Message}");
        }
        
        return playerItems;
    }

    /// <summary>
    /// Adds items from nearby containers to an array version.
    /// </summary>
    public static ItemStack[] AddContainerItemsArray(ItemStack[] playerItems)
    {
        return AddContainerItems(playerItems.ToList()).ToArray();
    }

    /// <summary>
    /// Gets the count of an item including nearby containers.
    /// </summary>
    public static int GetTotalItemCount(int playerCount, ItemValue item)
    {
        if (!Config?.modEnabled == true)
            return playerCount;

        // Safety check - don't run if game state isn't ready
        if (!IsGameReady())
            return playerCount;

        try
        {
            int containerCount = ContainerManager.GetItemCount(Config, item);
            LogDebug($"Item {item?.ItemClass?.GetItemName() ?? "unknown"}: {playerCount} in inventory + {containerCount} in containers");
            return playerCount + containerCount;
        }
        catch (Exception ex)
        {
            LogWarning($"Error counting items: {ex.Message}");
            return playerCount;
        }
    }

    /// <summary>
    /// Removes items from containers after removing from player inventory.
    /// </summary>
    public static void RemoveRemainingItems(ItemValue item, int remaining)
    {
        if (!Config?.modEnabled == true || remaining <= 0)
            return;

        // Safety check - don't run if game state isn't ready
        if (!IsGameReady())
            return;

        try
        {
            int removed = ContainerManager.RemoveItems(Config, item, remaining);
            LogDebug($"Removed {removed}/{remaining} {item?.ItemClass?.GetItemName() ?? "unknown"} from containers");
        }
        catch (Exception ex)
        {
            LogWarning($"Error removing items from containers: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers a refresh of the recipe tracker window if it exists and is visible.
    /// Called when container contents change to update ingredient counts in real-time.
    /// </summary>
    public static void RefreshRecipeTracker()
    {
        if (!Config?.modEnabled == true || !Config?.enableRecipeTrackerUpdates == true)
            return;

        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                return;

            var xui = LocalPlayerUI.GetUIForPlayer(player)?.xui;
            if (xui == null)
                return;

            // Find the recipe tracker window
            var recipeTracker = xui.GetChildByType<XUiC_RecipeTrackerWindow>();
            if (recipeTracker != null)
            {
                recipeTracker.IsDirty = true;
                // Don't log every refresh - too spammy
            }
        }
        catch (Exception ex)
        {
            FileLog($"RefreshRecipeTracker error: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks the HUD ammo counter as dirty to force recalculation.
    /// Called when container contents change to update the ammo count display.
    /// The HUD only recalculates ammo when IsDirty is true or hasChanged() returns true,
    /// but container changes don't trigger hasChanged(), so we must set IsDirty.
    /// </summary>
    public static void MarkHudAmmoDirty()
    {
        if (!Config?.modEnabled == true || !Config?.enableHudAmmoCounter == true)
            return;

        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                return;

            var playerUI = LocalPlayerUI.GetUIForPlayer(player);
            if (playerUI?.xui == null)
                return;
            
            // The HUD stat bars are children of XUi. Use GetChildrenByType to find them.
            var statBars = playerUI.xui.GetChildrenByType<XUiC_HUDStatBar>();
            if (statBars != null && statBars.Count > 0)
            {
                foreach (var statBar in statBars)
                {
                    if (statBar != null)
                        statBar.IsDirty = true;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarning($"MarkHudAmmoDirty error: {ex.Message}");
        }
    }

    #endregion

    #region Harmony Patches - Game Events
    
    /// <summary>
    /// Clears container cache when a new game starts.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartGame))]
    [HarmonyPriority(Priority.Low)]
    public static class GameManager_StartGame_Patch
    {
        public static void Prefix()
        {
            try
            {
                ContainerManager.ClearCache();
                LogDebug("Container cache cleared for new game");
            }
            catch (Exception ex)
            {
                LogWarning($"Error clearing cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Syncs container lock state in multiplayer (when someone opens a container).
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.TELockServer))]
    [HarmonyPriority(Priority.Low)]
    public static class GameManager_TELockServer_Patch
    {
        public static void Postfix(GameManager __instance, int _clrIdx, Vector3i _blockPos, int _lootEntityId)
        {
            if (!Config?.modEnabled == true)
                return;

            try
            {
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    return;

                var tileEntity = _lootEntityId != -1 
                    ? __instance.m_World.GetTileEntity(_lootEntityId) 
                    : __instance.m_World.GetTileEntity(_blockPos);

                if (tileEntity != null && __instance.lockedTileEntities.ContainsKey((ITileEntity)(object)tileEntity))
                {
                    LogDebug($"Broadcasting container lock at {_blockPos}");
                    var packet = NetPackageManager.GetPackage<NetPackagePCLock>().Setup(_blockPos, false);
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        (NetPackage)(object)packet, true, -1, -1, -1, null, 192, false);
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in TELockServer patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Syncs container unlock state in multiplayer (when someone closes a container).
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.TEUnlockServer))]
    [HarmonyPriority(Priority.Low)]
    public static class GameManager_TEUnlockServer_Patch
    {
        public static void Postfix(GameManager __instance, int _clrIdx, Vector3i _blockPos, int _lootEntityId)
        {
            if (!Config?.modEnabled == true)
                return;

            try
            {
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    return;

                var tileEntity = _lootEntityId != -1 
                    ? __instance.m_World.GetTileEntity(_lootEntityId) 
                    : __instance.m_World.GetTileEntity(_blockPos);

                if (tileEntity != null && !__instance.lockedTileEntities.ContainsKey((ITileEntity)(object)tileEntity))
                {
                    LogDebug($"Broadcasting container unlock at {_blockPos}");
                    var packet = NetPackageManager.GetPackage<NetPackagePCLock>().Setup(_blockPos, true);
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        (NetPackage)(object)packet, true, -1, -1, -1, null, 192, false);
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in TEUnlockServer patch: {ex.Message}");
            }
        }
    }

    #endregion

    #region Harmony Patches - Crafting
    
    /// <summary>
    /// Patch ItemActionEntryCraft.OnActivated to include container items when checking for materials.
    /// 
    /// BACKPACK COMPATIBILITY: Uses Postfix to add container items AFTER inventory is queried.
    /// This works regardless of how backpack mods change inventory structure.
    ///
    /// ROBUSTNESS:
    /// - Checks for transpiler conflicts before patching
    /// - Returns original code if pattern not found
    /// - Records status for runtime checks
    /// 
    /// NOTE: This patch targets the private 'hasItems' method which calls GetAllItemStacks.
    /// The OnActivated method doesn't directly call GetAllItemStacks - it calls hasItems().
    /// </summary>
    [HarmonyPatch(typeof(ItemActionEntryCraft), "hasItems")]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionEntryCraft_hasItems_Patch
    {
        private const string FEATURE_ID = "CraftHasItems";

        // Use Transpiler to inject after GetAllItemStacks - adds container items to result
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            // Check for Transpiler conflicts first
            var targetMethodInfo = AccessTools.Method(typeof(ItemActionEntryCraft), "hasItems");
            if (AdaptivePatching.HasTranspilerConflict(targetMethodInfo))
            {
                LogWarning("ItemActionEntryCraft.hasItems has Transpiler conflict - using fallback");
                RobustTranspiler.RecordTranspilerStatus(FEATURE_ID, false);
                return instructions; // Return unchanged, rely on other patches
            }

            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                var injectedMethod = AccessTools.Method(typeof(ProxiCraft), nameof(AddContainerItems));

                // Inject our method call after GetAllItemStacks
                return RobustTranspiler.TryInjectAfterMethodCall(
                    codes,
                    typeof(XUiM_PlayerInventory),
                    nameof(XUiM_PlayerInventory.GetAllItemStacks),
                    injectedMethod,
                    FEATURE_ID);
            });
        }
    }

    /// <summary>
    /// Patch XUiC_RecipeList to include container items in recipe availability check.
    /// 
    /// BACKPACK COMPATIBILITY: Uses Prefix to add items BEFORE recipe calculations.
    /// This ensures container items are considered when determining craftability.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_RecipeList))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_RecipeList_Patches
    {
        // Patch the recipe list building to include container items BEFORE calculations
        [HarmonyPatch("BuildRecipeInfosList")]
        [HarmonyPrefix]
        public static void BuildRecipeInfosList_Prefix(XUiC_RecipeList __instance, ref List<ItemStack> _items)
        {
            if (!Config?.modEnabled == true)
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
                return;

            try
            {
                // Add container items to the list BEFORE recipe calculations
                var containerItems = ContainerManager.GetStorageItems(Config);
                if (containerItems.Count > 0)
                {
                    // Create a new list combining inventory + containers
                    var combined = new List<ItemStack>(_items);
                    combined.AddRange(containerItems);
                    _items = combined;
                    LogDebug($"BuildRecipeInfosList: Added {containerItems.Count} container items to recipe check");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in BuildRecipeInfosList_Prefix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch XUiC_RecipeCraftCount.calcMaxCraftable to include container items.
    /// 
    /// BACKPACK COMPATIBILITY: Adds to result count, doesn't modify inventory queries.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_RecipeCraftCount), "calcMaxCraftable")]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_RecipeCraftCount_calcMaxCraftable_Patch
    {
        private const string FEATURE_ID = "CraftMaxCraftable";

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Check for conflicts
            var methodToCheck = AccessTools.Method(typeof(XUiC_RecipeCraftCount), "calcMaxCraftable");
            if (AdaptivePatching.HasTranspilerConflict(methodToCheck))
            {
                LogWarning("XUiC_RecipeCraftCount.calcMaxCraftable has conflict - skipping Transpiler");
                RobustTranspiler.RecordTranspilerStatus(FEATURE_ID, false);
                return instructions;
            }

            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                var injectedMethod = AccessTools.Method(typeof(ProxiCraft), nameof(AddContainerItemsArray));

                // Find GetAllItemStacks call
                int getAllIdx = RobustTranspiler.FindMethodCall(
                    codes,
                    typeof(XUiM_PlayerInventory),
                    nameof(XUiM_PlayerInventory.GetAllItemStacks));

                if (getAllIdx == -1)
                {
                    LogDebug($"[{FEATURE_ID}] GetAllItemStacks not found");
                    return false;
                }

                // Find the ToArray call after GetAllItemStacks (within 5 instructions)
                for (int j = getAllIdx + 1; j < Math.Min(getAllIdx + 6, codes.Count); j++)
                {
                    if (codes[j].opcode == OpCodes.Callvirt &&
                        codes[j].operand is MethodInfo toArrayMethod &&
                        toArrayMethod.Name == "ToArray")
                    {
                        codes.Insert(j + 1, new CodeInstruction(OpCodes.Call, injectedMethod));
                        LogDebug($"[{FEATURE_ID}] Injected after ToArray at IL index {j}");
                        return true;
                    }
                }

                LogDebug($"[{FEATURE_ID}] ToArray not found after GetAllItemStacks");
                return false;
            });
        }
    }

    #endregion

    #region Harmony Patches - Ingredient Display
    
    /// <summary>
    /// Prefix patch on HasItem(ItemValue) to return VANILLA inventory-only result.
    /// 
    /// This fixes the "Take Like" button issue where our GetItemCount patch makes
    /// the game think the player "has" every item type that exists in nearby containers,
    /// causing "Take Like" to take ALL items instead of just matching ones.
    /// 
    /// HasItem(ItemValue) is used by the UI for "do you have this item type?" checks,
    /// NOT for crafting quantity checks. By returning vanilla result, we preserve
    /// correct "Take Like" behavior while our GetItemCount patch still works for crafting.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.HasItem), new Type[] { typeof(ItemValue) })]
    [HarmonyPriority(Priority.High)] // Run before other patches
    private static class XUiM_PlayerInventory_HasItem_ItemValue_Patch
    {
        public static bool Prefix(XUiM_PlayerInventory __instance, ref bool __result, ItemValue _item)
        {
            // Return vanilla inventory-only check, bypassing our GetItemCount postfix
            // This preserves correct "Take Like" button behavior
            try
            {
                // Check backpack and toolbelt only (vanilla behavior)
                __result = __instance.backpack.GetItemCount(_item) > 0 || 
                           __instance.toolbelt.GetItemCount(_item) > 0;
                return false; // Skip original method (and any postfixes that might use GetItemCount)
            }
            catch
            {
                // On error, let original method run
                return true;
            }
        }
    }

    /// <summary>
    /// DIRECT Postfix patch on XUiM_PlayerInventory.GetItemCount
    /// This is the most reliable way to add container counts to ingredient displays.
    /// 
    /// ENHANCED SAFETY MODE: When enhancedSafetyCrafting is enabled, uses VirtualInventoryProvider
    /// which has additional multiplayer safety checks. When disabled, uses ContainerManager directly.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) })]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_GetItemCount_Patch
    {
        public static void Postfix(XUiM_PlayerInventory __instance, ref int __result, ItemValue _itemValue)
        {
            if (!Config?.modEnabled == true || _itemValue == null)
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
                return;

            try
            {
                int containerCount;
                
                // ENHANCED SAFETY: Use VirtualInventoryProvider for centralized safety checks
                if (Config.enhancedSafetyCrafting)
                {
                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    if (player == null) return;
                    
                    // VirtualInventoryProvider returns total (inventory + storage)
                    // We already have inventory count in __result, so get total and subtract
                    int totalCount = VirtualInventoryProvider.GetTotalItemCount(player, _itemValue);
                    containerCount = totalCount - __result;
                    if (containerCount < 0) containerCount = 0; // Safety
                }
                else
                {
                    // LEGACY: Direct ContainerManager access (original behavior)
                    containerCount = ContainerManager.GetItemCount(Config, _itemValue);
                }
                
                if (containerCount > 0)
                {
                    int oldResult = __result;
                    __result = AdaptivePatching.SafeAddCount(__result, containerCount);
                    LogDebug($"GetItemCount for {_itemValue.ItemClass?.GetItemName()}: {oldResult} + {containerCount} containers = {__result}");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in GetItemCount patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// DIRECT Postfix patch on XUiM_PlayerInventory.GetItemCount(int)
    /// This overload is used by the radial menu (ItemActionAttack.SetupRadial) to check ammo counts.
    /// Without this patch, radial menu shows ammo as greyed out even when available in containers.
    /// 
    /// ENHANCED SAFETY MODE: When enhancedSafetyCrafting is enabled, uses VirtualInventoryProvider.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(int) })]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_GetItemCountInt_Patch
    {
        public static void Postfix(XUiM_PlayerInventory __instance, ref int __result, int _itemId)
        {
            if (!Config?.modEnabled == true || _itemId <= 0)
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
                return;

            try
            {
                int containerCount;
                
                // ENHANCED SAFETY: Use VirtualInventoryProvider for centralized safety checks
                if (Config.enhancedSafetyCrafting)
                {
                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    if (player == null) return;
                    
                    int totalCount = VirtualInventoryProvider.GetTotalItemCount(player, _itemId);
                    containerCount = totalCount - __result;
                    if (containerCount < 0) containerCount = 0;
                }
                else
                {
                    // LEGACY: Direct ContainerManager access
                    containerCount = ContainerManager.GetItemCount(Config, _itemId);
                }
                
                if (containerCount > 0)
                {
                    int oldResult = __result;
                    __result = AdaptivePatching.SafeAddCount(__result, containerCount);
                    
                    // Get item name for debug logging
                    string itemName = ItemClass.GetForId(_itemId)?.GetItemName() ?? $"ID:{_itemId}";
                    LogDebug($"GetItemCount(int) for {itemName}: {oldResult} + {containerCount} containers = {__result}");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in GetItemCount(int) patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch ingredient entry to show correct item counts including containers.
    /// BACKUP: This Transpiler is a backup in case GetItemCount isn't called.
    ///
    /// ROBUSTNESS:
    /// - Uses safe transpiler wrapper
    /// - Returns original code if pattern not found
    /// </summary>
    [HarmonyPatch(typeof(XUiC_IngredientEntry), "GetBindingValueInternal")]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_IngredientEntry_GetBindingValueInternal_Patch
    {
        private const string FEATURE_ID = "IngredientBinding";

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                int getItemCountIdx = RobustTranspiler.FindMethodCall(
                    codes,
                    typeof(XUiM_PlayerInventory),
                    "GetItemCount",
                    new Type[] { typeof(ItemValue) });

                if (getItemCountIdx == -1)
                {
                    LogDebug($"[{FEATURE_ID}] GetItemCount not found");
                    return false;
                }

                // Insert our method call after GetItemCount (add Ldarg_0 first for 'this')
                var injectedMethod = AccessTools.Method(typeof(ProxiCraft), nameof(AddIngredientContainerCount));
                codes.Insert(getItemCountIdx + 1, new CodeInstruction(OpCodes.Ldarg_0)); // Load 'this' for ingredient info
                codes.Insert(getItemCountIdx + 2, new CodeInstruction(OpCodes.Call, injectedMethod));

                LogDebug($"[{FEATURE_ID}] Injected after GetItemCount at IL index {getItemCountIdx}");
                return true;
            });
        }
    }

    /// <summary>
    /// Adds container item counts for ingredient display.
    /// ADDITIVE: Takes inventory count (from any backpack mod) and adds container count.
    /// </summary>
    public static int AddIngredientContainerCount(int playerCount, XUiC_IngredientEntry entry)
    {
        if (!Config?.modEnabled == true || entry?.Ingredient?.itemValue == null)
            return playerCount;

        // Safety check - don't run if game state isn't ready
        if (!IsGameReady())
            return playerCount;

        try
        {
            // ADDITIVE: playerCount is whatever inventory returned (backpack mod compatible)
            // We just add our container count on top
            int containerCount = ContainerManager.GetItemCount(Config, entry.Ingredient.itemValue);
            return AdaptivePatching.SafeAddCount(playerCount, containerCount);
        }
        catch (Exception ex)
        {
            LogWarning($"Error getting ingredient count: {ex.Message}");
            return playerCount;
        }
    }

    #endregion

    #region Harmony Patches - HasItems and RemoveItems

    /// <summary>
    /// Patch HasItems to check containers when determining if player has enough materials.
    /// 
    /// BACKPACK COMPATIBILITY: This is a Postfix that only runs if inventory said "no".
    /// We check if containers can supplement what inventory is missing.
    /// 
    /// ENHANCED SAFETY MODE: When enhancedSafetyCrafting is enabled, uses VirtualInventoryProvider.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.HasItems))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_HasItems_Patch
    {
        public static void Postfix(XUiM_PlayerInventory __instance, ref bool __result, IList<ItemStack> _itemStacks, int _multiplier)
        {
            // If inventory already has items, no need to check containers
            // This makes us purely ADDITIVE - we only supplement inventory
            if (__result || !(Config?.modEnabled == true))
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
                return;

            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                
                // ENHANCED SAFETY: Use VirtualInventoryProvider for centralized safety checks
                if (Config.enhancedSafetyCrafting && player != null)
                {
                    __result = VirtualInventoryProvider.HasAllItems(player, _itemStacks, _multiplier);
                    if (__result)
                    {
                        LogDebug("HasItems satisfied via VirtualInventoryProvider");
                    }
                    return;
                }
                
                // LEGACY: Direct ContainerManager access (original behavior)
                // Get what inventory has (using standard interface - backpack mod compatible)
                var inventoryItems = __instance.GetAllItemStacks();
                
                // Check each required item
                foreach (var required in _itemStacks)
                {
                    if (required == null || required.IsEmpty())
                        continue;

                    int needed = required.count * _multiplier;
                    
                    // Count from inventory (whatever backpack mod provides)
                    int inventoryCount = 0;
                    foreach (var invItem in inventoryItems)
                    {
                        if (invItem?.itemValue != null && 
                            invItem.itemValue.type == required.itemValue.type)
                        {
                            inventoryCount += invItem.count;
                        }
                    }
                    
                    // Count from containers (our addition)
                    int containerCount = ContainerManager.GetItemCount(Config, required.itemValue);
                    
                    int totalAvailable = AdaptivePatching.SafeAddCount(inventoryCount, containerCount);
                    
                    if (totalAvailable < needed)
                    {
                        return; // Still don't have enough even with containers
                    }
                }
                
                __result = true;
                LogDebug("HasItems satisfied by inventory + containers");
            }
            catch (Exception ex)
            {
                LogWarning($"Error in HasItems patch: {ex.Message}");
                // On error, don't change result - let vanilla behavior continue
            }
        }
    }

    /// <summary>
    /// Patch RemoveItems to also remove from containers when crafting/trading.
    /// 
    /// BACKPACK COMPATIBILITY: This is a Postfix - we only remove from containers
    /// what the inventory removal couldn't satisfy. Inventory structure unchanged.
    /// 
    /// LOGIC FIX: We track inventory counts BEFORE removal in Prefix, then calculate
    /// how much still needs to come from containers in Postfix.
    /// 
    /// CRITICAL: We must get RAW inventory counts (not patched GetItemCount which includes containers)
    /// 
    /// ENHANCED SAFETY MODE: When enhancedSafetyCrafting is enabled, uses VirtualInventoryProvider
    /// for centralized item consumption with multiplayer safety checks.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.RemoveItems))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_RemoveItems_Patch
    {
        // Track what needs to be removed from containers after inventory removal
        private static IList<ItemStack> _pendingContainerRemovals;
        private static int _pendingMultiplier;
        private static Dictionary<int, int> _inventoryCountsBefore = new Dictionary<int, int>();
        private static bool _useEnhancedSafety = false;

        [HarmonyPrefix]
        public static void Prefix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier)
        {
            if (!Config?.modEnabled == true)
                return;

            // Check if Enhanced Safety is enabled
            _useEnhancedSafety = Config.enhancedSafetyCrafting;

            // Store what's being requested for the Postfix
            _pendingContainerRemovals = _itemStacks;
            _pendingMultiplier = _multiplier;
            
            // Track RAW inventory counts BEFORE removal (NOT using patched GetItemCount!)
            // We need to directly query bag + toolbelt to get the real inventory count
            _inventoryCountsBefore.Clear();
            foreach (var item in _itemStacks)
            {
                if (item == null || item.IsEmpty())
                    continue;
                    
                int itemType = item.itemValue.type;
                if (!_inventoryCountsBefore.ContainsKey(itemType))
                {
                    // Get RAW counts from bag and toolbelt directly, bypassing our patch
                    int bagCount = __instance.Backpack?.GetItemCount(item.itemValue) ?? 0;
                    int toolbeltCount = __instance.Toolbelt?.GetItemCount(item.itemValue) ?? 0;
                    int rawInventoryCount = bagCount + toolbeltCount;
                    _inventoryCountsBefore[itemType] = rawInventoryCount;
                    FileLog($"[REMOVEITEMS] Prefix: {item.itemValue.ItemClass?.GetItemName()} rawInv={rawInventoryCount} (bag={bagCount}, toolbelt={toolbeltCount}), need={item.count * _multiplier}, enhancedSafety={_useEnhancedSafety}");
                }
            }
        }

        [HarmonyPostfix]
        public static void Postfix(XUiM_PlayerInventory __instance)
        {
            if (!Config?.modEnabled == true || _pendingContainerRemovals == null)
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
            {
                _pendingContainerRemovals = null;
                _inventoryCountsBefore.Clear();
                return;
            }

            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                
                foreach (var required in _pendingContainerRemovals)
                {
                    if (required == null || required.IsEmpty())
                        continue;

                    int neededTotal = required.count * _pendingMultiplier;
                    int itemType = required.itemValue.type;
                    
                    // Get how much inventory HAD before removal (RAW count, not patched)
                    int inventoryHadBefore = _inventoryCountsBefore.TryGetValue(itemType, out int count) ? count : 0;
                    
                    // Calculate how much inventory could satisfy
                    int inventorySatisfied = Math.Min(neededTotal, inventoryHadBefore);
                    
                    // The rest needs to come from containers
                    int fromContainers = neededTotal - inventorySatisfied;
                    
                    FileLog($"[REMOVEITEMS] Postfix: {required.itemValue.ItemClass?.GetItemName()} neededTotal={neededTotal}, inventoryHad={inventoryHadBefore}, fromContainers={fromContainers}");
                    
                    if (fromContainers > 0)
                    {
                        int removed;
                        
                        // ENHANCED SAFETY: Use VirtualInventoryProvider
                        if (_useEnhancedSafety && player != null)
                        {
                            // VirtualInventoryProvider.ConsumeItems handles bag->toolbelt->storage order
                            // But inventory already removed from bag/toolbelt, so just remove from storage
                            removed = ContainerManager.RemoveItems(Config, required.itemValue, fromContainers);
                            FileLog($"[REMOVEITEMS] Enhanced safety: Removed {removed} from containers via ContainerManager");
                        }
                        else
                        {
                            // LEGACY: Direct ContainerManager access
                            removed = ContainerManager.RemoveItems(Config, required.itemValue, fromContainers);
                        }
                        
                        LogDebug($"Removed {removed}/{fromContainers} {required.itemValue?.ItemClass?.GetItemName()} from containers (had {inventoryHadBefore} in inventory, needed {neededTotal})");
                        FileLog($"[REMOVEITEMS] Removed {removed} from containers");
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in RemoveItems Postfix: {ex.Message}");
                FileLog($"[REMOVEITEMS] ERROR: {ex.Message}");
            }
            finally
            {
                _pendingContainerRemovals = null;
                _inventoryCountsBefore.Clear();
            }
        }
    }

    #endregion

    #region Harmony Patches - Reload Support

    // ====================================================================================
    // RELOAD SUPPORT - DESIGN DECISION
    // ====================================================================================
    //
    // WHY TRANSPILER instead of PREFIX PRE-STAGE?
    // --------------------------------------------
    // Reload requires a VARIABLE amount of ammo (magazine size - current ammo). The exact
    // count is calculated by vanilla code with perks, weapon mods, etc. factored in.
    //
    // Using a transpiler to intercept Bag.DecItem(item, count) is simpler because:
    // - Vanilla already calculated the exact 'count' we need
    // - We just extend the call: "if bag doesn't have enough, check containers"
    // - No need to duplicate vanilla's magazine size / perk calculations
    //
    // ALTERNATIVE APPROACH (if transpiler breaks on game update):
    // Could switch to PREFIX pre-stage like vehicle repair:
    // 1. Calculate: neededAmmo = magazineSize - currentAmmo (factor in perks)
    // 2. Check if bag has enough, if not transfer from containers
    // 3. Let vanilla proceed normally
    // DOWNSIDE: Must mirror vanilla's calculation logic, which could drift on updates.
    //
    // STABILITY TRADEOFF:
    // - Transpiler: More brittle to IL changes, but simpler logic
    // - Pre-stage: More stable patches, but duplicates game calculations
    //
    // Current choice: Transpiler (simpler, RobustTranspiler handles failures gracefully)
    // ====================================================================================
    
    /// <summary>
    /// Patch ItemActionRanged.CanReload to check containers for ammo.
    /// 
    /// CRITICAL: This is the GATEKEEPER for reload!
    /// The vanilla game checks inventory.GetItemCount and bag.GetItemCount for ammo.
    /// If both return 0, reload is blocked BEFORE the animation even starts.
    /// We need to add our container check as a fallback.
    /// 
    /// Without this patch, pressing R does nothing when ammo is only in containers.
    /// The existing transpiler on GetAmmoCountToReload only runs DURING the animation.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionRanged), nameof(ItemActionRanged.CanReload))]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionRanged_CanReload_Patch
    {
        public static void Postfix(ItemActionRanged __instance, ItemActionData _actionData, ref bool __result)
        {
            // If vanilla already allows reload, no need to check containers
            if (__result)
                return;
            
            // If mod disabled or reload feature disabled, don't override
            if (!Config?.modEnabled == true || !Config?.enableForReload == true)
                return;
            
            // Safety check
            if (!IsGameReady())
                return;
            
            try
            {
                // Get the ammo type for the current weapon
                var holdingItemItemValue = _actionData.invData.holdingEntity.inventory.holdingItemItemValue;
                var ammoItemValue = ItemClass.GetItem(__instance.MagazineItemNames[holdingItemItemValue.SelectedAmmoTypeIndex]);
                
                // Check if we have ammo in nearby containers
                int containerCount;
                if (Config.enhancedSafetyReload)
                {
                    // Enhanced Safety mode: Use VirtualInventoryProvider with MP safety checks
                    // Note: VirtualInventoryProvider.GetTotalItemCount returns bag+toolbelt+storage,
                    // so we need to subtract what the player already has (vanilla already checked that)
                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    int totalCount = VirtualInventoryProvider.GetTotalItemCount(player, ammoItemValue);
                    int playerCount = player?.bag?.GetItemCount(ammoItemValue) ?? 0;
                    playerCount += player?.inventory?.GetItemCount(ammoItemValue) ?? 0;
                    containerCount = totalCount - playerCount;
                }
                else
                {
                    // Legacy mode: Direct ContainerManager access
                    containerCount = ContainerManager.GetItemCount(Config, ammoItemValue);
                }
                
                if (containerCount > 0)
                {
                    LogDebug($"CanReload: Found {containerCount} {ammoItemValue.ItemClass?.GetItemName()} in containers - allowing reload");
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in CanReload Postfix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch AnimatorRangedReloadState to get ammo from containers.
    ///
    /// ROBUSTNESS:
    /// - Uses safe transpiler wrapper
    /// - Patches both GetItemCount (for counting) and DecItem (for removal)
    /// - Returns original code if patterns not found
    /// </summary>
    [HarmonyPatch(typeof(AnimatorRangedReloadState), "GetAmmoCountToReload")]
    [HarmonyPriority(Priority.Low)]
    private static class AnimatorRangedReloadState_GetAmmoCountToReload_Patch
    {
        private const string FEATURE_ID = "ReloadAmmo";

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Config?.enableForReload == true)
                return instructions;

            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                bool anyPatched = false;

                // Patch GetItemCount calls to add container counts
                var getItemCountIndices = RobustTranspiler.FindAllMethodCalls(
                    codes,
                    typeof(Inventory),
                    "GetItemCount",
                    new Type[] { typeof(ItemValue), typeof(bool), typeof(int), typeof(int), typeof(bool) });

                foreach (int idx in getItemCountIndices)
                {
                    var injectedMethod = AccessTools.Method(typeof(ProxiCraft), nameof(AddReloadContainerCount));
                    codes.Insert(idx + 1, new CodeInstruction(OpCodes.Ldarg_2)); // item value parameter
                    codes.Insert(idx + 2, new CodeInstruction(OpCodes.Call, injectedMethod));
                    LogDebug($"[{FEATURE_ID}] Injected after GetItemCount at IL index {idx}");
                    anyPatched = true;
                }

                // Patch DecItem calls to remove from containers
                var decItemMethod = AccessTools.Method(typeof(ProxiCraft), nameof(DecItemForReload));
                var decItemIndices = RobustTranspiler.FindAllMethodCalls(
                    codes,
                    typeof(Inventory),
                    "DecItem");

                // Note: indices may have shifted after inserting above, but we're replacing in-place
                for (int i = 0; i < codes.Count; i++)
                {
                    if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) &&
                        codes[i].operand is MethodInfo method &&
                        method.DeclaringType == typeof(Inventory) &&
                        method.Name == "DecItem")
                    {
                        codes[i].operand = decItemMethod;
                        codes[i].opcode = OpCodes.Call;
                        LogDebug($"[{FEATURE_ID}] Replaced DecItem at IL index {i}");
                        anyPatched = true;
                    }
                }

                return anyPatched;
            });
        }
    }

    /// <summary>
    /// Adds container ammo count for reload operations.
    /// </summary>
    public static int AddReloadContainerCount(int inventoryCount, ItemValue ammoItem)
    {
        LogDebug($"AddReloadContainerCount called: inventoryCount={inventoryCount}, ammo={ammoItem?.ItemClass?.GetItemName()}");
        
        if (!Config?.modEnabled == true || !Config?.enableForReload == true)
            return inventoryCount;

        if (Config.enhancedSafetyReload)
        {
            // Enhanced Safety mode: Use VirtualInventoryProvider with MP safety checks
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            // VirtualInventoryProvider.GetTotalItemCount returns the TOTAL (inventory + storage)
            // Since inventoryCount was already calculated, we add storage count on top
            int storageCount = ContainerManager.GetItemCount(Config, ammoItem);
            // But we need to check multiplayer safety first
            if (!MultiplayerModTracker.IsModAllowed())
            {
                LogDebug($"AddReloadContainerCount (enhanced): MP locked, returning {inventoryCount}");
                return inventoryCount;
            }
            int result = inventoryCount + storageCount;
            LogDebug($"AddReloadContainerCount (enhanced): returning {result}");
            return result;
        }
        else
        {
            // Legacy mode: Direct ContainerManager access
            int result = GetTotalItemCount(inventoryCount, ammoItem);
            LogDebug($"AddReloadContainerCount: returning {result}");
            return result;
        }
    }

    /// <summary>
    /// Removes ammo from inventory and containers for reload.
    /// </summary>
    public static int DecItemForReload(Inventory inventory, ItemValue item, int count, bool ignoreModded, IList<ItemStack> removedItems)
    {
        LogDebug($"DecItemForReload called: item={item?.ItemClass?.GetItemName()}, count={count}");
        
        int removed = inventory.DecItem(item, count, ignoreModded, removedItems);
        LogDebug($"DecItemForReload: inventory.DecItem returned {removed}");
        
        if (!Config?.modEnabled == true || !Config?.enableForReload == true)
            return removed;

        // Safety check - don't access containers if game state isn't ready
        if (!IsGameReady())
            return removed;

        if (removed < count)
        {
            int remaining = count - removed;
            LogDebug($"DecItemForReload: Need {remaining} more from containers");
            
            int containerRemoved;
            if (Config.enhancedSafetyReload)
            {
                // Enhanced Safety mode: Use VirtualInventoryProvider with MP safety checks
                // Note: We already removed from inventory, so just need storage portion
                // Check multiplayer safety first
                if (!MultiplayerModTracker.IsModAllowed())
                {
                    LogDebug($"DecItemForReload (enhanced): MP locked, skipping storage");
                    return removed;
                }
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            else
            {
                // Legacy mode: Direct ContainerManager access
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            LogDebug($"Removed {containerRemoved} ammo from containers for reload");
            removed += containerRemoved;
        }
        
        return removed;
    }

    #endregion

    #region Harmony Patches - Vehicle Refuel Support

    // ====================================================================================
    // VEHICLE REFUEL SUPPORT - DESIGN DECISION
    // ====================================================================================
    //
    // WHY TRANSPILER instead of PREFIX PRE-STAGE?
    // --------------------------------------------
    // Same reasoning as reload - fuel consumption is VARIABLE (tank capacity - current fuel).
    // Vanilla calculates this internally. Transpiler intercepts Bag.DecItem and extends it.
    //
    // ALTERNATIVE APPROACH (if transpiler breaks on game update):
    // Could switch to PREFIX pre-stage:
    // 1. Calculate: neededFuel = tankCapacity - currentFuel
    // 2. Transfer gas cans from containers to bag
    // 3. Let vanilla proceed
    // DOWNSIDE: Must handle partial gas cans, stack sizes, fuel efficiency perks.
    //
    // Current choice: Transpiler (RobustTranspiler with fallback patterns)
    // ====================================================================================
    
    /// <summary>
    /// Patch EntityVehicle.hasGasCan to check containers.
    /// </summary>
    [HarmonyPatch(typeof(EntityVehicle), "hasGasCan")]
    [HarmonyPriority(Priority.Low)]
    private static class EntityVehicle_hasGasCan_Patch
    {
        public static void Postfix(EntityVehicle __instance, EntityAlive _ea, ref bool __result)
        {
            // If already found gas, or mod disabled, skip
            if (__result || !Config?.modEnabled == true || !Config?.enableForRefuel == true)
                return;

            try
            {
                string fuelItemName = __instance.GetVehicle()?.GetFuelItem();
                if (string.IsNullOrEmpty(fuelItemName))
                    return;

                var fuelItem = ItemClass.GetItem(fuelItemName, false);
                
                int containerCount;
                if (Config.enhancedSafetyRefuel)
                {
                    // Enhanced Safety mode: Check multiplayer safety first
                    if (!MultiplayerModTracker.IsModAllowed())
                    {
                        LogDebug($"hasGasCan (enhanced): MP locked, skipping storage check");
                        return;
                    }
                    containerCount = ContainerManager.GetItemCount(Config, fuelItem);
                }
                else
                {
                    // Legacy mode: Direct ContainerManager access
                    containerCount = ContainerManager.GetItemCount(Config, fuelItem);
                }
                
                if (containerCount > 0)
                {
                    __result = true;
                    LogDebug($"Found {containerCount} fuel in containers");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error checking fuel in containers: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch EntityVehicle.takeFuel to use fuel from containers.
    ///
    /// ROBUSTNESS:
    /// - Uses signature-based method matching (survives minor refactors)
    /// - Returns original code if pattern not found (no crash)
    /// - Records status for runtime feature checks
    /// </summary>
    [HarmonyPatch(typeof(EntityVehicle), "takeFuel")]
    [HarmonyPriority(Priority.Low)]
    private static class EntityVehicle_takeFuel_Patch
    {
        private const string FEATURE_ID = "VehicleRefuel";

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Config?.enableForRefuel == true)
                return instructions;

            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                var replacementMethod = AccessTools.Method(typeof(ProxiCraft), nameof(DecItemForRefuel));

                // Try to find and replace Bag.DecItem call
                // Fallback patterns for if the method gets renamed
                return RobustTranspiler.TryReplaceMethodCall(
                    codes,
                    typeof(Bag),
                    nameof(Bag.DecItem),
                    replacementMethod,
                    FEATURE_ID,
                    targetParamTypes: null,
                    occurrence: 1,
                    fallbackNamePatterns: new[] { "Dec", "Remove", "Subtract", "Consume" });
            });
        }
    }

    /// <summary>
    /// Removes fuel from bag and containers for vehicle refueling.
    /// </summary>
    public static int DecItemForRefuel(Bag bag, ItemValue item, int count, bool ignoreModded, IList<ItemStack> removedItems)
    {
        int removed = bag.DecItem(item, count, ignoreModded, removedItems);
        
        if (!Config?.modEnabled == true || !Config?.enableForRefuel == true)
            return removed;

        // Safety check - don't access containers if game state isn't ready
        if (!IsGameReady())
            return removed;

        if (removed < count)
        {
            int remaining = count - removed;
            
            int containerRemoved;
            if (Config.enhancedSafetyRefuel)
            {
                // Enhanced Safety mode: Check multiplayer safety first
                if (!MultiplayerModTracker.IsModAllowed())
                {
                    LogDebug($"DecItemForRefuel (enhanced): MP locked, skipping storage");
                    return removed;
                }
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            else
            {
                // Legacy mode: Direct ContainerManager access
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            LogDebug($"Removed {containerRemoved} fuel from containers");
            removed += containerRemoved;
        }
        
        return removed;
    }

    #endregion

    #region Harmony Patches - Trader Support
    
    /// <summary>
    /// Patch purchase button to include currency from containers.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionEntryPurchase), "RefreshEnabled")]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionEntryPurchase_RefreshEnabled_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Config?.enableForTrader == true)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var getItemCountMethod = AccessTools.Method(typeof(XUiM_PlayerInventory), "GetItemCount", new Type[] { typeof(ItemValue) });
            
            for (int i = 0; i < codes.Count; i++)
            {
                if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) && 
                    codes[i].operand is MethodInfo method && 
                    method == getItemCountMethod)
                {
                    var injectedMethod = AccessTools.Method(typeof(ProxiCraft), nameof(AddCurrencyContainerCount));
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, injectedMethod));
                    LogDebug("Patched ItemActionEntryPurchase.RefreshEnabled");
                    break;
                }
            }
            
            return codes.AsEnumerable();
        }
    }

    /// <summary>
    /// Adds currency count from containers for trader.
    /// </summary>
    public static int AddCurrencyContainerCount(int playerCount)
    {
        if (!Config?.modEnabled == true || !Config?.enableForTrader == true)
            return playerCount;

        try
        {
            var currencyItem = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
            return GetTotalItemCount(playerCount, currencyItem);
        }
        catch (Exception ex)
        {
            LogWarning($"Error getting currency count: {ex.Message}");
            return playerCount;
        }
    }

    // ====================================================================================
    // TRADER SELLING SUPPORT - REMOVED
    // ====================================================================================
    // This feature was removed due to item duplication bugs that could not be fully 
    // mitigated. See TRADER_SELLING_POSTMORTEM.md for full technical details.
    //
    // SUMMARY: We had to replicate ~60% of the game's selling validation logic to
    // determine if a sale would succeed BEFORE adding items to the player's slot.
    // Any mismatch between our checks and vanilla's checks caused item duplication.
    //
    // The "buy from trader" feature (paying with dukes from containers) remains
    // functional because it has much simpler failure cases.
    // ====================================================================================

    #endregion

    #region Harmony Patches - Challenge Objective Support
    
    // ====================================================================================
    // CHALLENGE TRACKER INTEGRATION
    // ====================================================================================
    // 
    // PROBLEM: The game's challenge tracker (e.g., "Gather 4 Wood") only counts items in 
    // player inventory (bag + toolbelt), not in storage containers. When items move between
    // inventory and containers, the count should update in real-time.
    //
    // SOLUTION ARCHITECTURE:
    // 1. LootContainer_OnOpen_Patch: Cache container reference when opened
    // 2. LootContainer_OnClose_Patch: Clear cached reference when closed
    // 3. LootContainer_SlotChanged_Patch: Fire DragAndDropItemChanged when container changes
    // 4. ItemStack_SlotChanged_Patch: Fire DragAndDropItemChanged when inventory changes
    // 5. HandleUpdatingCurrent_Patch: SET Current field to include container items
    // 6. StatusText_Patch: Display correct count in UI (backup)
    // 7. CheckComplete_Patch: Mark objective complete when containers satisfy requirement
    //
    // CRITICAL: We use DragAndDropItemChanged instead of OnBackpackItemsChangedInternal!
    // OnBackpackItemsChangedInternal fires DURING transfers and causes item duplication.
    // DragAndDropItemChanged fires AFTER transfers complete and is safe.
    //
    // The challenge system already listens to DragAndDropItemChanged (via subscription in
    // EntityPlayerLocal), so by firing that event when container slots change, we trigger
    // the game's own recount logic - we just need to add our container items to the count.
    //
    // See RESEARCH_NOTES.md for the full history of fixes #1-#8e that led to this solution.
    // ====================================================================================

    /// <summary>
    /// Patch ChallengeObjectiveGather.HandleUpdatingCurrent to add container items.
    /// 
    /// This is the CORE patch. HandleUpdatingCurrent is called when the challenge recounts
    /// items (triggered by DragAndDropItemChanged). It sets base.Current from bag + toolbelt.
    /// 
    /// Our Postfix adds container items to the count and SETS the Current field.
    /// The SET is critical - without it, the UI doesn't update even if counting is correct.
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.Low)]
    private static class ChallengeObjectiveGather_HandleUpdatingCurrent_Patch
    {
        private static Type gatherType;
        private static Type trackedItemType;
        private static FieldInfo expectedItemField;
        private static FieldInfo currentField;
        private static FieldInfo maxCountField;

        static ChallengeObjectiveGather_HandleUpdatingCurrent_Patch()
        {
            FileLog("HandleUpdatingCurrent_Patch: Static constructor called");
            gatherType = AccessTools.TypeByName("Challenges.ChallengeObjectiveGather");
            trackedItemType = AccessTools.TypeByName("Challenges.ChallengeBaseTrackedItemObjective");
            var baseObjType = AccessTools.TypeByName("Challenges.BaseChallengeObjective");
            
            if (trackedItemType != null)
            {
                expectedItemField = AccessTools.Field(trackedItemType, "expectedItem");
            }
            if (baseObjType != null)
            {
                currentField = AccessTools.Field(baseObjType, "current");
                maxCountField = AccessTools.Field(baseObjType, "MaxCount");
            }
            FileLog($"HandleUpdatingCurrent_Patch: gatherType={gatherType?.Name}, currentField={currentField?.Name}");
        }

        static MethodBase TargetMethod()
        {
            FileLog("HandleUpdatingCurrent_Patch: TargetMethod called");
            if (gatherType == null)
            {
                FileLog("HandleUpdatingCurrent_Patch: gatherType is NULL");
                return null;
            }
            var method = AccessTools.Method(gatherType, "HandleUpdatingCurrent");
            FileLog($"HandleUpdatingCurrent_Patch: TargetMethod returning {method?.Name ?? "NULL"}");
            return method;
        }

        public static void Postfix(object __instance)
        {
            // Modify 'current' field to include container items
            // This is safe because we're using DragAndDropItemChanged (not OnBackpackItemsChangedInternal)
            
            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            if (!IsGameReady())
                return;

            try
            {
                var expectedItem = expectedItemField?.GetValue(__instance) as ItemValue;
                if (expectedItem == null || expectedItem.IsEmpty())
                    return;

                int inventoryCount = (int)(currentField?.GetValue(__instance) ?? 0);
                int maxCount = (int)(maxCountField?.GetValue(__instance) ?? 0);
                int containerCount = ContainerManager.GetItemCount(Config, expectedItem);
                int totalCount = inventoryCount + containerCount;
                
                // Cap at maxCount
                int displayCount = Math.Min(totalCount, maxCount);
                
                // SET the current field to include container items
                // Only log when we actually find containers items (reduce spam)
                if (containerCount > 0 && currentField != null && displayCount != inventoryCount)
                {
                    currentField.SetValue(__instance, displayCount);
                    FileLog($"[CHALLENGE] {expectedItem.ItemClass?.GetItemName()}: inv={inventoryCount} + containers={containerCount} = {displayCount}/{maxCount}");
                }
            }
            catch (Exception ex)
            {
                FileLog($"HandleUpdatingCurrent_Patch: Exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch BaseChallengeObjective.get_StatusText to display container items in the count.
    /// StatusText returns the "X/Y" format that's displayed in the UI.
    /// NOTE: With HandleUpdatingCurrent patch, this may be redundant but kept as backup.
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.Low)]
    private static class ChallengeObjective_StatusText_Patch
    {
        private static Type gatherType;
        private static Type trackedItemType;
        private static FieldInfo expectedItemField;
        private static FieldInfo currentField;
        private static FieldInfo maxCountField;

        static ChallengeObjective_StatusText_Patch()
        {
            FileLog("StatusText_Patch: Static constructor called");
            gatherType = AccessTools.TypeByName("Challenges.ChallengeObjectiveGather");
            trackedItemType = AccessTools.TypeByName("Challenges.ChallengeBaseTrackedItemObjective");
            var baseObjType = AccessTools.TypeByName("Challenges.BaseChallengeObjective");
            
            if (trackedItemType != null)
            {
                expectedItemField = AccessTools.Field(trackedItemType, "expectedItem");
            }
            if (baseObjType != null)
            {
                currentField = AccessTools.Field(baseObjType, "current");
                maxCountField = AccessTools.Field(baseObjType, "MaxCount");
            }
            FileLog($"StatusText_Patch: gatherType={gatherType?.Name}, expectedItemField={expectedItemField?.Name}");
        }

        static MethodBase TargetMethod()
        {
            FileLog("StatusText_Patch: TargetMethod called");
            var baseObjType = AccessTools.TypeByName("Challenges.BaseChallengeObjective");
            if (baseObjType == null)
            {
                FileLog("StatusText_Patch: baseObjType is NULL");
                return null;
            }
            var prop = baseObjType.GetProperty("StatusText");
            var method = prop?.GetGetMethod();
            FileLog($"StatusText_Patch: TargetMethod returning {method?.Name ?? "NULL"}");
            return method;
        }

        public static void Postfix(object __instance, ref string __result)
        {
            // Only process gather-type objectives
            if (gatherType == null || !gatherType.IsInstanceOfType(__instance))
                return;
            
            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            if (!IsGameReady())
                return;

            if (string.IsNullOrEmpty(__result))
                return;

            try
            {
                var expectedItem = expectedItemField?.GetValue(__instance) as ItemValue;
                if (expectedItem == null || expectedItem.IsEmpty())
                    return;

                // 'current' = gathering progress (how many harvested), NOT actual inventory count!
                int gatherProgress = (int)(currentField?.GetValue(__instance) ?? 0);
                int maxCount = (int)(maxCountField?.GetValue(__instance) ?? 0);
                
                // Get ACTUAL possession: player inventory + containers
                // We need to query the player's actual inventory, not use 'current'
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;
                
                // Count in player backpack + toolbelt (actual possession)
                int playerBagCount = player.bag?.GetItemCount(expectedItem) ?? 0;
                int playerToolbeltCount = player.inventory?.GetItemCount(expectedItem) ?? 0;
                int actualInventory = playerBagCount + playerToolbeltCount;
                
                // Count in containers
                int containerCount = ContainerManager.GetItemCount(Config, expectedItem);
                
                // Only process if containers have items (reduce spam)
                if (containerCount <= 0)
                    return;
                
                // Total possession = actual inventory + containers
                int totalPossession = actualInventory + containerCount;
                
                // Cap display at maxCount
                int displayCount = Math.Min(totalPossession, maxCount);

                // Replace the gather progress with total possession in the display
                if (totalPossession != gatherProgress)
                {
                    string oldPattern = $"{gatherProgress}/";
                    string newPattern = $"{displayCount}/";
                    
                    if (__result.Contains(oldPattern))
                    {
                        __result = __result.Replace(oldPattern, newPattern);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog($"StatusText_Patch: Exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch ChallengeObjectiveGather.CheckObjectiveComplete to consider container items.
    /// Must also set Complete=true to trigger green highlighting via ChallengeState.
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.Low)]
    private static class ChallengeObjectiveGather_CheckComplete_Patch
    {
        private static Type gatherType;
        private static Type trackedItemType;
        private static FieldInfo expectedItemField;
        private static FieldInfo currentField;
        private static FieldInfo maxCountField;
        private static PropertyInfo completeProperty;

        static ChallengeObjectiveGather_CheckComplete_Patch()
        {
            gatherType = AccessTools.TypeByName("Challenges.ChallengeObjectiveGather");
            trackedItemType = AccessTools.TypeByName("Challenges.ChallengeBaseTrackedItemObjective");
            var baseObjType = AccessTools.TypeByName("Challenges.BaseChallengeObjective");
            
            if (trackedItemType != null)
            {
                expectedItemField = AccessTools.Field(trackedItemType, "expectedItem");
            }
            if (baseObjType != null)
            {
                currentField = AccessTools.Field(baseObjType, "current");
                maxCountField = AccessTools.Field(baseObjType, "MaxCount");
                completeProperty = AccessTools.Property(baseObjType, "Complete");
            }
        }

        static MethodBase TargetMethod()
        {
            if (gatherType == null)
            {
                return null;
            }
            return gatherType.GetMethod("CheckObjectiveComplete", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        public static void Postfix(object __instance, ref bool __result, bool handleComplete)
        {
            // If vanilla already said complete, no need to do anything
            if (__result)
                return;
            
            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            if (!IsGameReady())
                return;

            try
            {
                var expectedItem = expectedItemField?.GetValue(__instance) as ItemValue;
                if (expectedItem == null || expectedItem.IsEmpty())
                    return;

                int maxCount = (int)(maxCountField?.GetValue(__instance) ?? 0);
                
                // Get ACTUAL possession: player inventory + containers
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;
                
                // Count in player backpack + toolbelt (actual possession)
                int playerBagCount = player.bag?.GetItemCount(expectedItem) ?? 0;
                int playerToolbeltCount = player.inventory?.GetItemCount(expectedItem) ?? 0;
                int actualInventory = playerBagCount + playerToolbeltCount;
                
                // Count in containers
                int containerCount = ContainerManager.GetItemCount(Config, expectedItem);
                
                // Only process if containers have items (reduce spam)
                if (containerCount <= 0)
                    return;
                
                // Total possession = actual inventory + containers
                int totalPossession = actualInventory + containerCount;
                
                if (totalPossession >= maxCount)
                {
                    __result = true;
                    
                    // CRITICAL: Must set Complete property to trigger green highlighting!
                    // The Complete property setter triggers Owner.HandleComplete() which sets ChallengeState = Completed
                    // Only do this if handleComplete is true (respecting the original intent)
                    if (handleComplete && completeProperty != null)
                    {
                        bool currentComplete = (bool)(completeProperty.GetValue(__instance) ?? false);
                        if (!currentComplete)
                        {
                            completeProperty.SetValue(__instance, true);
                            FileLog($"[CHALLENGE] {expectedItem.ItemClass?.GetItemName()} completed via containers (inv={actualInventory}, containers={containerCount})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog($"CheckComplete_Patch: Exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Cache container reference on open - needed for accurate counting.
    /// Also invalidates item count cache to ensure fresh data.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_LootContainer), "OnOpen")]
    [HarmonyPriority(Priority.Low)]  
    private static class LootContainer_OnOpen_Patch
    {
        public static void Postfix(XUiC_LootContainer __instance)
        {
            try
            {
                var teField = typeof(XUiC_LootContainer).GetField("localTileEntity", 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                var lootable = teField?.GetValue(__instance) as ITileEntityLootable;
                
                if (lootable != null)
                {
                    ContainerManager.CurrentOpenContainer = lootable;
                    
                    // Check if this is a drone's lootContainer
                    // Drones have EntityId set on their lootContainer
                    int entityId = lootable.EntityId;
                    if (entityId != -1)
                    {
                        var entity = GameManager.Instance?.World?.GetEntity(entityId);
                        if (entity is EntityDrone drone)
                        {
                            ContainerManager.CurrentOpenDrone = drone;
                            FileLog($"[CACHE] OnOpen: Set open drone entityId={entityId}");
                        }
                    }
                    
                    // Get world position - handle both TileEntity and ITileEntity (like TEFeatureStorage)
                    // ITileEntity has ToWorldPos() method, but TEFeatureStorage is NOT a TileEntity class
                    Vector3i pos = Vector3i.zero;
                    if (lootable is TileEntity te)
                    {
                        pos = te.ToWorldPos();
                    }
                    else if (lootable is ITileEntity ite)
                    {
                        // TEFeatureStorage implements ITileEntity which has ToWorldPos()
                        pos = ite.ToWorldPos();
                    }
                    ContainerManager.CurrentOpenContainerPos = pos;
                    ContainerManager.InvalidateCache(); // Force recount with new open container
                    FileLog($"[CACHE] OnOpen: Set open container at {ContainerManager.CurrentOpenContainerPos}");
                }
            }
            catch (Exception ex)
            {
                FileLog($"LootContainer_OnOpen_Patch: Exception: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Clear cached container reference on close.
    /// Also invalidates item count cache to ensure fresh data.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_LootContainer), "OnClose")]
    [HarmonyPriority(Priority.Low)]
    private static class LootContainer_OnClose_Patch
    {
        public static void Postfix()
        {
            ContainerManager.CurrentOpenContainer = null;
            ContainerManager.CurrentOpenContainerPos = Vector3i.zero;
            ContainerManager.CurrentOpenDrone = null; // Clear drone reference too
            ContainerManager.InvalidateCache(); // Force recount without open container
            FileLog("[CACHE] OnClose: Cleared");
        }
    }
    
    /// <summary>
    /// When a container slot changes, fire the DragAndDropItemChanged event.
    /// Challenges already listen to this event, so they will recount items.
    /// Also invalidates item count cache for fresh data, refreshes recipe tracker,
    /// and marks HUD ammo counter as dirty.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent")]
    [HarmonyPriority(Priority.Low)]
    private static class LootContainer_SlotChanged_Patch
    {
        public static void Postfix()
        {
            // Always invalidate cache when container contents change
            ContainerManager.InvalidateCache();

            // Refresh recipe tracker to show updated ingredient counts
            RefreshRecipeTracker();

            // Mark HUD ammo counter as dirty to refresh display
            MarkHudAmmoDirty();

            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;

                // Fire DragAndDropItemChanged - challenges listen to this
                var eventField = typeof(EntityPlayerLocal).GetField("DragAndDropItemChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(player) as Delegate;
                    eventDelegate?.DynamicInvoke();
                    FileLog("[RECOUNT] Fired DragAndDropItemChanged from container slot change");
                }
            }
            catch (Exception ex)
            {
                FileLog($"LootContainer_SlotChanged_Patch: Exception: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Patch XUiC_ItemStack.HandleSlotChangeEvent to trigger challenge recounts
    /// for ANY slot change (inventory, toolbelt, or container).
    /// Only fires when a container is open, and uses DragAndDropItemChanged
    /// which is safe (challenges already listen to it).
    /// </summary>
    [HarmonyPatch(typeof(XUiC_ItemStack), "HandleSlotChangeEvent")]
    [HarmonyPriority(Priority.Low)]
    private static class ItemStack_SlotChanged_Patch
    {
        public static void Postfix(XUiC_ItemStack __instance)
        {
            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;
            
            // Only trigger when a container or vehicle storage is open
            if (ContainerManager.CurrentOpenContainer == null && ContainerManager.CurrentOpenVehicle == null)
                return;
            
            // Skip if this is a container slot - already handled by LootContainer_SlotChanged_Patch
            if (__instance.StackLocation == XUiC_ItemStack.StackLocationTypes.LootContainer)
                return;
            
            // Skip if this is a vehicle slot - already handled by VehicleContainer_SlotChanged_Patch
            if (__instance.StackLocation == XUiC_ItemStack.StackLocationTypes.Vehicle)
                return;
            
            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;
                
                // Fire DragAndDropItemChanged - challenges listen to this
                var eventField = typeof(EntityPlayerLocal).GetField("DragAndDropItemChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(player) as Delegate;
                    eventDelegate?.DynamicInvoke();
                    FileLog($"[RECOUNT] Fired DragAndDropItemChanged from {__instance.StackLocation} slot change");
                }
            }
            catch (Exception ex)
            {
                FileLog($"ItemStack_SlotChanged_Patch: Exception: {ex.Message}");
            }
        }
    }

    // ====================================================================================
    // VEHICLE STORAGE PATCHES
    // ====================================================================================
    // Vehicle storage uses XUiC_VehicleContainer and XUiC_VehicleStorageWindowGroup,
    // NOT XUiC_LootContainer. We need separate patches to:
    // 1. Track when vehicle storage is opened/closed
    // 2. Invalidate cache and fire events when items are moved
    // ====================================================================================
    
    /// <summary>
    /// Track when vehicle storage window is opened.
    /// Sets CurrentOpenVehicle so we can skip counting it separately.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_VehicleStorageWindowGroup), "OnOpen")]
    [HarmonyPriority(Priority.Low)]
    private static class VehicleStorageWindowGroup_OnOpen_Patch
    {
        public static void Postfix(XUiC_VehicleStorageWindowGroup __instance)
        {
            try
            {
                var vehicleField = typeof(XUiC_VehicleStorageWindowGroup).GetField("currentVehicleEntity",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                var vehicle = vehicleField?.GetValue(__instance) as EntityVehicle;
                
                if (vehicle != null)
                {
                    ContainerManager.CurrentOpenVehicle = vehicle;
                    ContainerManager.InvalidateCache();
                    FileLog($"[CACHE] VehicleStorageWindowGroup.OnOpen: Set open vehicle {vehicle.EntityName}");
                }
            }
            catch (Exception ex)
            {
                FileLog($"VehicleStorageWindowGroup_OnOpen_Patch: Exception: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Track when vehicle storage window is closed.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_VehicleStorageWindowGroup), "OnClose")]
    [HarmonyPriority(Priority.Low)]
    private static class VehicleStorageWindowGroup_OnClose_Patch
    {
        public static void Postfix()
        {
            ContainerManager.CurrentOpenVehicle = null;
            ContainerManager.InvalidateCache();
            FileLog("[CACHE] VehicleStorageWindowGroup.OnClose: Cleared");
        }
    }
    
    /// <summary>
    /// Track when items are moved in vehicle storage.
    /// Fire DragAndDropItemChanged to update challenge counts.
    /// Also refreshes recipe tracker and HUD ammo counter.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_VehicleContainer), "HandleLootSlotChangedEvent")]
    [HarmonyPriority(Priority.Low)]
    private static class VehicleContainer_SlotChanged_Patch
    {
        public static void Postfix()
        {
            // Always invalidate cache when vehicle contents change
            ContainerManager.InvalidateCache();

            // Refresh recipe tracker to show updated ingredient counts
            RefreshRecipeTracker();

            // Mark HUD ammo counter as dirty to refresh display
            MarkHudAmmoDirty();

            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;

                // Fire DragAndDropItemChanged - challenges listen to this
                var eventField = typeof(EntityPlayerLocal).GetField("DragAndDropItemChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(player) as Delegate;
                    eventDelegate?.DynamicInvoke();
                    FileLog("[RECOUNT] Fired DragAndDropItemChanged from vehicle slot change");
                }
            }
            catch (Exception ex)
            {
                FileLog($"VehicleContainer_SlotChanged_Patch: Exception: {ex.Message}");
            }
        }
    }

    // ====================================================================================
    // WORKSTATION STORAGE PATCHES
    // ====================================================================================
    // Workstations (forge, workbench, chemistry station, etc.) have multiple slot types:
    // - Tool slots: Required tools for crafting
    // - Input slots: Materials being processed (smelting ore, etc.)
    // - Fuel slots: Fuel being consumed
    // - Output slots: Finished products <-- THIS IS WHAT WE COUNT
    //
    // We ONLY count from OUTPUT slots. Input/fuel slots contain items that are being
    // consumed for functionality and should NOT be counted as available materials.
    // ====================================================================================
    
    /// <summary>
    /// Track when workstation window is opened.
    /// Sets CurrentOpenWorkstation so we can count from its Output properly.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_WorkstationWindowGroup), "OnOpen")]
    [HarmonyPriority(Priority.Low)]
    private static class WorkstationWindowGroup_OnOpen_Patch
    {
        public static void Postfix(XUiC_WorkstationWindowGroup __instance)
        {
            try
            {
                var workstationDataField = typeof(XUiC_WorkstationWindowGroup).GetField("WorkstationData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                var workstationData = workstationDataField?.GetValue(__instance);
                if (workstationData != null)
                {
                    // XUiM_Workstation has a TileEntity property
                    var teProperty = workstationData.GetType().GetProperty("TileEntity");
                    var workstation = teProperty?.GetValue(workstationData) as TileEntityWorkstation;
                    
                    if (workstation != null)
                    {
                        ContainerManager.CurrentOpenWorkstation = workstation;
                        ContainerManager.InvalidateCache();
                        FileLog($"[CACHE] WorkstationWindowGroup.OnOpen: Set open workstation at {workstation.ToWorldPos()}");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog($"WorkstationWindowGroup_OnOpen_Patch: Exception: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Track when workstation window is closed.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_WorkstationWindowGroup), "OnClose")]
    [HarmonyPriority(Priority.Low)]
    private static class WorkstationWindowGroup_OnClose_Patch
    {
        public static void Postfix()
        {
            ContainerManager.CurrentOpenWorkstation = null;
            ContainerManager.InvalidateCache();
            FileLog("[CACHE] WorkstationWindowGroup.OnClose: Cleared");
        }
    }
    
    /// <summary>
    /// Track when items are moved in workstation OUTPUT grid.
    /// Fire DragAndDropItemChanged to update challenge counts.
    /// Only tracks output grid - we don't want to track input/fuel/tool grids.
    /// Also refreshes recipe tracker and HUD ammo counter.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_WorkstationOutputGrid), "UpdateBackend")]
    [HarmonyPriority(Priority.Low)]
    private static class WorkstationOutputGrid_UpdateBackend_Patch
    {
        public static void Postfix()
        {
            // Always invalidate cache when workstation output changes
            ContainerManager.InvalidateCache();

            // Refresh recipe tracker to show updated ingredient counts
            RefreshRecipeTracker();

            // Mark HUD ammo counter as dirty to refresh display
            MarkHudAmmoDirty();

            if (!Config?.modEnabled == true || !Config?.enableForQuests == true)
                return;

            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                    return;

                // Fire DragAndDropItemChanged - challenges listen to this
                var eventField = typeof(EntityPlayerLocal).GetField("DragAndDropItemChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(player) as Delegate;
                    eventDelegate?.DynamicInvoke();
                    FileLog("[RECOUNT] Fired DragAndDropItemChanged from workstation output change");
                }
            }
            catch (Exception ex)
            {
                FileLog($"WorkstationOutputGrid_UpdateBackend_Patch: Exception: {ex.Message}");
            }
        }
    }

    #endregion

    #region Harmony Patches - Painting Support

    // ====================================================================================
    // PAINTING SUPPORT
    // ====================================================================================
    //
    // Allows painting blocks using paint from nearby containers.
    // Uses PREFIX patches to replace the vanilla checkAmmo and decreaseAmmo methods.
    //
    // How it works:
    // 1. checkAmmo - Returns true if player has enough paint (inventory + containers)
    // 2. decreaseAmmo - Removes paint from player inventory first, then from containers
    //
    // Reference: BeyondStorage2's ItemActionTextureBlock_Patches.cs
    // ====================================================================================

    /// <summary>
    /// Patch ItemActionTextureBlock.checkAmmo to check containers for paint.
    /// Uses PREFIX to replace vanilla logic with container-aware version.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionTextureBlock), nameof(ItemActionTextureBlock.checkAmmo))]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionTextureBlock_checkAmmo_Patch
    {
        public static bool Prefix(ItemActionTextureBlock __instance, ItemActionData _actionData, ref bool __result)
        {
            // Skip if feature disabled
            if (!Config?.modEnabled == true || !Config?.enableForPainting == true)
                return true; // Run original method

            try
            {
                // Handle infinite ammo and creative modes (same as original)
                if (__instance.InfiniteAmmo ||
                    GameStats.GetInt(EnumGameStats.GameModeId) == 2 || // Creative
                    GameStats.GetInt(EnumGameStats.GameModeId) == 8)   // Test
                {
                    __result = true;
                    return false; // Skip original method
                }

                // Get current paint item
                var paintItem = __instance.currentMagazineItem;
                if (paintItem == null)
                    return true; // Let original handle it

                // Get entity-held ammo count
                EntityAlive holdingEntity = _actionData.invData.holdingEntity;
                int bagCount = holdingEntity.bag?.GetItemCount(paintItem) ?? 0;
                int inventoryCount = holdingEntity.inventory?.GetItemCount(paintItem) ?? 0;
                int entityAvailable = bagCount + inventoryCount;

                // If player has enough, no need to check containers
                if (entityAvailable > 0)
                {
                    __result = true;
                    return false;
                }

                // Check containers for paint
                int containerCount = ContainerManager.GetItemCount(Config, paintItem);
                __result = containerCount > 0;

                LogDebug($"checkAmmo: Paint={paintItem.ItemClass?.GetItemName()}, entity={entityAvailable}, containers={containerCount}, result={__result}");

                return false; // Skip original method
            }
            catch (Exception ex)
            {
                LogWarning($"Error in checkAmmo patch: {ex.Message}");
                return true; // Let original handle it on error
            }
        }
    }

    /// <summary>
    /// Patch ItemActionTextureBlock.decreaseAmmo to use paint from containers.
    /// Uses PREFIX to replace vanilla logic with container-aware version.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionTextureBlock), nameof(ItemActionTextureBlock.decreaseAmmo))]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionTextureBlock_decreaseAmmo_Patch
    {
        public static bool Prefix(ItemActionTextureBlock __instance, ItemActionData _actionData, ref bool __result)
        {
            // Skip if feature disabled
            if (!Config?.modEnabled == true || !Config?.enableForPainting == true)
                return true; // Run original method

            try
            {
                // Handle infinite ammo and creative modes (same as original)
                if (__instance.InfiniteAmmo ||
                    GameStats.GetInt(EnumGameStats.GameModeId) == 2 || // Creative
                    GameStats.GetInt(EnumGameStats.GameModeId) == 8)   // Test
                {
                    __result = true;
                    return false; // Skip original method
                }

                // Get the action data and paint cost
                var textureBlockData = _actionData as ItemActionTextureBlock.ItemActionTextureBlockData;
                if (textureBlockData == null)
                    return true; // Let original handle it

                int paintCost = BlockTextureData.list[textureBlockData.idx].PaintCost;

                EntityAlive holdingEntity = _actionData.invData.holdingEntity;
                var paintItem = __instance.currentMagazineItem;

                // Calculate entity-held ammo
                int bagCount = holdingEntity.bag?.GetItemCount(paintItem) ?? 0;
                int inventoryCount = holdingEntity.inventory?.GetItemCount(paintItem) ?? 0;
                int entityAvailable = bagCount + inventoryCount;

                // Get total available including storage
                int containerCount = ContainerManager.GetItemCount(Config, paintItem);
                int totalAvailable = entityAvailable + containerCount;

                // Check if we have enough total ammo
                if (totalAvailable < paintCost)
                {
                    __result = false;
                    return false; // Skip original method
                }

                // Remove ammo from entity inventory first (same priority as original)
                int remainingNeeded = paintCost;

                // Remove from bag first
                if (remainingNeeded > 0 && holdingEntity.bag != null)
                {
                    remainingNeeded -= holdingEntity.bag.DecItem(paintItem, remainingNeeded);
                }

                // Then from toolbelt/inventory
                if (remainingNeeded > 0 && holdingEntity.inventory != null)
                {
                    remainingNeeded -= holdingEntity.inventory.DecItem(paintItem, remainingNeeded);
                }

                // Remove any remaining needed from containers
                if (remainingNeeded > 0)
                {
                    int removed = ContainerManager.RemoveItems(Config, paintItem, remainingNeeded);
                    LogDebug($"decreaseAmmo: Removed {removed} paint from containers (needed {remainingNeeded})");
                    remainingNeeded -= removed;
                }

                __result = remainingNeeded <= 0;
                return false; // Skip original method
            }
            catch (Exception ex)
            {
                LogWarning($"Error in decreaseAmmo patch: {ex.Message}");
                return true; // Let original handle it on error
            }
        }
    }

    #endregion

    #region Harmony Patches - Block Upgrade Support

    // ====================================================================================
    // BLOCK UPGRADE SUPPORT
    // ====================================================================================
    //
    // Allows upgrading blocks using materials from nearby containers.
    // Uses POSTFIX patches to extend vanilla behavior:
    // 1. CanRemoveRequiredResource - If vanilla says NO, check if containers have enough
    // 2. RemoveRequiredResource - If vanilla fails, remove from containers
    //
    // LESSONS LEARNED:
    // - Prefer POSTFIX over TRANSPILER for stability
    // - Let vanilla try first, then extend if needed
    // - Don't replace entire methods unless absolutely necessary
    //
    // Reference: BeyondStorage2's ItemActionRepair_Upgrade_Patches.cs
    // ====================================================================================

    /// <summary>
    /// Patch ItemActionRepair.CanRemoveRequiredResource to check containers for upgrade materials.
    /// Uses POSTFIX to add container check if vanilla returns false.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionRepair), "CanRemoveRequiredResource")]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionRepair_CanRemoveRequiredResource_Patch
    {
        public static void Postfix(ItemActionRepair __instance, ref bool __result, ItemInventoryData data, BlockValue blockValue)
        {
            // If vanilla succeeded or mod disabled, no need to intervene
            if (__result || !Config?.modEnabled == true || !Config?.enableForRepairAndUpgrade == true)
                return;

            try
            {
                Block block = blockValue.Block;
                if (block == null)
                    return;

                // Get upgrade item name (mirrors vanilla logic)
                string upgradeItemName = GetUpgradeItemName(__instance, block);
                if (string.IsNullOrEmpty(upgradeItemName))
                    return;

                // Get required count from block properties
                if (!int.TryParse(block.Properties.Values[Block.PropUpgradeBlockClassItemCount], out int requiredCount))
                    return;

                // Get item value for the upgrade material
                ItemValue itemValue = ItemClass.GetItem(upgradeItemName);
                if (itemValue == null || itemValue.IsEmpty())
                    return;

                // Check how much player has (inventory + bag)
                int playerCount = 0;
                if (data?.holdingEntity != null)
                {
                    playerCount += data.holdingEntity.inventory?.GetItemCount(itemValue) ?? 0;
                    playerCount += data.holdingEntity.bag?.GetItemCount(itemValue) ?? 0;
                }

                // Check containers - with Enhanced Safety check if enabled
                int containerCount;
                if (Config.enhancedSafetyRepair)
                {
                    // Enhanced Safety mode: Check multiplayer safety first
                    if (!MultiplayerModTracker.IsModAllowed())
                    {
                        LogDebug($"CanRemoveRequiredResource (enhanced): MP locked, skipping storage check");
                        return;
                    }
                    containerCount = ContainerManager.GetItemCount(Config, itemValue);
                }
                else
                {
                    // Legacy mode: Direct ContainerManager access
                    containerCount = ContainerManager.GetItemCount(Config, itemValue);
                }
                int totalAvailable = playerCount + containerCount;

                if (totalAvailable >= requiredCount)
                {
                    __result = true;
                    LogDebug($"CanRemoveRequiredResource: {upgradeItemName} x{requiredCount}, player={playerCount}, containers={containerCount} -> allowed");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in CanRemoveRequiredResource patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the upgrade item name from a block, mirroring vanilla's GetUpgradeItemName logic.
        /// </summary>
        private static string GetUpgradeItemName(ItemActionRepair instance, Block block)
        {
            // Check UpgradeBlock.Item property first
            if (block.Properties.Values.TryGetValue("UpgradeBlock.Item", out string upgradeItem) &&
                !string.IsNullOrEmpty(upgradeItem))
            {
                return upgradeItem;
            }

            // Fall back to RepairItems if available
            if (block.RepairItems != null && block.RepairItems.Count > 0)
            {
                return block.RepairItems[0].ItemName;
            }

            return null;
        }
    }

    /// <summary>
    /// Patch ItemActionRepair.RemoveRequiredResource to remove upgrade materials from containers.
    /// Uses POSTFIX to remove from containers if vanilla fails to remove everything from player.
    /// 
    /// IMPORTANT: Vanilla returns false if it can't remove ALL items from one source.
    /// It tries inventory first, then bag - all-or-nothing from each.
    /// Our postfix kicks in when vanilla fails, meaning player doesn't have enough in either source.
    /// </summary>
    [HarmonyPatch(typeof(ItemActionRepair), "RemoveRequiredResource")]
    [HarmonyPriority(Priority.Low)]
    private static class ItemActionRepair_RemoveRequiredResource_Patch
    {
        public static void Postfix(ItemActionRepair __instance, ref bool __result, ItemInventoryData data, BlockValue blockValue)
        {
            // If vanilla succeeded or mod disabled, no need to intervene
            if (__result || !Config?.modEnabled == true || !Config?.enableForRepairAndUpgrade == true)
                return;

            // Enhanced Safety check at the top level
            if (Config.enhancedSafetyRepair && !MultiplayerModTracker.IsModAllowed())
            {
                LogDebug($"RemoveRequiredResource (enhanced): MP locked, skipping storage removal");
                return;
            }

            try
            {
                Block block = blockValue.Block;
                if (block == null)
                    return;

                // Get upgrade item name (same logic as CanRemoveRequiredResource)
                string upgradeItemName = GetUpgradeItemName(block);
                if (string.IsNullOrEmpty(upgradeItemName))
                    return;

                // Get required count from block properties
                if (!int.TryParse(block.Properties.Values[Block.PropUpgradeBlockClassItemCount], out int requiredCount))
                    return;

                // Get item value for the upgrade material
                ItemValue itemValue = ItemClass.GetItem(upgradeItemName);
                if (itemValue == null || itemValue.IsEmpty())
                    return;

                // Calculate how much the player has across both sources
                int inventoryCount = data?.holdingEntity?.inventory?.GetItemCount(itemValue) ?? 0;
                int bagCount = data?.holdingEntity?.bag?.GetItemCount(itemValue) ?? 0;
                int playerTotal = inventoryCount + bagCount;

                // Check containers
                int containerCount = ContainerManager.GetItemCount(Config, itemValue);
                int totalAvailable = playerTotal + containerCount;

                // If we don't have enough total, bail
                if (totalAvailable < requiredCount)
                    return;

                // Remove in order: inventory -> bag -> containers
                int remaining = requiredCount;

                // Remove from inventory first
                if (remaining > 0 && data?.holdingEntity?.inventory != null)
                {
                    int removed = data.holdingEntity.inventory.DecItem(itemValue, remaining);
                    remaining -= removed;
                }

                // Then bag
                if (remaining > 0 && data?.holdingEntity?.bag != null)
                {
                    int removed = data.holdingEntity.bag.DecItem(itemValue, remaining);
                    remaining -= removed;
                }

                // Finally containers
                if (remaining > 0)
                {
                    int removed = ContainerManager.RemoveItems(Config, itemValue, remaining);
                    remaining -= removed;
                    LogDebug($"RemoveRequiredResource: Removed {removed} {upgradeItemName} from containers (needed {requiredCount})");
                }

                // Show UI harvesting indicator (like vanilla does)
                if (remaining <= 0)
                {
                    var entityPlayerLocal = data?.holdingEntity as EntityPlayerLocal;
                    if (entityPlayerLocal != null && requiredCount != 0)
                    {
                        entityPlayerLocal.AddUIHarvestingItem(new ItemStack(itemValue, -requiredCount));
                    }
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in RemoveRequiredResource patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the upgrade item name from a block, mirroring vanilla's GetUpgradeItemName logic.
        /// </summary>
        private static string GetUpgradeItemName(Block block)
        {
            // Check UpgradeBlock.Item property first
            if (block.Properties.Values.TryGetValue("UpgradeBlock.Item", out string upgradeItem) &&
                !string.IsNullOrEmpty(upgradeItem))
            {
                return upgradeItem;
            }

            // Fall back to RepairItems if available
            if (block.RepairItems != null && block.RepairItems.Count > 0)
            {
                return block.RepairItems[0].ItemName;
            }

            return null;
        }
    }

    #endregion

    #region Harmony Patches - Generator/Power Source Refuel Support

    // ====================================================================================
    // GENERATOR REFUEL SUPPORT
    // ====================================================================================
    //
    // Allows refueling generators/power sources using fuel from nearby containers.
    // Uses TRANSPILER to replace Bag.DecItem with our wrapper that also checks containers.
    //
    // Reference: BeyondStorage2's XUiC_PowerSourceStats_Patches.cs
    // ====================================================================================

    /// <summary>
    /// Patch XUiC_PowerSourceStats.BtnRefuel_OnPress to use fuel from containers.
    /// Uses TRANSPILER to replace Bag.DecItem with our wrapper.
    ///
    /// ROBUSTNESS:
    /// - Uses signature-based method matching (survives minor refactors)
    /// - Returns original code if pattern not found (no crash)
    /// - Records status for runtime feature checks
    /// </summary>
    [HarmonyPatch(typeof(XUiC_PowerSourceStats), nameof(XUiC_PowerSourceStats.BtnRefuel_OnPress))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_PowerSourceStats_BtnRefuel_OnPress_Patch
    {
        private const string FEATURE_ID = "GeneratorRefuel";

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Config?.enableForGeneratorRefuel == true)
                return instructions;

            return RobustTranspiler.SafeTranspile(instructions, FEATURE_ID, codes =>
            {
                var replacementMethod = AccessTools.Method(typeof(ProxiCraft), nameof(DecItemForGeneratorRefuel));

                // Try to find and replace Bag.DecItem call
                // Fallback patterns for if the method gets renamed
                return RobustTranspiler.TryReplaceMethodCall(
                    codes,
                    typeof(Bag),
                    nameof(Bag.DecItem),
                    replacementMethod,
                    FEATURE_ID,
                    targetParamTypes: null,
                    occurrence: 1,
                    fallbackNamePatterns: new[] { "Dec", "Remove", "Subtract", "Consume" });
            });
        }
    }

    /// <summary>
    /// Removes fuel from bag and containers for generator refueling.
    /// </summary>
    public static int DecItemForGeneratorRefuel(Bag bag, ItemValue item, int count, bool ignoreModded, IList<ItemStack> removedItems)
    {
        int removed = bag.DecItem(item, count, ignoreModded, removedItems);

        if (!Config?.modEnabled == true || !Config?.enableForGeneratorRefuel == true)
            return removed;

        // Safety check - don't access containers if game state isn't ready
        if (!IsGameReady())
            return removed;

        if (removed < count)
        {
            int remaining = count - removed;
            
            int containerRemoved;
            if (Config.enhancedSafetyRefuel)
            {
                // Enhanced Safety mode: Check multiplayer safety first
                if (!MultiplayerModTracker.IsModAllowed())
                {
                    LogDebug($"DecItemForGeneratorRefuel (enhanced): MP locked, skipping storage");
                    return removed;
                }
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            else
            {
                // Legacy mode: Direct ContainerManager access
                containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            }
            LogDebug($"Removed {containerRemoved} fuel from containers for generator");
            removed += containerRemoved;
        }

        return removed;
    }

    #endregion

    #region Harmony Patches - Lockpicking Support

    // ====================================================================================
    // LOCKPICKING SUPPORT
    // ====================================================================================
    //
    // Allows lockpicking using lockpicks from nearby containers.
    //
    // IMPLEMENTATION NOTE:
    // Lockpicking is already covered by our existing patches:
    // - XUiM_PlayerInventory.GetItemCount patch adds container counts
    // - XUiM_PlayerInventory.RemoveItems patch removes from containers
    //
    // The game's lockpicking code (BlockSecureLoot, TEFeatureLockPickable) uses
    // XUiM_PlayerInventory.GetItemCount to check for lockpicks, which we already patch.
    //
    // No additional patches needed - this is just a documentation note.
    // ====================================================================================

    #endregion

    #region Harmony Patches - Item Repair Support (Weapons/Tools)

    // ====================================================================================
    // ITEM REPAIR SUPPORT
    // ====================================================================================
    //
    // Allows repairing weapons/tools using repair kits from nearby containers.
    //
    // IMPLEMENTATION NOTE:
    // Item repair is already covered by our existing patches:
    // - XUiM_PlayerInventory.GetItemCount patch adds container counts (enables Repair button)
    // - XUiM_PlayerInventory.RemoveItems patch removes from containers (consumes repair kits)
    //
    // The game's ItemActionEntryRepair uses XUiM_PlayerInventory for both checking
    // and consuming repair kits, which we already patch.
    //
    // No additional patches needed - this is just a documentation note.
    // ====================================================================================

    #endregion

    #region Harmony Patches - Vehicle Repair Support

    // ====================================================================================
    // VEHICLE REPAIR SUPPORT - DESIGN DECISION
    // ====================================================================================
    //
    // Allows repairing vehicles using repair kits from nearby containers.
    //
    // WHY PREFIX PRE-STAGE instead of TRANSPILER?
    // --------------------------------------------
    // Vehicle repair always needs exactly 1 repair kit - FIXED amount. This makes
    // pre-staging simple: just transfer 1 kit before vanilla runs.
    //
    // Why NOT transpiler here:
    // - Vanilla's RepairVehicle intertwines kit consumption with repair logic
    // - It reads kit quality/properties to calculate repair amount
    // - Multiple IL points would need interception
    // - A transpiler would be complex and fragile
    //
    // APPROACH:
    // We use a PREFIX that "pre-stages" a repair kit from storage into the player's bag
    // before vanilla runs. This lets vanilla handle all the actual repair logic, sounds,
    // UI updates, perk bonuses, etc. - we just make sure the repair kit is available.
    //
    // CRITICAL: Must check CanStack() BEFORE removing from storage!
    // If player inventory is full, we can't pre-stage, and removing the kit would lose it.
    // Fixed in v1.2.1 - now fails gracefully if inventory is full.
    //
    // This approach is cleaner than replacing the entire method because:
    // 1. Any future vanilla changes to repair logic will still apply
    // 2. No transpiler needed - simple and robust
    // 3. Compatible with other mods that might also patch this method
    //
    // COMPARISON TO RELOAD/REFUEL:
    // | Feature        | Amount    | Approach    | Why                                |
    // |----------------|-----------|-------------|------------------------------------|
    // | Vehicle Repair | Fixed (1) | Pre-stage   | Simple, no calculations needed     |
    // | Reload         | Variable  | Transpiler  | Vanilla calculates count for us    |
    // | Refuel         | Variable  | Transpiler  | Vanilla calculates count for us    |
    //
    // ALTERNATIVE APPROACH (if current method has issues):
    // Could use transpiler to intercept the specific kit consumption call, but would
    // need to maintain the same ItemStack reference that vanilla uses for quality calc.
    // ====================================================================================

    /// <summary>
    /// Patch XUiM_Vehicle.RepairVehicle to support repair kits from nearby containers.
    /// Uses PREFIX to pre-stage a repair kit from storage before vanilla checks.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_Vehicle), nameof(XUiM_Vehicle.RepairVehicle))]
    [HarmonyPriority(Priority.High)]
    private static class XUiM_Vehicle_RepairVehicle_Patch
    {
        public static void Prefix(XUi _xui, Vehicle vehicle)
        {
            if (!Config?.modEnabled == true || !Config?.enableForItemRepair == true)
                return;

            if (!IsGameReady())
                return;

            // Enhanced Safety check at the top level
            if (Config.enhancedSafetyVehicle && !MultiplayerModTracker.IsModAllowed())
            {
                LogDebug($"Vehicle repair (enhanced): MP locked, skipping storage pre-stage");
                return;
            }

            try
            {
                // Resolve vehicle if not provided
                if (vehicle == null)
                    vehicle = _xui?.vehicle?.GetVehicle();

                if (vehicle == null)
                    return;

                // Check if repair is actually needed
                if (vehicle.GetRepairAmountNeeded() <= 0)
                    return;

                EntityPlayerLocal player = _xui?.playerUI?.entityPlayer;
                if (player == null)
                    return;

                ItemValue repairKitItem = ItemClass.GetItem("resourceRepairKit");
                if (repairKitItem?.ItemClass == null)
                    return;

                // Check if player already has repair kits in bag or toolbelt
                int bagCount = player.bag?.GetItemCount(repairKitItem) ?? 0;
                int toolbeltCount = player.inventory?.GetItemCount(repairKitItem) ?? 0;

                if (bagCount + toolbeltCount > 0)
                {
                    // Player has repair kits - vanilla will handle it
                    return;
                }

                // No repair kits in inventory - check storage
                int storageCount = ContainerManager.GetItemCount(Config, repairKitItem);
                if (storageCount <= 0)
                {
                    // No repair kits anywhere - vanilla will show error
                    return;
                }

                // Check if player has room to receive a repair kit BEFORE removing from storage
                // If inventory is full, don't bother - let vanilla fail naturally
                // (Can't repair a car with your arms full!)
                ItemStack testStack = new ItemStack(repairKitItem.Clone(), 1);
                bool canReceive = player.bag.CanStack(testStack) || player.inventory.CanStack(testStack);
                
                if (!canReceive)
                {
                    LogDebug($"Vehicle repair: Player inventory full, cannot pre-stage repair kit");
                    return; // Let vanilla handle the "no repair kit" error
                }

                // Pre-stage: Remove 1 repair kit from storage and add to player's bag
                int removed = ContainerManager.RemoveItems(Config, repairKitItem, 1);
                if (removed > 0)
                {
                    // Add to player's bag so vanilla finds it
                    ItemStack repairKitStack = new ItemStack(repairKitItem.Clone(), 1);
                    bool added = player.bag.AddItem(repairKitStack);
                    
                    if (added)
                    {
                        LogDebug($"Vehicle repair: Pre-staged 1 repair kit from storage to bag");
                    }
                    else
                    {
                        // Bag full but toolbelt might work (race condition edge case)
                        player.inventory.AddItem(repairKitStack);
                        LogDebug($"Vehicle repair: Pre-staged 1 repair kit from storage to toolbelt");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Vehicle repair patch error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Harmony Patches - HUD Ammo Counter

    // ====================================================================================
    // HUD AMMO COUNTER
    // ====================================================================================
    //
    // Shows total ammo from all in-range storage in the HUD stat bar.
    // When the player has a ranged weapon equipped, the ammo counter shows:
    // - Vanilla: Only ammo in player inventory (bag + toolbelt)
    // - With this patch: Ammo in inventory + nearby containers
    //
    // Uses POSTFIX to add container ammo count after vanilla calculates inventory count.
    // ====================================================================================

    /// <summary>
    /// Patch XUiC_HUDStatBar.updateActiveItemAmmo to include container ammo in HUD.
    /// Uses POSTFIX to add container count after vanilla calculates inventory count.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_HUDStatBar), "updateActiveItemAmmo")]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_HUDStatBar_updateActiveItemAmmo_Patch
    {
        // Cached field references for performance
        private static FieldInfo _activeAmmoField;
        private static FieldInfo _ammoCountField;
        private static bool _fieldsInitialized;

        public static void Postfix(XUiC_HUDStatBar __instance)
        {
            if (!Config?.modEnabled == true || !Config?.enableHudAmmoCounter == true)
                return;

            // Safety check - don't run if game state isn't ready
            if (!IsGameReady())
                return;

            try
            {
                // Cache reflection lookups (one-time per session)
                // Try both public and non-public since game uses publicizer
                if (!_fieldsInitialized)
                {
                    _fieldsInitialized = true;
                    
                    // Try public first (publicized assemblies), then non-public (original)
                    _activeAmmoField = typeof(XUiC_HUDStatBar).GetField("activeAmmoItemValue",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_activeAmmoField == null)
                    {
                        _activeAmmoField = typeof(XUiC_HUDStatBar).GetField("activeAmmoItemValue",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    
                    _ammoCountField = typeof(XUiC_HUDStatBar).GetField("currentAmmoCount",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_ammoCountField == null)
                    {
                        _ammoCountField = typeof(XUiC_HUDStatBar).GetField("currentAmmoCount",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    
                    FileLogInternal($"HUD ammo patch: activeAmmoField={_activeAmmoField != null}, ammoCountField={_ammoCountField != null}");
                }

                if (_activeAmmoField == null || _ammoCountField == null)
                {
                    return;
                }

                // Get the active ammo item type
                var activeAmmo = _activeAmmoField.GetValue(__instance) as ItemValue;
                if (activeAmmo == null || activeAmmo.type == 0)
                    return;

                // Get the current ammo count (from vanilla calculation)
                int currentCount = (int)_ammoCountField.GetValue(__instance);

                // Get container ammo count
                int containerCount = ContainerManager.GetItemCount(Config, activeAmmo);

                if (containerCount > 0)
                {
                    int newCount = AdaptivePatching.SafeAddCount(currentCount, containerCount);
                    _ammoCountField.SetValue(__instance, newCount);
                    LogDebug($"HUD ammo: {activeAmmo.ItemClass?.GetItemName()} = {currentCount} inventory + {containerCount} containers = {newCount}");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"HUD ammo patch error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch Bag.DecItem to invalidate cache and refresh HUD when ammo is consumed from bag.
    /// This handles reload operations and any other bag item removal.
    /// </summary>
    [HarmonyPatch(typeof(Bag), nameof(Bag.DecItem))]
    [HarmonyPriority(Priority.Low)]
    private static class Bag_DecItem_Patch
    {
        public static void Postfix()
        {
            if (!Config?.modEnabled == true)
                return;

            // Invalidate cache since bag contents changed
            ContainerManager.InvalidateCache();
            
            // Mark HUD dirty to refresh ammo display
            MarkHudAmmoDirty();
        }
    }

    /// <summary>
    /// Patch Inventory.DecItem to invalidate cache and refresh HUD when ammo is consumed from toolbelt.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.DecItem))]
    [HarmonyPriority(Priority.Low)]
    private static class Inventory_DecItem_Patch
    {
        public static void Postfix()
        {
            if (!Config?.modEnabled == true)
                return;

            // Invalidate cache since inventory contents changed
            ContainerManager.InvalidateCache();
            
            // Mark HUD dirty to refresh ammo display
            MarkHudAmmoDirty();
        }
    }

    /// <summary>
    /// Patch Bag.AddItem to invalidate cache when items are added to bag.
    /// This ensures counts are accurate after picking up items.
    /// </summary>
    [HarmonyPatch(typeof(Bag), nameof(Bag.AddItem))]
    [HarmonyPriority(Priority.Low)]
    private static class Bag_AddItem_Patch
    {
        public static void Postfix()
        {
            if (!Config?.modEnabled == true)
                return;

            ContainerManager.InvalidateCache();
            MarkHudAmmoDirty();
            RefreshRecipeTracker();
        }
    }

    /// <summary>
    /// Patch Inventory.AddItem to invalidate cache when items are added to toolbelt.
    /// Must use TargetMethod to specify exact overload since there are multiple AddItem methods.
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.Low)]
    private static class Inventory_AddItem_Patch
    {
        static MethodBase TargetMethod()
        {
            // Target AddItem(ItemStack, out int) specifically
            return typeof(Inventory).GetMethod(nameof(Inventory.AddItem), 
                BindingFlags.Public | BindingFlags.Instance, 
                null,
                new Type[] { typeof(ItemStack), typeof(int).MakeByRefType() },
                null);
        }

        public static void Postfix()
        {
            if (!Config?.modEnabled == true)
                return;

            ContainerManager.InvalidateCache();
            MarkHudAmmoDirty();
            RefreshRecipeTracker();
        }
    }

    #endregion

    #region Harmony Patches - Slot Lock Change Handler

    // ====================================================================================
    // SLOT LOCK CHANGE HANDLER
    // ====================================================================================
    //
    // When users lock/unlock slots (Ctrl+Click), we need to:
    // 1. Invalidate item count cache (locked items shouldn't be counted)
    // 2. Fire DragAndDropItemChanged to update challenge tracker
    // 3. Refresh recipe tracker to update ingredient counts
    //
    // This ensures all displays update immediately when slot locks change.
    // 
    // DEBOUNCING: When containers open, all slots get their lock state initialized.
    // We debounce updates to avoid firing hundreds of events per container open.
    // ====================================================================================

    private static float _lastSlotLockUpdateTime = 0f;
    private const float SLOT_LOCK_DEBOUNCE_TIME = 0.1f; // 100ms debounce

    /// <summary>
    /// Patch XUiC_ItemStack.UserLockedSlot setter to trigger updates when lock state changes.
    /// Uses debouncing to avoid spam when many slots are initialized at once.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_ItemStack), nameof(XUiC_ItemStack.UserLockedSlot), MethodType.Setter)]
    [HarmonyPriority(Priority.Low)]
    private static class XUiC_ItemStack_UserLockedSlot_Patch
    {
        public static void Postfix(XUiC_ItemStack __instance, bool value)
        {
            if (!Config?.modEnabled == true)
                return;

            // Only act if respectLockedSlots is enabled (otherwise locks don't affect our counts)
            if (!Config?.respectLockedSlots == true)
                return;

            try
            {
                // Always invalidate cache immediately (this is cheap)
                ContainerManager.InvalidateCache();
                
                // Debounce the expensive operations
                float currentTime = Time.time;
                if (currentTime - _lastSlotLockUpdateTime < SLOT_LOCK_DEBOUNCE_TIME)
                {
                    // Within debounce window, skip expensive operations
                    return;
                }
                
                // Fire the update
                _lastSlotLockUpdateTime = currentTime;
                
                // Refresh recipe tracker
                RefreshRecipeTracker();
                
                // Mark HUD ammo counter as dirty
                MarkHudAmmoDirty();
                
                // Fire DragAndDropItemChanged to update challenge tracker (only once per debounce window)
                if (Config?.enableForQuests == true)
                {
                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    if (player != null)
                    {
                        var eventField = typeof(EntityPlayerLocal).GetField("DragAndDropItemChanged",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        
                        if (eventField != null)
                        {
                            var eventDelegate = eventField.GetValue(player) as Delegate;
                            eventDelegate?.DynamicInvoke();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog($"UserLockedSlot_Patch: Exception: {ex.Message}");
            }
        }
    }

    #endregion

    #region Harmony Patches - Trader Purchase Fix

    // ====================================================================================
    // TRADER PURCHASE/SELL FIX
    // ====================================================================================
    //
    // Fixes buying/selling with traders using items from nearby containers (drone, etc.)
    // 
    // CanSwapItems is called with:
    //   _removedStack = items player gives (currency when buying, items when selling)
    //   _addedStack = items player receives (items when buying, currency when selling)
    //
    // BUYING: Need currency from containers to pay
    // SELLING: Need items from containers to sell
    //
    // The patch checks if player + containers have enough of _removedStack.
    // ====================================================================================

    /// <summary>
    /// Patch XUiM_PlayerInventory.CanSwapItems to consider containers for both buying and selling.
    /// 
    /// BUYING: _removedStack = currency, _addedStack = purchased item
    /// SELLING: _removedStack = item being sold, _addedStack = currency received
    /// 
    /// In both cases, we need to check if player + containers have enough of _removedStack.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.CanSwapItems))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_CanSwapItems_Patch
    {
        public static void Postfix(XUiM_PlayerInventory __instance, ref bool __result, ItemStack _removedStack, ItemStack _addedStack, int _slotNumber)
        {
            // Only process if vanilla said false and we might be able to help
            if (__result || !Config?.modEnabled == true || !Config?.enableForTrader == true)
                return;

            // Safety check
            if (!IsGameReady())
                return;

            try
            {
                if (_removedStack == null || _removedStack.IsEmpty() || _addedStack == null || _addedStack.IsEmpty())
                    return;

                var currencyItem = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
                bool isBuying = _removedStack.itemValue.type == currencyItem.type;
                bool isSelling = _addedStack.itemValue.type == currencyItem.type;

                FileLog($"[CANSWAP] removedStack={_removedStack.itemValue.ItemClass?.GetItemName()}x{_removedStack.count}, addedStack={_addedStack.itemValue.ItemClass?.GetItemName()}x{_addedStack.count}, isBuying={isBuying}, isSelling={isSelling}");

                // Get container count for the item being REMOVED (given away)
                int containerCount = ContainerManager.GetItemCount(Config, _removedStack.itemValue);
                
                if (containerCount <= 0)
                {
                    FileLog($"[CANSWAP] No containers have {_removedStack.itemValue.ItemClass?.GetItemName()}");
                    return;
                }

                // Get player's count of the item being removed
                int playerCount = __instance.GetItemCount(_removedStack.itemValue);
                int totalAvailable = playerCount + containerCount;

                FileLog($"[CANSWAP] playerCount={playerCount}, containerCount={containerCount}, totalAvailable={totalAvailable}, needed={_removedStack.count}");

                // Check if we have enough to remove
                if (totalAvailable < _removedStack.count)
                {
                    FileLog($"[CANSWAP] Not enough items even with containers");
                    return;
                }

                // Check if we have space for the item being ADDED (received)
                int availableSpace = __instance.CountAvailableSpaceForItem(_addedStack.itemValue);
                
                FileLog($"[CANSWAP] availableSpace={availableSpace}, needed={_addedStack.count}");

                if (availableSpace >= _addedStack.count)
                {
                    __result = true;
                    string action = isBuying ? "BUY" : (isSelling ? "SELL" : "SWAP");
                    LogDebug($"CanSwapItems ({action}): Allowing with containers, have={totalAvailable}, need={_removedStack.count}");
                    FileLog($"[CANSWAP] SUCCESS! Setting result=true");
                }
                else
                {
                    FileLog($"[CANSWAP] Not enough space for received items");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in CanSwapItems patch: {ex.Message}");
                FileLog($"[CANSWAP] ERROR: {ex.Message}");
            }
        }
    }

    #endregion
}
