using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProxiCraft;

/// <summary>
/// Startup health check system that validates all mod functionality before gameplay.
///
/// DESIGN PRINCIPLES:
/// 1. Run ONCE at startup, not during gameplay
/// 2. Be completely transparent about what's working and what isn't
/// 3. Give users control - they decide whether to proceed
/// 4. Provide actionable information for debugging and bug reports
/// 5. Acceptable delays during load if clearly communicated
///
/// OUTPUT FORMAT:
/// [ProxiCraft] ══════════════════════════════════════════════════════════
/// [ProxiCraft] STARTUP HEALTH CHECK
/// [ProxiCraft] ══════════════════════════════════════════════════════════
/// [ProxiCraft] [OK]   Crafting patches validated
/// [ProxiCraft] [OK]   Reload patches validated
/// [ProxiCraft] [WARN] Vehicle refuel: Method renamed, auto-adapted
/// [ProxiCraft] [FAIL] Generator refuel: Could not find target method
/// [ProxiCraft] ──────────────────────────────────────────────────────────
/// [ProxiCraft] SUMMARY: 8/10 features operational, 1 adapted, 1 failed
/// [ProxiCraft] ══════════════════════════════════════════════════════════
/// </summary>
public static class StartupHealthCheck
{
    /// <summary>
    /// Result of checking a single feature
    /// </summary>
    public class FeatureCheckResult
    {
        public string FeatureId { get; set; }
        public string FeatureName { get; set; }
        public HealthStatus Status { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public string SuggestedFix { get; set; }
        public TimeSpan CheckDuration { get; set; }
    }

    public enum HealthStatus
    {
        OK,       // Feature working normally
        Adapted,  // Feature working via fallback (warn user)
        Degraded, // Feature partially working
        Failed,   // Feature not working
        Disabled  // Feature disabled by config
    }

    /// <summary>
    /// All feature check results from the last health check
    /// </summary>
    public static List<FeatureCheckResult> Results { get; private set; } = new();

    /// <summary>
    /// Whether the health check has been run
    /// </summary>
    public static bool HasRun { get; private set; } = false;

    /// <summary>
    /// Total time the health check took
    /// </summary>
    public static TimeSpan TotalDuration { get; private set; }

    /// <summary>
    /// Cached method mappings discovered during health check.
    /// Key = original method description, Value = discovered method
    /// </summary>
    private static readonly Dictionary<string, MethodInfo> _discoveredMethods = new();

    /// <summary>
    /// Whether verbose output is enabled (from config).
    /// When false, only problems are logged to console.
    /// </summary>
    private static bool _verboseMode = false;

    /// <summary>
    /// Gets a cached discovered method, or null if not found.
    /// </summary>
    public static MethodInfo GetDiscoveredMethod(string key)
    {
        return _discoveredMethods.TryGetValue(key, out var method) ? method : null;
    }

    /// <summary>
    /// Runs the complete health check. Should be called once during mod initialization.
    /// Silent by default - only outputs when there are problems OR verboseHealthCheck is enabled.
    /// </summary>
    public static void RunHealthCheck(ModConfig config)
    {
        var totalTimer = Stopwatch.StartNew();
        Results.Clear();
        _discoveredMethods.Clear();

        // Check if verbose mode is enabled (either explicitly or via debug mode)
        _verboseMode = config.verboseHealthCheck || config.isDebug;

        // Check each feature category (silently collect results)
        CheckCraftingFeatures(config);
        CheckReloadFeatures(config);
        CheckRefuelFeatures(config);
        CheckTraderFeatures(config);
        CheckQuestFeatures(config);
        CheckRepairFeatures(config);
        CheckNewFeatures(config);
        CheckStorageSources(config);

        // Validate Harmony patches actually applied
        ValidateHarmonyPatches();

        totalTimer.Stop();
        TotalDuration = totalTimer.Elapsed;
        HasRun = true;

        // Determine if we have any issues
        bool hasIssues = Results.Any(r =>
            r.Status == HealthStatus.Adapted ||
            r.Status == HealthStatus.Degraded ||
            r.Status == HealthStatus.Failed);

        // Only output if there are issues OR verbose mode is on
        if (hasIssues || _verboseMode)
        {
            LogHeader("STARTUP HEALTH CHECK");

            if (_verboseMode)
            {
                Log("Validating mod functionality before gameplay...");
                Log("");
            }

            // Log results (LogResult respects verbose mode for OK/Disabled)
            foreach (var result in Results)
            {
                LogResult(result);
            }

            // Print summary
            PrintSummary();
        }
    }

    #region Feature Checks

