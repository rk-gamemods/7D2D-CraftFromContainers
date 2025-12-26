using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProxiCraft;

/// <summary>
/// Safe patching utilities that wrap Harmony operations with error handling.
/// Provides graceful degradation when patches fail instead of crashing.
/// </summary>
public static class SafePatcher
{
    /// <summary>
    /// Safely applies all patches from an assembly, logging any failures
    /// </summary>
    public static int ApplyPatches(Harmony harmony, Assembly assembly)
    {
        int successCount = 0;
        int failCount = 0;

        try
        {
            UnityEngine.Debug.Log("[ProxiCraft] SafePatcher.ApplyPatches starting...");
            
            // Get all types with Harmony patches
            var patchTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Any())
                .ToList();

            UnityEngine.Debug.Log($"[ProxiCraft] Found {patchTypes.Count} patch types to process");

            foreach (var patchType in patchTypes)
            {
                try
                {
                    var patchAttr = patchType.GetCustomAttribute<HarmonyPatch>();
                    string targetDesc = GetPatchTargetDescription(patchAttr, patchType);

                    UnityEngine.Debug.Log($"[ProxiCraft] Applying patch: {patchType.Name} -> {targetDesc}");
                    
                    harmony.CreateClassProcessor(patchType).Patch();
                    
                    ModCompatibility.RecordPatchStatus(
                        patchType.Name, 
                        targetDesc, 
                        true);
                    
                    successCount++;
                    UnityEngine.Debug.Log($"[ProxiCraft] Successfully patched: {patchType.Name}");
                }
                catch (Exception ex)
                {
                    failCount++;
                    
                    var patchAttr = patchType.GetCustomAttribute<HarmonyPatch>();
                    string targetDesc = GetPatchTargetDescription(patchAttr, patchType);
                    
                    ModCompatibility.RecordPatchStatus(
                        patchType.Name,
                        targetDesc,
                        false,
                        ex.Message,
                        ex);

                    UnityEngine.Debug.LogWarning($"[ProxiCraft] Failed to patch {patchType.Name}: {ex.Message}");
                    UnityEngine.Debug.LogWarning($"[ProxiCraft]   Stack trace: {ex.StackTrace}");
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[ProxiCraft] Critical error during patching: {ex.Message}");
            UnityEngine.Debug.LogError($"[ProxiCraft] {ex.StackTrace}");
        }

        UnityEngine.Debug.Log($"[ProxiCraft] Patching complete: {successCount} successful, {failCount} failed");
        
        return successCount;
    }

    private static string GetPatchTargetDescription(HarmonyPatch attr, Type patchType)
    {
        if (attr == null)
            return "Unknown";

        // Try to get from attribute
        if (attr.info.declaringType != null && !string.IsNullOrEmpty(attr.info.methodName))
        {
            return $"{attr.info.declaringType.Name}.{attr.info.methodName}";
        }

        // Try to get from nested HarmonyPatch attributes
        var allAttrs = patchType.GetCustomAttributes<HarmonyPatch>().ToList();
        string typeName = null;
        string methodName = null;

        foreach (var a in allAttrs)
        {
            if (a.info.declaringType != null)
                typeName = a.info.declaringType.Name;
            if (!string.IsNullOrEmpty(a.info.methodName))
                methodName = a.info.methodName;
        }

        if (typeName != null || methodName != null)
            return $"{typeName ?? "?"}.{methodName ?? "?"}";

        return patchType.Name;
    }

    /// <summary>
    /// Wraps a transpiler to catch and handle errors gracefully
    /// </summary>
    public static IEnumerable<CodeInstruction> SafeTranspiler(
        IEnumerable<CodeInstruction> instructions,
        string patchName,
        Func<List<CodeInstruction>, bool> patchAction)
    {
        var codes = new List<CodeInstruction>(instructions);
        
        try
        {
            bool success = patchAction(codes);
            
            if (!success)
            {
                ProxiCraft.LogWarning($"Transpiler '{patchName}' could not find injection point");
                ProxiCraft.LogWarning("  This may be caused by a game update or conflicting mod");
                ProxiCraft.LogWarning("  The feature will be disabled but the game should work");
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Transpiler '{patchName}' failed: {ex.Message}");
            ProxiCraft.LogWarning("  Returning original code to prevent crash");
            
            // Return original instructions to prevent crash
            return instructions;
        }

        return codes.AsEnumerable();
    }

    /// <summary>
    /// Wraps a Prefix/Postfix to catch and log errors
    /// </summary>
    public static void SafeExecute(string patchName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ProxiCraft.LogWarning($"Error in {patchName}: {ex.Message}");
            
            if (ProxiCraft.Config?.isDebug == true)
            {
                ProxiCraft.LogWarning($"  Stack: {ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Wraps a function that returns a value, with fallback on error
    /// </summary>
    public static T SafeExecute<T>(string patchName, Func<T> action, T fallbackValue)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            ProxiCraft.LogWarning($"Error in {patchName}: {ex.Message}");
            return fallbackValue;
        }
    }

    /// <summary>
    /// Helper to find and patch a specific method call in IL code
    /// </summary>
    public static bool TryInjectAfterMethodCall(
        List<CodeInstruction> codes,
        Type targetType,
        string targetMethodName,
        Type[] targetMethodParams,
        MethodInfo injectedMethod,
        params CodeInstruction[] additionalInstructions)
    {
        var targetMethod = targetMethodParams != null
            ? AccessTools.Method(targetType, targetMethodName, targetMethodParams)
            : AccessTools.Method(targetType, targetMethodName);

        if (targetMethod == null)
        {
            ProxiCraft.LogDebug($"Target method not found: {targetType.Name}.{targetMethodName}");
            return false;
        }

        for (int i = 0; i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) &&
                codes[i].operand is MethodInfo method &&
                method == targetMethod)
            {
                // Insert additional instructions first (in reverse order)
                int insertIndex = i + 1;
                
                if (additionalInstructions != null)
                {
                    for (int j = additionalInstructions.Length - 1; j >= 0; j--)
                    {
                        codes.Insert(insertIndex, additionalInstructions[j]);
                    }
                }

                // Insert the injected method call
                codes.Insert(insertIndex, new CodeInstruction(OpCodes.Call, injectedMethod));
                
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Helper to replace a method call with another
    /// </summary>
    public static bool TryReplaceMethodCall(
        List<CodeInstruction> codes,
        Type targetType,
        string targetMethodName,
        MethodInfo replacementMethod)
    {
        var targetMethod = AccessTools.Method(targetType, targetMethodName);
        if (targetMethod == null)
            return false;

        bool found = false;
        
        for (int i = 0; i < codes.Count; i++)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) &&
                codes[i].operand is MethodInfo method &&
                method == targetMethod)
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = replacementMethod;
                found = true;
            }
        }

        return found;
    }
}
