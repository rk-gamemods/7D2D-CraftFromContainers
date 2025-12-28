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
    public const string MOD_VERSION = "1.0.0";
    
    // Static references
    private static ProxiCraft _instance;
    private static Mod _mod;
    public static ModConfig Config { get; private set; }
    
    // Harmony instance ID (unique to prevent conflicts)
    private static readonly string HarmonyId = "rkgamemods.proxicraft";

    #region IModApi Implementation
    
    public void InitMod(Mod modInstance)
    {
        _instance = this;
        _mod = modInstance;
        
        // Force log at very start to verify logging works
        Debug.Log("[ProxiCraft] InitMod starting...");
        
        LoadConfig();
        
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
    /// </summary>
    private static bool IsGameReady()
    {
        try
        {
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
    /// DIRECT Postfix patch on XUiM_PlayerInventory.GetItemCount
    /// This is the most reliable way to add container counts to ingredient displays.
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
                int containerCount = ContainerManager.GetItemCount(Config, _itemValue);
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
    /// Patch RemoveItems to also remove from containers when crafting.
    /// 
    /// BACKPACK COMPATIBILITY: This is a Postfix - we only remove from containers
    /// what the inventory removal couldn't satisfy. Inventory structure unchanged.
    /// </summary>
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.RemoveItems))]
    [HarmonyPriority(Priority.Low)]
    private static class XUiM_PlayerInventory_RemoveItems_Patch
    {
        // Track what needs to be removed from containers after inventory removal
        private static IList<ItemStack> _pendingContainerRemovals;
        private static int _pendingMultiplier;

        [HarmonyPrefix]
        public static void Prefix(IList<ItemStack> _itemStacks, int _multiplier)
        {
            if (!Config?.modEnabled == true)
                return;

            // Store what's being requested for the Postfix
            _pendingContainerRemovals = _itemStacks;
            _pendingMultiplier = _multiplier;
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
                return;
            }

            try
            {
                // Check what inventory still has - anything short needs to come from containers
                var inventoryItems = __instance.GetAllItemStacks();
                
                foreach (var required in _pendingContainerRemovals)
                {
                    if (required == null || required.IsEmpty())
                        continue;

                    int neededTotal = required.count * _pendingMultiplier;
                    
                    // Count what's left in inventory after vanilla removal
                    int inventoryHas = 0;
                    foreach (var invItem in inventoryItems)
                    {
                        if (invItem?.itemValue != null && 
                            invItem.itemValue.type == required.itemValue.type)
                        {
                            inventoryHas += invItem.count;
                        }
                    }
                    
                    // The vanilla method removed what it could from inventory
                    // We need to remove any shortfall from containers
                    // Note: This is a heuristic - we remove (needed - inventory_remaining) from containers
                    // This works because vanilla removes from inventory first
                    
                    int fromContainers = neededTotal - inventoryHas;
                    if (fromContainers > 0)
                    {
                        int removed = ContainerManager.RemoveItems(Config, required.itemValue, fromContainers);
                        LogDebug($"Removed {removed}/{fromContainers} {required.itemValue?.ItemClass?.GetItemName()} from containers");
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error in RemoveItems Postfix: {ex.Message}");
            }
            finally
            {
                _pendingContainerRemovals = null;
            }
        }
    }

    #endregion

    #region Harmony Patches - Reload Support
    
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
        if (!Config?.modEnabled == true || !Config?.enableForReload == true)
            return inventoryCount;

        return GetTotalItemCount(inventoryCount, ammoItem);
    }

    /// <summary>
    /// Removes ammo from inventory and containers for reload.
    /// </summary>
    public static int DecItemForReload(Inventory inventory, ItemValue item, int count, bool ignoreModded, IList<ItemStack> removedItems)
    {
        int removed = inventory.DecItem(item, count, ignoreModded, removedItems);
        
        if (!Config?.modEnabled == true || !Config?.enableForReload == true)
            return removed;

        // Safety check - don't access containers if game state isn't ready
        if (!IsGameReady())
            return removed;

        if (removed < count)
        {
            int remaining = count - removed;
            int containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
            LogDebug($"Removed {containerRemoved} ammo from containers for reload");
            removed += containerRemoved;
        }
        
        return removed;
    }

    #endregion

    #region Harmony Patches - Vehicle Refuel Support
    
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
                int containerCount = ContainerManager.GetItemCount(Config, fuelItem);
                
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
            int containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
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
                
                FileLog($"HandleUpdatingCurrent_Patch: Item={expectedItem.ItemClass?.GetItemName()}, inventory={inventoryCount}, containers={containerCount}, total={totalCount}, max={maxCount}");
                
                // SET the current field to include container items
                if (containerCount > 0 && currentField != null && displayCount != inventoryCount)
                {
                    currentField.SetValue(__instance, displayCount);
                    FileLog($"[UPDATE] Set Current to {displayCount} (was {inventoryCount})");
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
                
                FileLog($"[DIAG] === PLAYER INVENTORY: bag={playerBagCount}, toolbelt={playerToolbeltCount}, total={actualInventory} ===");
                
                // Count in containers
                int containerCount = ContainerManager.GetItemCount(Config, expectedItem);
                
                // Total possession = actual inventory + containers
                int totalPossession = actualInventory + containerCount;
                
                // Cap display at maxCount
                int displayCount = Math.Min(totalPossession, maxCount);
                
                FileLog($"[DIAG] === GRAND TOTAL: inv={actualInventory} + containers={containerCount} = {totalPossession}, showing {displayCount}/{maxCount} ===");

                // Replace the gather progress with total possession in the display
                if (totalPossession != gatherProgress)
                {
                    string oldPattern = $"{gatherProgress}/";
                    string newPattern = $"{displayCount}/";
                    
                    if (__result.Contains(oldPattern))
                    {
                        __result = __result.Replace(oldPattern, newPattern);
                        FileLog($"StatusText_Patch: Changed '{oldPattern}' to '{newPattern}' -> {__result}");
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
                
                // Total possession = actual inventory + containers
                int totalPossession = actualInventory + containerCount;
                
                FileLog($"CheckComplete_Patch: Item={expectedItem.ItemClass?.GetItemName()}, actualInv={actualInventory}, containers={containerCount}, totalPossession={totalPossession}, max={maxCount}, handleComplete={handleComplete}");
                
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
                            FileLog($"CheckComplete_Patch: Set Complete=true for green highlighting!");
                        }
                    }
                    
                    FileLog($"CheckComplete_Patch: Marking complete!");
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
    /// Also invalidates item count cache for fresh data.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent")]
    [HarmonyPriority(Priority.Low)]
    private static class LootContainer_SlotChanged_Patch
    {
        public static void Postfix()
        {
            // Always invalidate cache when container contents change
            ContainerManager.InvalidateCache();
            
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
    /// </summary>
    [HarmonyPatch(typeof(XUiC_VehicleContainer), "HandleLootSlotChangedEvent")]
    [HarmonyPriority(Priority.Low)]
    private static class VehicleContainer_SlotChanged_Patch
    {
        public static void Postfix()
        {
            // Always invalidate cache when vehicle contents change
            ContainerManager.InvalidateCache();
            
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
            int containerRemoved = ContainerManager.RemoveItems(Config, item, remaining);
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
}
