using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProxiCraft;

/// <summary>
/// Provides robust transpiler utilities that survive game updates.
///
/// KEY STRATEGIES FOR STABILITY:
/// 1. SIGNATURE MATCHING: Find methods by signature, not exact reference
/// 2. MULTIPLE PATTERNS: Try different IL patterns to find the target
/// 3. GRACEFUL FALLBACK: Return original code if pattern not found
/// 4. FEATURE DISABLE: Auto-disable feature if transpiler fails
/// 5. CONTEXT VALIDATION: Verify surrounding IL before patching
/// </summary>
public static class RobustTranspiler
{
    // Track which transpilers succeeded for runtime feature checks
    private static readonly Dictionary<string, bool> _transpilerStatus = new();

    /// <summary>
    /// Checks if a transpiler successfully applied its patch.
    /// </summary>
    public static bool DidTranspilerSucceed(string featureId)
    {
        return _transpilerStatus.TryGetValue(featureId, out bool success) && success;
    }

    /// <summary>
    /// Records whether a transpiler succeeded, for runtime feature checks.
    /// </summary>
    public static void RecordTranspilerStatus(string featureId, bool success)
    {
        _transpilerStatus[featureId] = success;

        if (!success)
        {
            ProxiCraft.LogWarning($"Transpiler '{featureId}' failed - feature will use fallback behavior");
        }
    }