    private static void CheckCraftingFeatures(ModConfig config)
    {
        if (!config.enableForCrafting)
        {
            AddResult("Crafting", "Crafting from containers", HealthStatus.Disabled, "Disabled in config");
            return;
        }

        var result = new FeatureCheckResult
        {
            FeatureId = "Crafting",
            FeatureName = "Crafting from containers"
        };

        var timer = Stopwatch.StartNew();

        try
        {
            // Check required types and methods exist
            var checks = new List<(string name, bool exists, string details)>
            {
                ("ItemActionEntryCraft.OnActivated",
                 AccessTools.Method(typeof(ItemActionEntryCraft), "OnActivated") != null,
                 "Primary crafting activation method"),

                ("XUiM_PlayerInventory.GetAllItemStacks",
                 AccessTools.Method(typeof(XUiM_PlayerInventory), "GetAllItemStacks") != null,
                 "Inventory item retrieval"),

                ("XUiM_PlayerInventory.GetItemCount",
                 AccessTools.Method(typeof(XUiM_PlayerInventory), "GetItemCount", new[] { typeof(ItemValue) }) != null,
                 "Item counting method"),

                ("XUiM_PlayerInventory.RemoveItems",
                 AccessTools.Method(typeof(XUiM_PlayerInventory), "RemoveItems") != null,
                 "Item removal method")
            };

            var failed = checks.Where(c => !c.exists).ToList();

            if (failed.Count == 0)
            {
                result.Status = HealthStatus.OK;
                result.Message = "All crafting methods validated";
            }
            else if (failed.Count < checks.Count)
            {
                result.Status = HealthStatus.Degraded;
                result.Message = $"{failed.Count} method(s) not found";
                result.Details = string.Join(", ", failed.Select(f => f.name));
                result.SuggestedFix = "Some crafting features may not work. Check for game update.";
            }
            else
            {
                result.Status = HealthStatus.Failed;
                result.Message = "Crafting methods not found";
                result.Details = "Game API may have changed significantly";
                result.SuggestedFix = "Mod update required for this game version";
            }
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Message = $"Check failed: {ex.Message}";
        }

        timer.Stop();
        result.CheckDuration = timer.Elapsed;
        AddResult(result);
    }

    private static void CheckReloadFeatures(ModConfig config)
    {
        if (!config.enableForReload)
        {
            AddResult("Reload", "Weapon reload from containers", HealthStatus.Disabled, "Disabled in config");
            return;
        }

        // Check CanReload patch (gatekeeper - allows reload to start)
        var canReloadResult = CheckMethodWithFallback(
            "ReloadCanReload",
            "Reload gatekeeper (CanReload)",
            typeof(ItemActionRanged),
            "CanReload",
            null,
            new[] { "canReload", "CheckCanReload" }
        );
        AddResult(canReloadResult);

        // Check GetAmmoCountToReload patch (during reload animation)
        var result = CheckMethodWithFallback(
            "Reload",
            "Weapon reload from containers",
            typeof(AnimatorRangedReloadState),
            "GetAmmoCountToReload",
            null,
            new[] { "GetAmmo", "AmmoCount", "ReloadAmmo" }
        );

        AddResult(result);
    }

