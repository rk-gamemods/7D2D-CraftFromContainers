using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProxiCraft;

/// <summary>
/// Handles mod compatibility detection, conflict resolution, and diagnostic reporting.
/// This system helps identify issues with other mods and provides clear troubleshooting info.
/// </summary>
public static class ModCompatibility
{
    // Known potentially conflicting mods (by assembly name or namespace patterns)
    private static readonly Dictionary<string, string> KnownConflictingMods = new Dictionary<string, string>
    {
        // Format: { "AssemblyOrNamespace", "Description of conflict" }

        // HIGH RISK - Direct conflicts (same functionality)
        { "BeyondStorage", "Beyond Storage 2 - provides same functionality, patches same methods. Choose one." },
        { "CraftFromContainers", "Original CraftFromContainers mod - provides same functionality, choose one" },
        { "CraftFromContainersPlus", "CraftFromContainers fork - provides same functionality, choose one" },
        { "CraftFromChests", "Similar mod - will conflict with same patches" },
        { "PullFromContainers", "Similar mod - will conflict with same patches" },
        { "AutoCraft", "Crafting mod - may conflict with recipe methods" },
        
        // MEDIUM RISK - UI overhauls
        { "SMXui", "UI overhaul - may change crafting window structure" },
        { "SMXhud", "HUD overhaul - may affect ingredient display" },
        { "SMXmenu", "Menu overhaul - may change recipe list behavior" },
        
        // LOW RISK - Inventory mods (we're designed to work with these!)
        // These are logged as INFO not WARNING because we're compatible
        { "ExpandedStorage", "Container mod - should be compatible (we only add to container queries)" },
        { "BetterVehicles", "Vehicle mod - may change fuel/storage systems" },
    };

    // Backpack mods and other known compatible mods (logged as compatible, not conflicting)
    private static readonly Dictionary<string, string> KnownBackpackMods = new Dictionary<string, string>
    {
        { "BiggerBackpack", "Inventory expansion - compatible (we use additive patching)" },
        { "BackpackExpansion", "Inventory expansion - compatible (we use additive patching)" },
        { "60SlotBackpack", "Inventory expansion - compatible (we use additive patching)" },
        { "96SlotBackpack", "Inventory expansion - compatible (we use additive patching)" },
        { "LargerBackpack", "Inventory expansion - compatible (we use additive patching)" },
        { "ExtraBackpackSlots", "Inventory expansion - compatible (we use additive patching)" },
        { "ExtendedBackpack", "Inventory expansion - compatible (we use additive patching)" },
        { "BackpackButtons", "Backpack UI mod - compatible (we don't modify UI structure)" },
        { "Inventory", "Inventory mod - likely compatible (we use additive patching)" },
        { "AGF-V2-Backpack72Plus", "AGF 72-slot backpack - compatible (tested)" },
        { "AGF-V2-HUDPlus", "AGF HUD overhaul - compatible (tested)" },

        // Our own companion mods
        { "AudibleBreakingGlassJars", "Glass jar break sound - compatible (patches ItemActionEat, only reads inventory)" },
    };

    // Track detected backpack mods for diagnostic purposes
    private static readonly List<string> DetectedBackpackMods = new List<string>();

    // Track which patches succeeded/failed
    private static readonly Dictionary<string, PatchStatus> PatchStatuses = new Dictionary<string, PatchStatus>();
    
    // Track detected conflicts
    private static readonly List<ConflictInfo> DetectedConflicts = new List<ConflictInfo>();

    /// <summary>
    /// Represents the status of a Harmony patch
    /// </summary>
    public class PatchStatus
    {
        public string PatchName { get; set; }
        public string TargetMethod { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public List<string> OtherPatchOwners { get; set; } = new List<string>();
    }

    /// <summary>
    /// Information about a detected mod conflict
    /// </summary>
    public class ConflictInfo
    {
        public string ModName { get; set; }
        public string ConflictType { get; set; }
        public string Description { get; set; }
        public string Recommendation { get; set; }
    }