    /// <summary>
    /// Finds a method call in IL by matching the method signature.
    /// More robust than exact method reference matching.
    /// </summary>
    /// <param name="codes">The IL instructions to search</param>
    /// <param name="declaringType">The type that declares the method</param>
    /// <param name="methodName">The method name</param>
    /// <param name="parameterTypes">Optional parameter types for overload resolution</param>
    /// <returns>Index of the call instruction, or -1 if not found</returns>
    public static int FindMethodCall(
        List<CodeInstruction> codes,
        Type declaringType,
        string methodName,
        Type[] parameterTypes = null)
    {
        for (int i = 0; i < codes.Count; i++)
        {
            if (!IsMethodCallOpCode(codes[i].opcode))
                continue;

            if (codes[i].operand is not MethodInfo method)
                continue;

            // Match by signature rather than exact reference
            if (MatchesMethodSignature(method, declaringType, methodName, parameterTypes))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds all method calls matching the signature.
    /// Useful when the same method is called multiple times.
    /// </summary>
    public static List<int> FindAllMethodCalls(
        List<CodeInstruction> codes,
        Type declaringType,
        string methodName,
        Type[] parameterTypes = null)
    {
        var indices = new List<int>();

        for (int i = 0; i < codes.Count; i++)
        {
            if (!IsMethodCallOpCode(codes[i].opcode))
                continue;

            if (codes[i].operand is not MethodInfo method)
                continue;

            if (MatchesMethodSignature(method, declaringType, methodName, parameterTypes))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>
    /// Matches a method by signature, allowing for inheritance and interface implementation.
    /// </summary>
    private static bool MatchesMethodSignature(
        MethodInfo method,
        Type declaringType,
        string methodName,
        Type[] parameterTypes)
    {
        // Check method name
        if (method.Name != methodName)
            return false;

        // Check declaring type (allow subclasses and interfaces)
        if (!IsTypeMatch(method.DeclaringType, declaringType))
            return false;

        // Check parameter types if specified
        if (parameterTypes != null)
        {
            var methodParams = method.GetParameters();
            if (methodParams.Length != parameterTypes.Length)
                return false;

            for (int i = 0; i < parameterTypes.Length; i++)
            {
                if (methodParams[i].ParameterType != parameterTypes[i])
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type matches, considering inheritance.
    /// </summary>
    private static bool IsTypeMatch(Type actual, Type expected)
    {
        if (actual == expected)
            return true;

        // Check if actual is a subclass of expected
        if (expected.IsAssignableFrom(actual))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if an opcode is a method call.
    /// </summary>
    private static bool IsMethodCallOpCode(OpCode opcode)
    {
        return opcode == OpCodes.Call ||
               opcode == OpCodes.Callvirt ||
               opcode == OpCodes.Calli;
    }

    /// <summary>
    /// Safely replaces a method call with another method.
    /// Returns true if successful, false if the target wasn't found.
    /// </summary>
    /// <param name="codes">The IL instructions</param>
    /// <param name="targetType">Type containing the method to replace</param>
    /// <param name="targetMethodName">Name of the method to replace</param>
    /// <param name="replacementMethod">The replacement method</param>
    /// <param name="featureId">Feature ID for status tracking</param>
    /// <param name="targetParamTypes">Optional parameter types for overload resolution</param>
    /// <param name="occurrence">Which occurrence to replace (1 = first, -1 = all)</param>
    /// <returns>True if replacement succeeded</returns>
    public static bool TryReplaceMethodCall(
        List<CodeInstruction> codes,
        Type targetType,
        string targetMethodName,
        MethodInfo replacementMethod,
        string featureId,
        Type[] targetParamTypes = null,
        int occurrence = 1)
    {
        try
        {
            var indices = FindAllMethodCalls(codes, targetType, targetMethodName, targetParamTypes);

            if (indices.Count == 0)
            {
                ProxiCraft.LogWarning($"[{featureId}] Could not find {targetType.Name}.{targetMethodName} in IL");
                RecordTranspilerStatus(featureId, false);
                return false;
            }

            // Validate replacement method signature matches
            var targetMethod = AccessTools.Method(targetType, targetMethodName, targetParamTypes);
            if (targetMethod != null && !ValidateReplacementSignature(targetMethod, replacementMethod))
            {
                ProxiCraft.LogWarning($"[{featureId}] Replacement method signature doesn't match target");
                RecordTranspilerStatus(featureId, false);
                return false;
            }

            // Replace the specified occurrence(s)
            int replaced = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                if (occurrence == -1 || occurrence == i + 1)
                {
                    int idx = indices[i];
                    codes[idx].opcode = OpCodes.Call;
                    codes[idx].operand = replacementMethod;
                    replaced++;

                    ProxiCraft.LogDebug($"[{featureId}] Replaced call at IL index {idx}");
                }
            }

            if (replaced > 0)
            {
                ProxiCraft.LogDebug($"[{featureId}] Successfully replaced {replaced} method call(s)");
                RecordTranspilerStatus(featureId, true);
                return true;
            }

            RecordTranspilerStatus(featureId, false);
            return false;
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"[{featureId}] Transpiler error: {ex.Message}");
            RecordTranspilerStatus(featureId, false);
            return false;
        }
    }

    /// <summary>
    /// Validates that the replacement method has a compatible signature.
    /// The replacement should have the same parameters (possibly with 'this' as first param for instance methods).
    /// </summary>
    private static bool ValidateReplacementSignature(MethodInfo target, MethodInfo replacement)
    {
        var targetParams = target.GetParameters();
        var replacementParams = replacement.GetParameters();

        // For instance methods, replacement may have 'this' as first parameter
        int offset = 0;
        if (!target.IsStatic && replacement.IsStatic)
        {
            // Static replacement for instance method - first param should be the instance type
            if (replacementParams.Length < 1)
                return false;

            if (!target.DeclaringType.IsAssignableFrom(replacementParams[0].ParameterType))
                return false;

            offset = 1;
        }

        // Check remaining parameters match
        if (replacementParams.Length - offset != targetParams.Length)
            return false;

        for (int i = 0; i < targetParams.Length; i++)
        {
            if (replacementParams[i + offset].ParameterType != targetParams[i].ParameterType)
                return false;
        }

        // Return types should be compatible
        if (target.ReturnType != replacement.ReturnType &&
            !target.ReturnType.IsAssignableFrom(replacement.ReturnType))
            return false;

        return true;
    }

    /// <summary>
    /// Injects a method call after a target method call.
    /// Useful for adding behavior without replacing.
    /// </summary>
    public static bool TryInjectAfterMethodCall(
        List<CodeInstruction> codes,
        Type targetType,
        string targetMethodName,
        MethodInfo injectedMethod,
        string featureId,
        Type[] targetParamTypes = null,
        CodeInstruction[] additionalInstructions = null)
    {
        try
        {
            int idx = FindMethodCall(codes, targetType, targetMethodName, targetParamTypes);

            if (idx == -1)
            {
                ProxiCraft.LogWarning($"[{featureId}] Could not find {targetType.Name}.{targetMethodName} in IL");
                RecordTranspilerStatus(featureId, false);
                return false;
            }

            // Insert after the target call
            int insertIdx = idx + 1;

            // Insert additional instructions first (in reverse order to maintain order)
            if (additionalInstructions != null)
            {
                for (int i = additionalInstructions.Length - 1; i >= 0; i--)
                {
                    codes.Insert(insertIdx, additionalInstructions[i]);
                }
            }

            // Insert the injected method call
            codes.Insert(insertIdx, new CodeInstruction(OpCodes.Call, injectedMethod));

            ProxiCraft.LogDebug($"[{featureId}] Injected call after IL index {idx}");
            RecordTranspilerStatus(featureId, true);
            return true;
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"[{featureId}] Transpiler injection error: {ex.Message}");
            RecordTranspilerStatus(featureId, false);
            return false;
        }
    }

    /// <summary>
    /// Creates a safe transpiler wrapper that catches errors and returns original code on failure.
    /// </summary>
    public static IEnumerable<CodeInstruction> SafeTranspile(
        IEnumerable<CodeInstruction> instructions,
        string featureId,
        Func<List<CodeInstruction>, bool> patchAction)
    {
        var codes = new List<CodeInstruction>(instructions);

        try
        {
            bool success = patchAction(codes);

            if (!success)
            {
                ProxiCraft.LogWarning($"[{featureId}] Transpiler could not find injection point");
                ProxiCraft.LogWarning($"[{featureId}] This may be caused by a game update - feature disabled");
                RecordTranspilerStatus(featureId, false);
                return instructions; // Return original unchanged
            }

            RecordTranspilerStatus(featureId, true);
            return codes.AsEnumerable();
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"[{featureId}] Transpiler failed: {ex.Message}");
            ProxiCraft.LogWarning($"[{featureId}] Returning original code to prevent crash");
            RecordTranspilerStatus(featureId, false);
            return instructions; // Return original unchanged
        }
    }

    /// <summary>
    /// Logs IL instructions for debugging transpiler issues.
    /// Only logs when debug mode is enabled.
    /// </summary>
    public static void DebugLogIL(List<CodeInstruction> codes, string context, int startIdx = 0, int count = 20)
    {
        if (ProxiCraft.Config?.isDebug != true)
            return;

        ProxiCraft.LogDebug($"=== IL Dump: {context} ===");

        int endIdx = Math.Min(startIdx + count, codes.Count);
        for (int i = startIdx; i < endIdx; i++)
        {
            var code = codes[i];
            string operandStr = code.operand?.ToString() ?? "null";

            // Shorten method operands for readability
            if (code.operand is MethodInfo method)
            {
                operandStr = $"{method.DeclaringType?.Name}.{method.Name}";
            }

            ProxiCraft.LogDebug($"  [{i:D4}] {code.opcode,-12} {operandStr}");
        }

        ProxiCraft.LogDebug($"=== End IL Dump ===");
    }
}