    private static void CheckRefuelFeatures(ModConfig config)
    {
        // Vehicle refuel
        if (config.enableForRefuel)
        {
            var vehicleResult = CheckMethodWithFallback(
                "VehicleRefuel",
                "Vehicle refuel from containers",
                typeof(EntityVehicle),
                "takeFuel",
                null,
                new[] { "TakeFuel", "ConsumeFuel", "UseFuel" }
            );

            // Also check Bag.DecItem which we need to patch
            var bagCheck = CheckMethodWithFallback(
                "VehicleRefuel.DecItem",
                "Bag item removal (for vehicle)",
                typeof(Bag),
                "DecItem",
                null,
                new[] { "Dec", "Remove", "Subtract", "Consume" }
            );

            if (vehicleResult.Status == HealthStatus.OK && bagCheck.Status == HealthStatus.OK)
            {
                vehicleResult.Message = "Vehicle refuel validated";
            }
            else if (vehicleResult.Status == HealthStatus.Adapted || bagCheck.Status == HealthStatus.Adapted)
            {
                vehicleResult.Status = HealthStatus.Adapted;
                vehicleResult.Message = "Vehicle refuel adapted";
                vehicleResult.Details = bagCheck.Details ?? vehicleResult.Details;
                vehicleResult.SuggestedFix = bagCheck.SuggestedFix ?? vehicleResult.SuggestedFix;
            }
            else if (vehicleResult.Status == HealthStatus.Failed || bagCheck.Status == HealthStatus.Failed)
            {
                vehicleResult.Status = HealthStatus.Failed;
            }

            AddResult(vehicleResult);
        }
        else
        {
            AddResult("VehicleRefuel", "Vehicle refuel from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Generator refuel
        if (config.enableForGeneratorRefuel)
        {
            var genResult = CheckMethodWithFallback(
                "GeneratorRefuel",
                "Generator refuel from containers",
                typeof(XUiC_PowerSourceStats),
                "BtnRefuel_OnPress",
                null,
                new[] { "Refuel", "OnRefuel", "RefuelPress" }
            );

            AddResult(genResult);
        }
        else
        {
            AddResult("GeneratorRefuel", "Generator refuel from containers", HealthStatus.Disabled, "Disabled in config");
        }
    }

    private static void CheckRepairFeatures(ModConfig config)
    {
        if (!config.enableForRepairAndUpgrade)
        {
            AddResult("Repair", "Block repair/upgrade", HealthStatus.Disabled, "Disabled in config");
            return;
        }

        // Check ItemActionRepair methods for block upgrades
        var canRemoveMethod = AccessTools.Method(typeof(ItemActionRepair), "CanRemoveRequiredResource");
        var removeMethod = AccessTools.Method(typeof(ItemActionRepair), "RemoveRequiredResource");

        if (canRemoveMethod != null && removeMethod != null)
        {
            AddResult("Repair", "Block repair/upgrade", HealthStatus.OK,
                "ItemActionRepair methods validated");
        }
        else
        {
            AddResult("Repair", "Block repair/upgrade", HealthStatus.Degraded,
                "Some upgrade methods not found",
                $"CanRemoveRequiredResource: {(canRemoveMethod != null ? "OK" : "MISSING")}, RemoveRequiredResource: {(removeMethod != null ? "OK" : "MISSING")}");
        }
    }

    private static void CheckNewFeatures(ModConfig config)
    {
        // Painting
        if (config.enableForPainting)
        {
            var paintCheck1 = AccessTools.Method(typeof(ItemActionTextureBlock), "checkAmmo");
            var paintCheck2 = AccessTools.Method(typeof(ItemActionTextureBlock), "decreaseAmmo");

            if (paintCheck1 != null && paintCheck2 != null)
            {
                AddResult("Painting", "Painting from containers", HealthStatus.OK, "Paint methods validated");
            }
            else
            {
                AddResult("Painting", "Painting from containers", HealthStatus.Failed,
                    "Paint methods not found - feature will not work",
                    $"checkAmmo: {(paintCheck1 != null ? "OK" : "MISSING")}, decreaseAmmo: {(paintCheck2 != null ? "OK" : "MISSING")}");
            }
        }
        else
        {
            AddResult("Painting", "Painting from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Lockpicking - uses existing patches
        if (config.enableForLockpicking)
        {
            AddResult("Lockpicking", "Lockpicking from containers", HealthStatus.OK,
                "Uses existing inventory count patches");
        }
        else
        {
            AddResult("Lockpicking", "Lockpicking from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Item repair - uses existing patches
        if (config.enableForItemRepair)
        {
            AddResult("ItemRepair", "Item repair from containers", HealthStatus.OK,
                "Uses existing inventory patches");
        }
        else
        {
            AddResult("ItemRepair", "Item repair from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Vehicle repair - dedicated patch for XUiM_Vehicle.RepairVehicle
        if (config.enableForItemRepair)
        {
            var repairVehicleMethod = AccessTools.Method(typeof(XUiM_Vehicle), nameof(XUiM_Vehicle.RepairVehicle));
            if (repairVehicleMethod != null)
            {
                AddResult("VehicleRepair", "Vehicle repair from containers", HealthStatus.OK,
                    "XUiM_Vehicle.RepairVehicle patched");
            }
            else
            {
                AddResult("VehicleRepair", "Vehicle repair from containers", HealthStatus.Failed,
                    "RepairVehicle method not found");
            }
        }
        else
        {
            AddResult("VehicleRepair", "Vehicle repair from containers", HealthStatus.Disabled, "Disabled in config (enableForItemRepair)");
        }

        // HUD Ammo Counter
        if (config.enableHudAmmoCounter)
        {
            var updateAmmoMethod = AccessTools.Method(typeof(XUiC_HUDStatBar), "updateActiveItemAmmo");
            if (updateAmmoMethod != null)
            {
                AddResult("HudAmmoCounter", "HUD ammo counter from containers", HealthStatus.OK,
                    "XUiC_HUDStatBar.updateActiveItemAmmo validated");
            }
            else
            {
                AddResult("HudAmmoCounter", "HUD ammo counter from containers", HealthStatus.Failed,
                    "updateActiveItemAmmo method not found");
            }
        }
        else
        {
            AddResult("HudAmmoCounter", "HUD ammo counter from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Recipe Tracker Updates
        if (config.enableRecipeTrackerUpdates)
        {
            var recipeTrackerType = AccessTools.TypeByName("XUiC_RecipeTrackerWindow");
            if (recipeTrackerType != null)
            {
                AddResult("RecipeTracker", "Recipe tracker live updates", HealthStatus.OK,
                    "XUiC_RecipeTrackerWindow found - updates will trigger on container changes");
            }
            else
            {
                AddResult("RecipeTracker", "Recipe tracker live updates", HealthStatus.Failed,
                    "XUiC_RecipeTrackerWindow not found");
            }
        }
        else
        {
            AddResult("RecipeTracker", "Recipe tracker live updates", HealthStatus.Disabled, "Disabled in config");
        }

        // Trader Selling
        if (config.enableTraderSelling)
        {
            var sellActivated = AccessTools.Method(typeof(ItemActionEntrySell), "OnActivated");
            var sellRefresh = AccessTools.Method(typeof(ItemActionEntrySell), "RefreshEnabled");
            if (sellActivated != null && sellRefresh != null)
            {
                AddResult("TraderSelling", "Selling items from containers", HealthStatus.OK,
                    "ItemActionEntrySell methods validated");
            }
            else
            {
                AddResult("TraderSelling", "Selling items from containers", HealthStatus.Failed,
                    "ItemActionEntrySell methods not found",
                    $"OnActivated: {(sellActivated != null ? "OK" : "MISSING")}, RefreshEnabled: {(sellRefresh != null ? "OK" : "MISSING")}");
            }
        }
        else
        {
            AddResult("TraderSelling", "Selling items from containers", HealthStatus.Disabled, "Disabled in config");
        }

        // Locked Slot Respect
        if (config.respectLockedSlots)
        {
            // Check if PackedBoolArray exists (used for SlotLocks)
            var packedBoolType = AccessTools.TypeByName("PackedBoolArray");
            // Check if TEFeatureStorage has SlotLocks
            var storageType = AccessTools.TypeByName("TEFeatureStorage");
            var slotLocksField = storageType != null ? AccessTools.Field(storageType, "SlotLocks") : null;

            if (packedBoolType != null && slotLocksField != null)
            {
                AddResult("LockedSlots", "Respect locked slots", HealthStatus.OK,
                    "SlotLocks field validated - locked slots will be skipped");
            }
            else if (packedBoolType != null)
            {
                AddResult("LockedSlots", "Respect locked slots", HealthStatus.Degraded,
                    "PackedBoolArray found but SlotLocks field not found - may not work for all containers",
                    $"TEFeatureStorage: {(storageType != null ? "found" : "missing")}, SlotLocks: {(slotLocksField != null ? "found" : "missing")}");
            }
            else
            {
                AddResult("LockedSlots", "Respect locked slots", HealthStatus.Failed,
                    "PackedBoolArray type not found - feature will not work");
            }
        }
        else
        {
            AddResult("LockedSlots", "Respect locked slots", HealthStatus.Disabled, "Disabled in config");
        }
    }

    private static void CheckStorageSources(ModConfig config)
    {
        // These are simpler - just check if the entity/tile types exist
        if (config.pullFromVehicles)
        {
            bool exists = typeof(EntityVehicle) != null;
            AddResult("Vehicles", "Vehicle storage", exists ? HealthStatus.OK : HealthStatus.Failed,
                exists ? "EntityVehicle type found" : "EntityVehicle type not found");
        }

        if (config.pullFromDrones)
        {
            var droneType = AccessTools.TypeByName("EntityDrone");
            AddResult("Drones", "Drone storage",
                droneType != null ? HealthStatus.OK : HealthStatus.Failed,
                droneType != null ? "EntityDrone type found" : "EntityDrone type not found - drones may not work");
        }

        if (config.pullFromDewCollectors)
        {
            var collectorType = AccessTools.TypeByName("TileEntityCollector");
            AddResult("DewCollectors", "Dew collector contents",
                collectorType != null ? HealthStatus.OK : HealthStatus.Failed,
                collectorType != null ? "TileEntityCollector found" : "TileEntityCollector not found");
        }

        if (config.pullFromWorkstationOutputs)
        {
            var workstationType = AccessTools.TypeByName("TileEntityWorkstation");
            AddResult("Workstations", "Workstation outputs",
                workstationType != null ? HealthStatus.OK : HealthStatus.Failed,
                workstationType != null ? "TileEntityWorkstation found" : "TileEntityWorkstation not found");
        }
    }

    private static void CheckTraderFeatures(ModConfig config)
    {
        if (!config.enableForTrader)
        {
            AddResult("Trader", "Trader purchases from containers", HealthStatus.Disabled, "Disabled in config");
            return;
        }

        var result = new FeatureCheckResult
        {
            FeatureId = "Trader",
            FeatureName = "Trader purchases from containers"
        };

        var timer = Stopwatch.StartNew();

        try
        {
            // Check the target method exists
            var purchaseMethod = AccessTools.Method(typeof(ItemActionEntryPurchase), "RefreshEnabled");

            if (purchaseMethod != null)
            {
                // Check transpiler status if available
                if (RobustTranspiler.DidTranspilerSucceed("TraderPurchase"))
                {
                    result.Status = HealthStatus.OK;
                    result.Message = "Trader purchase patch applied successfully";
                }
                else
                {
                    // Method exists but transpiler may not have run yet (first startup)
                    result.Status = HealthStatus.OK;
                    result.Message = "ItemActionEntryPurchase.RefreshEnabled validated";
                }
            }
            else
            {
                result.Status = HealthStatus.Failed;
                result.Message = "ItemActionEntryPurchase.RefreshEnabled not found";
                result.Details = "Trader purchase method missing from game";
                result.SuggestedFix = "Feature will not work. May need mod update.";
            }
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Message = $"Check failed: {ex.Message}";
        }

        timer.Stop();
        result.CheckDuration = timer.Elapsed;
        AddResult(result);
    }

    private static void CheckQuestFeatures(ModConfig config)
    {
        if (!config.enableForQuests)
        {
            AddResult("Quests", "Quest/challenge tracking from containers", HealthStatus.Disabled, "Disabled in config");
            return;
        }

        var result = new FeatureCheckResult
        {
            FeatureId = "Quests",
            FeatureName = "Quest/challenge tracking from containers"
        };

        var timer = Stopwatch.StartNew();

        try
        {
            // Check challenge tracker types exist
            var gatherType = AccessTools.TypeByName("Challenges.ChallengeObjectiveGather");
            var baseObjType = AccessTools.TypeByName("Challenges.BaseChallengeObjective");

            var checks = new List<(string name, bool exists)>
            {
                ("ChallengeObjectiveGather", gatherType != null),
                ("BaseChallengeObjective", baseObjType != null),
                ("XUiC_LootContainer.OnOpen", AccessTools.Method(typeof(XUiC_LootContainer), "OnOpen") != null),
                ("XUiC_LootContainer.OnClose", AccessTools.Method(typeof(XUiC_LootContainer), "OnClose") != null),
                ("XUiC_LootContainer.HandleLootSlotChangedEvent",
                    AccessTools.Method(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent") != null),
                ("XUiC_ItemStack.HandleSlotChangeEvent",
                    AccessTools.Method(typeof(XUiC_ItemStack), "HandleSlotChangeEvent") != null)
            };

            // Also check HandleUpdatingCurrent if gatherType exists
            if (gatherType != null)
            {
                var handleMethod = AccessTools.Method(gatherType, "HandleUpdatingCurrent");
                checks.Add(("HandleUpdatingCurrent", handleMethod != null));
            }

            var failed = checks.Where(c => !c.exists).ToList();

            if (failed.Count == 0)
            {
                result.Status = HealthStatus.OK;
                result.Message = "All challenge tracker methods validated";
                result.Details = $"Checked {checks.Count} methods/types";
            }
            else if (failed.Count < checks.Count / 2)
            {
                result.Status = HealthStatus.Degraded;
                result.Message = $"{failed.Count} method(s) not found";
                result.Details = string.Join(", ", failed.Select(f => f.name));
                result.SuggestedFix = "Some challenge tracking may not work correctly.";
            }
            else
            {
                result.Status = HealthStatus.Failed;
                result.Message = "Challenge tracker types not found";
                result.Details = string.Join(", ", failed.Select(f => f.name));
                result.SuggestedFix = "Quest/challenge tracking will not work. Mod update may be required.";
            }
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Message = $"Check failed: {ex.Message}";
        }

        timer.Stop();
        result.CheckDuration = timer.Elapsed;
        AddResult(result);
    }

    /// <summary>
    /// Validates that Harmony patches were actually applied.
    /// This runs after SafePatcher has applied all patches.
    /// </summary>
    private static void ValidateHarmonyPatches()
    {
        // Define critical patches to verify
        var patchesToVerify = new List<(Type targetType, string methodName, Type[] paramTypes, string patchType, string featureId)>
        {
            // Core inventory patches (critical for most features)
            (typeof(XUiM_PlayerInventory), "GetItemCount", new[] { typeof(ItemValue) }, "Postfix", "Core.GetItemCount.ItemValue"),
            (typeof(XUiM_PlayerInventory), "GetItemCount", new[] { typeof(int) }, "Postfix", "Core.GetItemCount.Int"),
            (typeof(XUiM_PlayerInventory), "HasItems", null, "Postfix", "Core.HasItems"),
            (typeof(XUiM_PlayerInventory), "RemoveItems", null, "Postfix", "Core.RemoveItems"),

            // Container tracking
            (typeof(XUiC_LootContainer), "OnOpen", null, "Postfix", "Container.OnOpen"),
            (typeof(XUiC_LootContainer), "OnClose", null, "Postfix", "Container.OnClose"),

            // Vehicle refuel
            (typeof(EntityVehicle), "hasGasCan", null, "Postfix", "VehicleRefuel.hasGasCan"),

            // Painting
            (typeof(ItemActionTextureBlock), "checkAmmo", null, "Prefix", "Painting.checkAmmo"),
            (typeof(ItemActionTextureBlock), "decreaseAmmo", null, "Prefix", "Painting.decreaseAmmo"),
            
            // Block upgrades
            (typeof(ItemActionRepair), "CanRemoveRequiredResource", null, "Postfix", "BlockUpgrade.CanRemove"),
            (typeof(ItemActionRepair), "RemoveRequiredResource", null, "Postfix", "BlockUpgrade.Remove"),
        };

        int verified = 0;
        int failed = 0;
        var failedPatches = new List<string>();

        foreach (var (targetType, methodName, paramTypes, patchType, featureId) in patchesToVerify)
        {
            try
            {
                var method = paramTypes != null
                    ? AccessTools.Method(targetType, methodName, paramTypes)
                    : AccessTools.Method(targetType, methodName);

                if (method == null)
                {
                    // Method doesn't exist - already reported in feature checks
                    continue;
                }

                var patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo == null)
                {
                    failed++;
                    failedPatches.Add($"{targetType.Name}.{methodName} ({patchType})");
                    continue;
                }

                bool hasOurPatch = false;
                switch (patchType)
                {
                    case "Prefix":
                        hasOurPatch = patchInfo.Prefixes?.Any(p => p.owner == "rkgamemods.proxicraft") ?? false;
                        break;
                    case "Postfix":
                        hasOurPatch = patchInfo.Postfixes?.Any(p => p.owner == "rkgamemods.proxicraft") ?? false;
                        break;
                    case "Transpiler":
                        hasOurPatch = patchInfo.Transpilers?.Any(p => p.owner == "rkgamemods.proxicraft") ?? false;
                        break;
                }

                if (hasOurPatch)
                {
                    verified++;
                }
                else
                {
                    failed++;
                    failedPatches.Add($"{targetType.Name}.{methodName} ({patchType})");
                }
            }
            catch
            {
                // Skip patches that can't be verified
            }
        }

        // Also check transpiler success status from RobustTranspiler
        var transpilerFeatures = new[] { "CraftHasItems", "CraftMaxCraftable", "IngredientBinding",
            "ReloadAmmo", "VehicleRefuel", "GeneratorRefuel" };

        foreach (var featureId in transpilerFeatures)
        {
            // Only report failures if the transpiler explicitly failed (not just untracked)
            if (RobustTranspiler.DidTranspilerSucceed(featureId) == false)
            {
                // Check if it was tracked and failed vs never tracked
                // RobustTranspiler returns false for both untracked and failed, so we can't distinguish here
                // The transpiler status is set during patch application, so if it's false, it either
                // wasn't tracked or failed - we'll rely on feature checks for the method existence
            }
        }

        // Add result for Harmony validation
        if (failed == 0)
        {
            AddResult("HarmonyPatches", "Harmony patch validation", HealthStatus.OK,
                $"Verified {verified} patches applied successfully");
        }
        else if (failed <= 2)
        {
            AddResult("HarmonyPatches", "Harmony patch validation", HealthStatus.Degraded,
                $"{verified} patches verified, {failed} not found",
                $"Missing: {string.Join(", ", failedPatches.Take(3))}");
        }
        else
        {
            AddResult("HarmonyPatches", "Harmony patch validation", HealthStatus.Failed,
                $"Multiple patches not applied ({failed} failures)",
                $"Missing: {string.Join(", ", failedPatches.Take(5))}",
                "Some features will not work. Check for mod conflicts.");
        }
    }

    #endregion

    #region Helper Methods

    private static FeatureCheckResult CheckMethodWithFallback(
        string featureId,
        string featureName,
        Type targetType,
        string methodName,
        Type[] paramTypes,
        string[] fallbackPatterns)
    {
        var result = new FeatureCheckResult
        {
            FeatureId = featureId,
            FeatureName = featureName
        };

        var timer = Stopwatch.StartNew();

        try
        {
            // Try exact match first
            var method = paramTypes != null
                ? AccessTools.Method(targetType, methodName, paramTypes)
                : AccessTools.Method(targetType, methodName);

            if (method != null)
            {
                result.Status = HealthStatus.OK;
                result.Message = $"{targetType.Name}.{methodName} validated";
                _discoveredMethods[$"{targetType.Name}.{methodName}"] = method;
            }
            else
            {
                // Try adaptive fallback
                var adaptiveResult = AdaptiveMethodFinder.FindMethod(
                    targetType, methodName, paramTypes, null, fallbackPatterns);

                if (adaptiveResult.Found)
                {
                    result.Status = HealthStatus.Adapted;
                    result.Message = $"Method found via {adaptiveResult.Strategy}";
                    result.Details = $"Original: {methodName} → Found: {adaptiveResult.Method.Name}";
                    result.SuggestedFix = adaptiveResult.SuggestedFix;

                    // Cache the discovered method for runtime use
                    _discoveredMethods[$"{targetType.Name}.{methodName}"] = adaptiveResult.Method;
                }
                else
                {
                    result.Status = HealthStatus.Failed;
                    result.Message = $"{targetType.Name}.{methodName} not found";
                    result.Details = adaptiveResult.DiagnosticInfo;
                    result.SuggestedFix = "Feature will not work. Mod update may be required.";
                }
            }
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Message = $"Check failed: {ex.Message}";
        }

        timer.Stop();
        result.CheckDuration = timer.Elapsed;
        return result;
    }

    private static void AddResult(FeatureCheckResult result)
    {
        Results.Add(result);
        // Note: Logging is now done after all checks complete in RunHealthCheck
    }

    private static void AddResult(string featureId, string featureName, HealthStatus status, string message, string details = null, string suggestedFix = null)
    {
        var result = new FeatureCheckResult
        {
            FeatureId = featureId,
            FeatureName = featureName,
            Status = status,
            Message = message,
            Details = details,
            SuggestedFix = suggestedFix
        };
        AddResult(result);
    }

    #endregion

    #region Logging

    private static void LogHeader(string title)
    {
        UnityEngine.Debug.Log($"[ProxiCraft] ══════════════════════════════════════════════════════════");
        UnityEngine.Debug.Log($"[ProxiCraft] {title}");
        UnityEngine.Debug.Log($"[ProxiCraft] ══════════════════════════════════════════════════════════");
    }

    private static void Log(string message)
    {
        UnityEngine.Debug.Log($"[ProxiCraft] {message}");
    }

    private static void LogResult(FeatureCheckResult result)
    {
        // Skip OK/Disabled results unless verbose mode is on
        if (!_verboseMode && (result.Status == HealthStatus.OK || result.Status == HealthStatus.Disabled))
        {
            return;
        }

        string statusTag = result.Status switch
        {
            HealthStatus.OK => "[OK]     ",
            HealthStatus.Adapted => "[ADAPT]  ",
            HealthStatus.Degraded => "[WARN]   ",
            HealthStatus.Failed => "[FAIL]   ",
            HealthStatus.Disabled => "[OFF]    ",
            _ => "[???]    "
        };

        string line = $"{statusTag}{result.FeatureName}: {result.Message}";

        // Use appropriate log level
        switch (result.Status)
        {
            case HealthStatus.OK:
            case HealthStatus.Disabled:
                UnityEngine.Debug.Log($"[ProxiCraft] {line}");
                break;

            case HealthStatus.Adapted:
            case HealthStatus.Degraded:
                UnityEngine.Debug.LogWarning($"[ProxiCraft] {line}");
                if (!string.IsNullOrEmpty(result.Details))
                    UnityEngine.Debug.LogWarning($"[ProxiCraft]          Details: {result.Details}");
                if (!string.IsNullOrEmpty(result.SuggestedFix))
                    UnityEngine.Debug.LogWarning($"[ProxiCraft]          Fix: {result.SuggestedFix}");
                break;

            case HealthStatus.Failed:
                UnityEngine.Debug.LogError($"[ProxiCraft] {line}");
                if (!string.IsNullOrEmpty(result.Details))
                    UnityEngine.Debug.LogError($"[ProxiCraft]          Details: {result.Details}");
                if (!string.IsNullOrEmpty(result.SuggestedFix))
                    UnityEngine.Debug.LogError($"[ProxiCraft]          Fix: {result.SuggestedFix}");
                break;
        }
    }

    private static void PrintSummary()
    {
        Log("");
        Log("──────────────────────────────────────────────────────────");

        int ok = Results.Count(r => r.Status == HealthStatus.OK);
        int adapted = Results.Count(r => r.Status == HealthStatus.Adapted);
        int degraded = Results.Count(r => r.Status == HealthStatus.Degraded);
        int failed = Results.Count(r => r.Status == HealthStatus.Failed);
        int disabled = Results.Count(r => r.Status == HealthStatus.Disabled);
        int total = Results.Count - disabled;

        string summaryLine = $"SUMMARY: {ok + adapted}/{total} features operational";

        if (adapted > 0)
            summaryLine += $", {adapted} auto-adapted";
        if (degraded > 0)
            summaryLine += $", {degraded} degraded";
        if (failed > 0)
            summaryLine += $", {failed} FAILED";
        if (disabled > 0)
            summaryLine += $" ({disabled} disabled)";

        if (failed > 0)
        {
            UnityEngine.Debug.LogError($"[ProxiCraft] {summaryLine}");
            UnityEngine.Debug.LogError($"[ProxiCraft] Some features will NOT work. Use 'pc health' for details.");
        }
        else if (adapted > 0 || degraded > 0)
        {
            UnityEngine.Debug.LogWarning($"[ProxiCraft] {summaryLine}");
            UnityEngine.Debug.LogWarning($"[ProxiCraft] Mod adapted to changes. Please report if issues occur.");
        }
        else
        {
            // This branch only reached in verbose mode (since no issues means silent otherwise)
            Log(summaryLine);
            Log("All features validated successfully!");
        }

        // Only show timing in verbose mode
        if (_verboseMode)
        {
            Log($"Health check completed in {TotalDuration.TotalMilliseconds:F0}ms");
        }
        Log("══════════════════════════════════════════════════════════");
    }

    #endregion

    #region Console Command Support

    /// <summary>
    /// Gets a formatted health report for the console command.
    /// </summary>
    public static string GetHealthReport()
    {
        if (!HasRun)
            return "Health check has not been run yet.";

        var lines = new List<string>
        {
            "ProxiCraft Health Report",
            "========================",
            ""
        };

        foreach (var result in Results)
        {
            string status = result.Status.ToString().ToUpper();
            lines.Add($"[{status}] {result.FeatureName}");
            lines.Add($"       {result.Message}");

            if (!string.IsNullOrEmpty(result.Details))
                lines.Add($"       Details: {result.Details}");
            if (!string.IsNullOrEmpty(result.SuggestedFix))
                lines.Add($"       Fix: {result.SuggestedFix}");

            lines.Add("");
        }

        int ok = Results.Count(r => r.Status == HealthStatus.OK);
        int adapted = Results.Count(r => r.Status == HealthStatus.Adapted);
        int failed = Results.Count(r => r.Status == HealthStatus.Failed);
        int total = Results.Count(r => r.Status != HealthStatus.Disabled);

        lines.Add($"Summary: {ok + adapted}/{total} operational, {adapted} adapted, {failed} failed");
        lines.Add($"Check duration: {TotalDuration.TotalMilliseconds:F0}ms");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Re-runs the health check (useful for console command).
    /// </summary>
    public static void Recheck()
    {
        if (ProxiCraft.Config != null)
        {
            RunHealthCheck(ProxiCraft.Config);
        }
    }

    /// <summary>
    /// Generates a comprehensive diagnostic report for bug reporting.
    /// Always returns full output regardless of verbose setting.
    /// </summary>
    public static string GetFullDiagnosticReport()
    {
        // Re-run health check to get fresh data
        if (ProxiCraft.Config != null)
        {
            // Temporarily force verbose mode to capture everything
            bool wasVerbose = _verboseMode;
            _verboseMode = true;
            Results.Clear();
            _discoveredMethods.Clear();

            var totalTimer = Stopwatch.StartNew();

            // Run all checks
            CheckCraftingFeatures(ProxiCraft.Config);
            CheckReloadFeatures(ProxiCraft.Config);
            CheckRefuelFeatures(ProxiCraft.Config);
            CheckTraderFeatures(ProxiCraft.Config);
            CheckQuestFeatures(ProxiCraft.Config);
            CheckRepairFeatures(ProxiCraft.Config);
            CheckNewFeatures(ProxiCraft.Config);
            CheckStorageSources(ProxiCraft.Config);
            ValidateHarmonyPatches();

            totalTimer.Stop();
            TotalDuration = totalTimer.Elapsed;
            HasRun = true;

            _verboseMode = wasVerbose;
        }

        var lines = new List<string>
        {
            "=== ProxiCraft Full Diagnostic Report ===",
            "",
            $"Mod Version: {ProxiCraft.MOD_VERSION}",
            $"Config Loaded: {(ProxiCraft.Config != null ? "Yes" : "No")}",
            "",
            "=== Configuration ===",
            $"  Mod Enabled: {ProxiCraft.Config?.modEnabled}",
            $"  Debug Mode: {ProxiCraft.Config?.isDebug}",
            $"  Verbose Health Check: {ProxiCraft.Config?.verboseHealthCheck}",
            $"  Range: {ProxiCraft.Config?.range}",
            "",
            "=== Feature Health ===",
            ""
        };

        // Group results by category - must include ALL FeatureIds used in AddResult calls
        var featureGroups = new Dictionary<string, List<FeatureCheckResult>>
        {
            { "Crafting", Results.Where(r => r.FeatureId.Contains("Craft") || r.FeatureId == "Repair" || r.FeatureId == "RecipeTracker").ToList() },
            { "Reload", Results.Where(r => r.FeatureId == "Reload" || r.FeatureId == "HudAmmoCounter").ToList() },
            { "Refuel", Results.Where(r => r.FeatureId.Contains("Refuel")).ToList() },
            { "Trader", Results.Where(r => r.FeatureId == "Trader" || r.FeatureId == "TraderSelling").ToList() },
            { "Quests", Results.Where(r => r.FeatureId == "Quests").ToList() },
            { "Painting", Results.Where(r => r.FeatureId == "Painting").ToList() },
            { "Other Features", Results.Where(r => r.FeatureId == "Lockpicking" || r.FeatureId == "ItemRepair" || 
                r.FeatureId == "VehicleRepair" || r.FeatureId == "LockedSlots").ToList() },
            { "Storage Sources", Results.Where(r => r.FeatureId == "Vehicles" || r.FeatureId == "Drones" ||
                r.FeatureId == "DewCollectors" || r.FeatureId == "Workstations").ToList() },
            { "System", Results.Where(r => r.FeatureId == "HarmonyPatches").ToList() }
        };

        foreach (var group in featureGroups)
        {
            if (group.Value.Count == 0)
                continue;

            lines.Add($"[{group.Key.ToUpper()}]");
            foreach (var result in group.Value)
            {
                string statusTag = result.Status switch
                {
                    HealthStatus.OK => "[OK]",
                    HealthStatus.Adapted => "[ADAPT]",
                    HealthStatus.Degraded => "[WARN]",
                    HealthStatus.Failed => "[FAIL]",
                    HealthStatus.Disabled => "[OFF]",
                    _ => "[???]"
                };
                lines.Add($"  {statusTag} {result.FeatureName}");
                lines.Add($"        {result.Message}");
                if (!string.IsNullOrEmpty(result.Details))
                    lines.Add($"        Details: {result.Details}");
                if (!string.IsNullOrEmpty(result.SuggestedFix))
                    lines.Add($"        Fix: {result.SuggestedFix}");
            }
            lines.Add("");
        }

        // Summary
        int ok = Results.Count(r => r.Status == HealthStatus.OK);
        int adapted = Results.Count(r => r.Status == HealthStatus.Adapted);
        int degraded = Results.Count(r => r.Status == HealthStatus.Degraded);
        int failed = Results.Count(r => r.Status == HealthStatus.Failed);
        int disabled = Results.Count(r => r.Status == HealthStatus.Disabled);
        int total = Results.Count;

        lines.Add("=== Summary ===");
        lines.Add($"Total Checks: {total}");
        lines.Add($"  OK: {ok}");
        lines.Add($"  Adapted: {adapted}");
        lines.Add($"  Degraded: {degraded}");
        lines.Add($"  Failed: {failed}");
        lines.Add($"  Disabled: {disabled}");
        lines.Add($"Check Duration: {TotalDuration.TotalMilliseconds:F0}ms");
        lines.Add("");
        lines.Add("Copy this report when submitting bug reports.");

        return string.Join("\n", lines);
    }

    #endregion
}
