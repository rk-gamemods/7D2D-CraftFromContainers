using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProxiCraft;

/// <summary>
/// Adaptive patching system that works alongside backpack/inventory mods.
/// 
/// KEY DESIGN PRINCIPLES:
/// 1. ADDITIVE ONLY: We never modify inventory structure, only add external container items
/// 2. POSTFIX PREFERRED: Use Postfix patches to modify results, not Transpilers
/// 3. DETECT & ADAPT: Check if methods exist/changed before patching
/// 4. GRACEFUL FALLBACK: If a patch can't be applied, skip it (feature disabled, not crash)
/// 
/// BACKPACK MOD COMPATIBILITY:
/// - Backpack mods typically modify: Inventory size, Bag slots, XUiM_PlayerInventory methods
/// - We only READ from inventory using standard interfaces
/// - We APPEND container items to whatever inventory returns
/// - We never REPLACE or MODIFY inventory data structures
/// </summary>
public static class AdaptivePatching
{
    /// <summary>
    /// Checks if a method exists and has the expected signature.
    /// Returns false if another mod may have changed it.
    /// </summary>
    public static bool ValidateMethodSignature(Type type, string methodName, Type[] paramTypes, Type returnType)
    {
        try
        {
            var method = paramTypes != null 
                ? AccessTools.Method(type, methodName, paramTypes)
                : AccessTools.Method(type, methodName);

            if (method == null)
            {
                ProxiCraft.LogDebug($"Method not found: {type.Name}.{methodName}");
                return false;
            }

            if (returnType != null && method.ReturnType != returnType)
            {
                ProxiCraft.LogDebug($"Method {type.Name}.{methodName} return type changed: expected {returnType.Name}, got {method.ReturnType.Name}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"Error validating {type.Name}.{methodName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if another mod has already patched a method with a Transpiler.
    /// Transpiler conflicts are the most dangerous - we should avoid stacking them.
    /// </summary>
    public static bool HasTranspilerConflict(MethodBase method)
    {
        try
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches?.Transpilers != null && patches.Transpilers.Count > 0)
            {
                var otherTranspilers = patches.Transpilers
                    .Where(p => p.owner != "rkgamemods.proxicraft")
                    .ToList();

                if (otherTranspilers.Any())
                {
                    string owners = string.Join(", ", otherTranspilers.Select(p => p.owner));
                    ProxiCraft.LogWarning($"Transpiler conflict on {method.DeclaringType?.Name}.{method.Name}: {owners}");
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines the best patching strategy for a method based on conflicts.
    /// </summary>
    public static PatchStrategy DeterminePatchStrategy(Type type, string methodName, Type[] paramTypes = null)
    {
        try
        {
            var method = paramTypes != null 
                ? AccessTools.Method(type, methodName, paramTypes)
                : AccessTools.Method(type, methodName);

            if (method == null)
                return PatchStrategy.Skip;

            var patches = Harmony.GetPatchInfo(method);
            if (patches == null)
                return PatchStrategy.Full; // No other patches, we're safe

            // Check for Transpiler conflicts
            bool hasOtherTranspilers = patches.Transpilers?
                .Any(p => p.owner != "rkgamemods.proxicraft") ?? false;

            // Check for Prefix conflicts that might skip original
            bool hasBlockingPrefix = patches.Prefixes?
                .Any(p => p.owner != "rkgamemods.proxicraft") ?? false;

            if (hasOtherTranspilers)
            {
                // Another mod is using Transpiler - use Postfix only (safest)
                ProxiCraft.LogDebug($"Using Postfix-only for {type.Name}.{methodName} (Transpiler conflict)");
                return PatchStrategy.PostfixOnly;
            }

            if (hasBlockingPrefix)
            {
                // Another mod might skip the original - use both but be careful
                ProxiCraft.LogDebug($"Using careful patching for {type.Name}.{methodName} (Prefix detected)");
                return PatchStrategy.Careful;
            }

            return PatchStrategy.Full;
        }
        catch
        {
            return PatchStrategy.Skip;
        }
    }

    public enum PatchStrategy
    {
        /// <summary>Full patching including Transpilers</summary>
        Full,
        /// <summary>Only use Postfix patches (safest)</summary>
        PostfixOnly,
        /// <summary>Use patches but with extra null checks</summary>
        Careful,
        /// <summary>Don't patch this method</summary>
        Skip
    }

    /// <summary>
    /// Safely merges container items with inventory items.
    /// This is the core "additive" operation - never modifies source, only creates new combined list.
    /// </summary>
    public static List<ItemStack> SafeMergeItems(IEnumerable<ItemStack> inventoryItems, IEnumerable<ItemStack> containerItems)
    {
        var result = new List<ItemStack>();
        
        // First, add all inventory items (whatever structure backpack mod created)
        if (inventoryItems != null)
        {
            foreach (var item in inventoryItems)
            {
                if (item != null && !item.IsEmpty())
                {
                    result.Add(item);
                }
            }
        }

        // Then, add container items (our addition)
        if (containerItems != null)
        {
            foreach (var item in containerItems)
            {
                if (item != null && !item.IsEmpty())
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Safely adds a count to an existing count.
    /// Used for item counting operations.
    /// </summary>
    public static int SafeAddCount(int inventoryCount, int containerCount)
    {
        // Handle potential overflow
        try
        {
            checked
            {
                return inventoryCount + containerCount;
            }
        }
        catch (OverflowException)
        {
            return int.MaxValue;
        }
    }
}

/// <summary>
/// Runtime method replacer for dynamic compatibility.
/// Can swap out patching strategies at runtime based on detected conflicts.
/// </summary>
public static class DynamicPatchManager
{
    private static readonly Dictionary<string, bool> FeatureStatus = new Dictionary<string, bool>();

    /// <summary>
    /// Attempts to patch a method, falling back to alternative strategies if needed.
    /// </summary>
    public static bool TryPatchWithFallback(
        Harmony harmony,
        Type targetType,
        string methodName,
        HarmonyMethod transpiler,
        HarmonyMethod postfix,
        string featureId)
    {
        try
        {
            var strategy = AdaptivePatching.DeterminePatchStrategy(targetType, methodName);

            switch (strategy)
            {
                case AdaptivePatching.PatchStrategy.Full:
                    // Use Transpiler if provided, otherwise Postfix
                    if (transpiler != null)
                    {
                        harmony.Patch(
                            AccessTools.Method(targetType, methodName),
                            transpiler: transpiler);
                    }
                    else if (postfix != null)
                    {
                        harmony.Patch(
                            AccessTools.Method(targetType, methodName),
                            postfix: postfix);
                    }
                    FeatureStatus[featureId] = true;
                    return true;

                case AdaptivePatching.PatchStrategy.PostfixOnly:
                    // Only use Postfix (skip Transpiler due to conflict)
                    if (postfix != null)
                    {
                        harmony.Patch(
                            AccessTools.Method(targetType, methodName),
                            postfix: postfix);
                        FeatureStatus[featureId] = true;
                        return true;
                    }
                    ProxiCraft.LogWarning($"Feature '{featureId}' requires Transpiler but has conflict - disabled");
                    FeatureStatus[featureId] = false;
                    return false;

                case AdaptivePatching.PatchStrategy.Careful:
                    // Use Postfix with extra safety
                    if (postfix != null)
                    {
                        harmony.Patch(
                            AccessTools.Method(targetType, methodName),
                            postfix: postfix);
                        FeatureStatus[featureId] = true;
                        return true;
                    }
                    FeatureStatus[featureId] = false;
                    return false;

                case AdaptivePatching.PatchStrategy.Skip:
                default:
                    ProxiCraft.LogWarning($"Feature '{featureId}' skipped - method not compatible");
                    FeatureStatus[featureId] = false;
                    return false;
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Failed to patch for feature '{featureId}': {ex.Message}");
            FeatureStatus[featureId] = false;
            return false;
        }
    }

    /// <summary>
    /// Checks if a feature was successfully enabled.
    /// </summary>
    public static bool IsFeatureEnabled(string featureId)
    {
        return FeatureStatus.TryGetValue(featureId, out bool enabled) && enabled;
    }
}