    /// <summary>
    /// Scans for potential mod conflicts at startup
    /// </summary>
    public static void ScanForConflicts()
    {
        DetectedConflicts.Clear();
        
        try
        {
            // Check loaded assemblies for known conflicts
            ScanLoadedAssemblies();
            
            // Check Harmony patches for conflicts
            ScanHarmonyPatches();
            
            // Report findings
            ReportConflicts();
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error scanning for conflicts: {ex.Message}");
        }
    }

    private static void ScanLoadedAssemblies()
    {
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        
        foreach (var assembly in loadedAssemblies)
        {
            try
            {
                string assemblyName = assembly.GetName().Name;
                
                // Check for known backpack mods (these are COMPATIBLE, not conflicts!)
                foreach (var backpackMod in KnownBackpackMods)
                {
                    if (assemblyName.IndexOf(backpackMod.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        DetectedBackpackMods.Add(assemblyName);
                        ProxiCraft.Log($"Detected backpack mod: {assemblyName} - {backpackMod.Value}");
                    }
                }
                
                // Check for known conflicting mods
                foreach (var knownConflict in KnownConflictingMods)
                {
                    if (assemblyName.IndexOf(knownConflict.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Determine severity based on the mod type
                        bool isHighRisk = knownConflict.Key == "BeyondStorage" ||
                                         knownConflict.Key == "CraftFromContainers" ||
                                         knownConflict.Key == "CraftFromContainersPlus" ||
                                         knownConflict.Key == "CraftFromChests" ||
                                         knownConflict.Key == "PullFromContainers" ||
                                         knownConflict.Key == "AutoCraft";
                        
                        DetectedConflicts.Add(new ConflictInfo
                        {
                            ModName = assemblyName,
                            ConflictType = isHighRisk ? "High Risk Conflict" : "Potential Conflict",
                            Description = knownConflict.Value,
                            Recommendation = isHighRisk 
                                ? $"RECOMMENDED: Disable '{assemblyName}' as it provides similar functionality."
                                : $"If you experience issues, try disabling '{assemblyName}' to isolate the problem."
                        });
                    }
                }
            }
            catch
            {
                // Some assemblies may not allow name access
            }
        }
        
        // Log detected backpack mods summary
        if (DetectedBackpackMods.Count > 0)
        {
            ProxiCraft.Log($"Found {DetectedBackpackMods.Count} backpack mod(s) - using additive patching for compatibility");
        }
    }

    private static void ScanHarmonyPatches()
    {
        // Methods we DIRECTLY patch that could conflict with other mods
        // Categorized by conflict risk level
        var criticalMethods = new[]
        {
            // HIGH RISK - Core inventory operations, conflicts likely cause duplication or item loss
            ("XUiM_PlayerInventory", "HasItems"),
            ("XUiM_PlayerInventory", "RemoveItems"),
            ("XUiM_PlayerInventory", "GetItemCount"),
            ("Inventory", "DecItem"),
            ("Bag", "DecItem"),
            ("Bag", "AddItem"),
            
            // MEDIUM RISK - Feature-specific, conflicts may cause feature malfunction
            ("ItemActionEntryCraft", "hasItems"),           // Crafting availability check
            ("XUiC_RecipeList", "Update"),                  // Recipe list display (transpiler)
            ("XUiC_RecipeCraftCount", "calcMaxCraftable"),  // Max craft count (transpiler)
            ("EntityVehicle", "hasGasCan"),                 // Vehicle refuel check
            ("EntityVehicle", "takeFuel"),                  // Vehicle refuel action
            ("AnimatorRangedReloadState", "GetAmmoCountToReload"), // Reload ammo count
            ("ItemActionTextureBlock", "checkAmmo"),        // Paint ammo check
            ("ItemActionTextureBlock", "decreaseAmmo"),     // Paint ammo consume
            ("XUiC_PowerSourceStats", "BtnRefuel_OnPress"), // Generator refuel
            
            // Note: We do NOT directly patch GetAllItemStacks - we inject into methods that CALL it
            // So another mod patching GetAllItemStacks is not a conflict with us
            
            // LOW RISK - UI/state tracking, usually safe even with conflicts
            // (not listed - OnOpen, OnClose, HandleSlotChanged, etc.)
        };

        foreach (var (typeName, methodName) in criticalMethods)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) continue;

                var method = AccessTools.Method(type, methodName);
                if (method == null) continue;

                var patches = Harmony.GetPatchInfo(method);
                if (patches == null) continue;

                // Check for other mods patching this method
                var allPatches = patches.Prefixes
                    .Concat(patches.Postfixes)
                    .Concat(patches.Transpilers)
                    .Where(p => p.owner != "rkgamemods.proxicraft")
                    .ToList();

                if (allPatches.Any())
                {
                    var otherOwners = allPatches.Select(p => p.owner).Distinct().ToList();
                    
                    DetectedConflicts.Add(new ConflictInfo
                    {
                        ModName = string.Join(", ", otherOwners),
                        ConflictType = "Harmony Patch Conflict",
                        Description = $"Method '{typeName}.{methodName}' is also patched by: {string.Join(", ", otherOwners)}",
                        Recommendation = "This may cause unexpected behavior. Check if features work correctly."
                    });
                }
            }
            catch (Exception ex)
            {
                ProxiCraft.LogDebug($"Error checking patches for {typeName}.{methodName}: {ex.Message}");
            }
        }
    }

    private static void ReportConflicts()
    {
        if (DetectedConflicts.Count == 0)
        {
            ProxiCraft.Log("No mod conflicts detected.");
            return;
        }

        ProxiCraft.LogWarning($"=== POTENTIAL MOD CONFLICTS DETECTED ({DetectedConflicts.Count}) ===");
        
        foreach (var conflict in DetectedConflicts)
        {
            ProxiCraft.LogWarning($"  [{conflict.ConflictType}] {conflict.ModName}");
            ProxiCraft.LogWarning($"    Issue: {conflict.Description}");
            ProxiCraft.LogWarning($"    Tip: {conflict.Recommendation}");
        }
        
        ProxiCraft.LogWarning("=== END CONFLICT REPORT ===");
    }

    /// <summary>
    /// Records the status of a patch attempt
    /// </summary>
    public static void RecordPatchStatus(string patchName, string targetMethod, bool success, string errorMessage = null, Exception ex = null)
    {
        PatchStatuses[patchName] = new PatchStatus
        {
            PatchName = patchName,
            TargetMethod = targetMethod,
            Success = success,
            ErrorMessage = errorMessage,
            Exception = ex
        };

        if (!success)
        {
            ProxiCraft.LogWarning($"Patch '{patchName}' failed for '{targetMethod}': {errorMessage}");
        }
    }

    /// <summary>
    /// Gets a diagnostic report of all patch statuses
    /// </summary>
    public static string GetDiagnosticReport()
    {
        var report = new System.Text.StringBuilder();
        
        report.AppendLine("=== ProxiCraft Diagnostic Report ===");
        report.AppendLine($"Mod Version: {ProxiCraft.MOD_VERSION}");
        report.AppendLine($"Game Version: {Constants.cVersionInformation.LongString}");
        report.AppendLine($"Config Enabled: {ProxiCraft.Config?.modEnabled}");
        report.AppendLine();
        
        // Backpack Mod Compatibility
        report.AppendLine("--- Backpack Mod Compatibility ---");
        if (DetectedBackpackMods.Count == 0)
        {
            report.AppendLine("  No backpack mods detected");
        }
        else
        {
            report.AppendLine($"  Detected {DetectedBackpackMods.Count} backpack mod(s):");
            foreach (var mod in DetectedBackpackMods)
            {
                report.AppendLine($"    ✓ {mod} (compatible - using additive patching)");
            }
        }
        report.AppendLine();
        
        // Patch Status
        report.AppendLine("--- Patch Status ---");
        int successCount = PatchStatuses.Count(p => p.Value.Success);
        int failCount = PatchStatuses.Count(p => !p.Value.Success);
        report.AppendLine($"Successful: {successCount}, Failed: {failCount}");
        
        foreach (var patch in PatchStatuses.OrderBy(p => p.Value.Success))
        {
            string status = patch.Value.Success ? "✓" : "✗";
            report.AppendLine($"  {status} {patch.Key} -> {patch.Value.TargetMethod}");
            if (!patch.Value.Success && !string.IsNullOrEmpty(patch.Value.ErrorMessage))
            {
                report.AppendLine($"      Error: {patch.Value.ErrorMessage}");
            }
        }
        report.AppendLine();
        
        // Detected Conflicts
        report.AppendLine("--- Detected Conflicts ---");
        if (DetectedConflicts.Count == 0)
        {
            report.AppendLine("  None detected");
        }
        else
        {
            foreach (var conflict in DetectedConflicts)
            {
                report.AppendLine($"  • {conflict.ModName}: {conflict.Description}");
            }
        }
        report.AppendLine();
        
        // Feature Status
        report.AppendLine("--- Feature Status ---");
        report.AppendLine($"  Crafting from containers: {(ProxiCraft.Config?.modEnabled == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Repair/Upgrade: {(ProxiCraft.Config?.enableForRepairAndUpgrade == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Trader purchases: {(ProxiCraft.Config?.enableForTrader == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Vehicle refuel: {(ProxiCraft.Config?.enableForRefuel == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Weapon reload: {(ProxiCraft.Config?.enableForReload == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Vehicle storage: {(ProxiCraft.Config?.pullFromVehicles == true ? "Enabled" : "Disabled")}");
        report.AppendLine($"  Range: {ProxiCraft.Config?.range ?? -1} (-1 = unlimited)");
        
        report.AppendLine("=== End Report ===");
        
        return report.ToString();
    }

    /// <summary>
    /// Checks if a specific feature should be enabled based on patch success
    /// </summary>
    public static bool IsFeatureAvailable(string featureName)
    {
        // Map features to required patches
        var featurePatchMap = new Dictionary<string, string[]>
        {
            { "Crafting", new[] { "ItemActionEntryCraft_OnActivated", "XUiC_RecipeList_Update" } },
            { "Reload", new[] { "AnimatorRangedReloadState_GetAmmoCountToReload" } },
            { "Refuel", new[] { "EntityVehicle_hasGasCan", "EntityVehicle_takeFuel" } },
            { "Trader", new[] { "ItemActionEntryPurchase_RefreshEnabled" } },
        };

        if (!featurePatchMap.TryGetValue(featureName, out var requiredPatches))
            return true; // Unknown feature, assume available

        return requiredPatches.All(p => 
            !PatchStatuses.TryGetValue(p, out var status) || status.Success);
    }

    /// <summary>
    /// Gets list of detected conflicts
    /// </summary>
    public static IReadOnlyList<ConflictInfo> GetConflicts() => DetectedConflicts.AsReadOnly();

    /// <summary>
    /// Gets list of failed patches
    /// </summary>
    public static IEnumerable<PatchStatus> GetFailedPatches() => 
        PatchStatuses.Values.Where(p => !p.Success);

    /// <summary>
    /// Returns true if there are any critical conflicts (patches that failed)
    /// </summary>
    public static bool HasCriticalConflicts() => 
        PatchStatuses.Values.Any(p => !p.Success);

    /// <summary>
    /// Returns true if there are any detected conflicts
    /// </summary>
    public static bool HasAnyConflicts() => 
        DetectedConflicts.Count > 0;

    /// <summary>
    /// Returns true if any backpack mods were detected
    /// </summary>
    public static bool HasBackpackMods() => 
        DetectedBackpackMods.Count > 0;

    /// <summary>
    /// Gets list of detected backpack mods
    /// </summary>
    public static IReadOnlyList<string> GetBackpackMods() => 
        DetectedBackpackMods.AsReadOnly();
}
