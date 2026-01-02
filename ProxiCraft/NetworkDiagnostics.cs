using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;

namespace ProxiCraft;

/// <summary>
/// Stores detailed information about an observed packet type for diagnostics.
/// </summary>
public class PacketInfo
{
    public string TypeName;
    public string FullTypeName;
    public string Namespace;
    public string AssemblyName;
    public string AssemblyLocation;
    public int Count;
    public DateTime FirstSeen;
}

/// <summary>
/// Lightweight network diagnostics for multiplayer debugging.
///
/// STABILITY GUARANTEES:
/// - All operations wrapped in try-catch - cannot throw
/// - Token bucket throttling for errors (3 initial, +1 per 10s, max 3)
/// - Thread-safe ConcurrentDictionary for multiplayer safety
/// - Rate-limited file I/O (max once per second)
/// - Zero impact when uninitialized (early exit checks)
/// </summary>
public static class NetworkDiagnostics
{
    // Error throttling: token bucket rate limiter
    // - Max 3 tokens, regenerate 1 every 10 seconds
    // - Each error consumes 1 token; if no tokens, error is suppressed
    private static int _errorTokens = 3;
    private const int MaxErrorTokens = 3;
    private static DateTime _lastTokenRegen = DateTime.MinValue;
    private const double TokenRegenSeconds = 10.0;

    // Thread-safe packet tracking
    private static readonly ConcurrentDictionary<string, PacketInfo> _observedPackets =
        new ConcurrentDictionary<string, PacketInfo>();

    // Known ProxiCraft packet types (so we don't log our own)
    private static readonly HashSet<string> _knownPacketTypes = new HashSet<string>
    {
        "NetPackagePCLock",
        "NetPackagePCHandshake"
    };

    // Known conflicting mod packets - if we see these, warn about potential conflicts
    // Key: packet type name pattern, Value: mod name for warning
    private static readonly Dictionary<string, string> _conflictingPacketTypes = new Dictionary<string, string>
    {
        { "NetPackageBeyondStorage", "Beyond Storage 2" },
        { "BeyondStorage", "Beyond Storage 2" },
        { "NetPackageLockedTEs", "Beyond Storage 2" },
        { "CraftFromContainers", "CraftFromContainers" },
        { "NetPackageCFC", "CraftFromContainers" },
    };

    // Track which conflict warnings we've already shown (only warn once per mod)
    private static readonly HashSet<string> _warnedConflictMods = new HashSet<string>();

    // Log file path and state
    private static string _networkLogPath;
    private static bool _initialized;

    // Rate limiting for file writes - don't write more than once per second
    private static DateTime _lastWriteTime = DateTime.MinValue;
    private static readonly List<string> _pendingWrites = new List<string>();
    private static readonly object _writeLock = new object();

    /// <summary>
    /// Initialize the network diagnostics system.
    /// Safe to call multiple times - idempotent.
    /// </summary>
    public static void Init()
    {
        // Already initialized - no-op
        if (_initialized)
            return;

        try
        {
            string modPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _networkLogPath = Path.Combine(modPath, "network_log.txt");

            // Clear the log file for this session
            File.WriteAllText(_networkLogPath,
                $"=== ProxiCraft Network Log ===\n" +
                $"Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"This file is cleared each game session.\n\n");

            _observedPackets.Clear();
            _errorTokens = MaxErrorTokens;
            _lastTokenRegen = DateTime.MinValue;
            _initialized = true;
        }
        catch
        {
            // Failed to initialize - will remain uninitialized, all calls will early-exit
        }
    }

    /// <summary>
    /// Called when we observe a packet. Ultra-lightweight for hot path.
    /// </summary>
    public static void OnPacketObserved(NetPackage packet)
    {
        // Fast exit if not initialized
        if (!_initialized || packet == null)
            return;

        try
        {
            var type = packet.GetType();
            string typeName = type.Name;

            // Don't log our own packets - fast string check
            if (_knownPacketTypes.Contains(typeName))
                return;

            string fullTypeName = type.FullName ?? typeName;

            // Fast path: already seen this packet type
            if (_observedPackets.TryGetValue(fullTypeName, out var existingInfo))
            {
                // Just increment count - no allocation, no I/O
                Interlocked.Increment(ref existingInfo.Count);
                return;
            }

            // Slow path: first time seeing this packet - gather info and log
            RecordNewPacketType(type, typeName, fullTypeName);
        }
        catch
        {
            // Throttled error handling - consume a token if available
            ConsumeErrorToken();
        }
    }

    /// <summary>
    /// Token bucket for error throttling. Returns true if error should be logged.
    /// </summary>
    private static bool ConsumeErrorToken()
    {
        // Regenerate tokens based on elapsed time
        var now = DateTime.Now;
        if (_lastTokenRegen != DateTime.MinValue)
        {
            var elapsed = (now - _lastTokenRegen).TotalSeconds;
            var tokensToAdd = (int)(elapsed / TokenRegenSeconds);
            if (tokensToAdd > 0)
            {
                _errorTokens = Math.Min(MaxErrorTokens, _errorTokens + tokensToAdd);
                _lastTokenRegen = now;
            }
        }
        else
        {
            _lastTokenRegen = now;
        }

        // Try to consume a token
        if (_errorTokens > 0)
        {
            _errorTokens--;
            return true; // Error can be logged
        }

        return false; // Suppressed
    }

    /// <summary>
    /// Record a new packet type. Separated from hot path for clarity.
    /// </summary>
    private static void RecordNewPacketType(Type type, string typeName, string fullTypeName)
    {
        var assembly = type.Assembly;
        var info = new PacketInfo
        {
            TypeName = typeName,
            FullTypeName = fullTypeName,
            Namespace = type.Namespace,
            AssemblyName = assembly?.GetName()?.Name ?? "Unknown",
            AssemblyLocation = GetSafeAssemblyLocation(assembly),
            Count = 1,
            FirstSeen = DateTime.Now
        };

        // Thread-safe add - might fail if another thread added first, that's OK
        if (_observedPackets.TryAdd(fullTypeName, info))
        {
            // Queue the log message - don't block the hot path with I/O
            QueueLogMessage($"New packet: {typeName} | Assembly: {info.AssemblyName} | Namespace: {info.Namespace ?? "(none)"}");

            // Check for known conflicting mod packets
            CheckForConflictingPacket(typeName, fullTypeName);
        }
    }

    /// <summary>
    /// Check if a packet is from a known conflicting mod and warn once.
    /// </summary>
    private static void CheckForConflictingPacket(string typeName, string fullTypeName)
    {
        try
        {
            foreach (var conflict in _conflictingPacketTypes)
            {
                if (typeName.Contains(conflict.Key) || fullTypeName.Contains(conflict.Key))
                {
                    string modName = conflict.Value;

                    // Only warn once per mod
                    if (_warnedConflictMods.Contains(modName))
                        return;

                    _warnedConflictMods.Add(modName);

                    // Log to console and file - this is important context for crash diagnosis
                    ProxiCraft.LogWarning($"[Network Conflict] Detected '{modName}' packets from another player!");
                    ProxiCraft.LogWarning($"[Network Conflict] If crashes occur, this mod conflict may be the cause.");
                    QueueLogMessage($"CONFLICT WARNING: Detected {modName} packet ({typeName}) - potential multiplayer mod conflict!");

                    return;
                }
            }
        }
        catch
        {
            // Silent fail - conflict detection is best-effort
        }
    }

    /// <summary>
    /// Queue a log message for rate-limited writing.
    /// </summary>
    private static void QueueLogMessage(string message)
    {
        lock (_writeLock)
        {
            _pendingWrites.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

            // Rate limit: only write once per second max
            var now = DateTime.Now;
            if ((now - _lastWriteTime).TotalSeconds >= 1.0)
            {
                FlushPendingWrites();
                _lastWriteTime = now;
            }
        }
    }

    /// <summary>
    /// Flush any pending log messages to disk.
    /// </summary>
    private static void FlushPendingWrites()
    {
        if (_pendingWrites.Count == 0 || string.IsNullOrEmpty(_networkLogPath))
            return;

        try
        {
            string content = string.Join("\n", _pendingWrites) + "\n";
            _pendingWrites.Clear();
            File.AppendAllText(_networkLogPath, content);
        }
        catch
        {
            // I/O failed - just clear pending to prevent memory buildup
            _pendingWrites.Clear();
        }
    }

    /// <summary>
    /// Safely get assembly location.
    /// </summary>
    private static string GetSafeAssemblyLocation(System.Reflection.Assembly assembly)
    {
        if (assembly == null)
            return "Unknown";

        try
        {
            string location = assembly.Location;
            if (string.IsNullOrEmpty(location))
                return "(dynamic)";
            return Path.GetFileName(location);
        }
        catch
        {
            return "(unavailable)";
        }
    }

    /// <summary>
    /// Called when an exception occurs during packet processing.
    /// </summary>
    public static void OnPacketException(NetPackage packet, Exception ex)
    {
        if (!_initialized || packet == null || ex == null)
            return;

        try
        {
            var type = packet.GetType();
            QueueLogMessage($"EXCEPTION: {type.Name} | {ex.GetType().Name}: {ex.Message}");

            // Also log to main warning log since exceptions are important
            ProxiCraft.LogWarning($"[Network] Exception processing packet '{type.Name}': {ex.Message}");
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Clears packet tracking and flushes pending writes.
    /// </summary>
    public static void Clear()
    {
        try
        {
            _observedPackets.Clear();

            lock (_writeLock)
            {
                if (_initialized)
                {
                    _pendingWrites.Add($"[{DateTime.Now:HH:mm:ss}] --- Session cleared ---");
                    FlushPendingWrites();
                }
            }
        }
        catch
        {
            // Silent fail
        }
    }

    /// <summary>
    /// Check if diagnostics are active (for status reporting).
    /// </summary>
    public static bool IsActive => _initialized;
}

/// <summary>
/// Harmony patches to observe network packet processing.
/// 
/// Patches ConnectionManager.ProcessPackages which is where all network packets
/// are dispatched. This allows us to detect packets from conflicting mods.
///
/// STABILITY GUARANTEES:
/// - Prefix pattern: observes packets before processing, cannot break it
/// - All code wrapped in try-catch with empty catch
/// - Only active in multiplayer (ConnectionManager only used in MP)
/// </summary>
[HarmonyPatch(typeof(ConnectionManager), "ProcessPackages")]
public static class NetworkPacketObserver
{
    [HarmonyPrefix]
    public static void Prefix(List<NetPackage> ___packagesToProcess)
    {
        // Entire body wrapped - cannot throw
        try
        {
            // Only observe if we have packets and diagnostics is initialized
            if (___packagesToProcess == null || ___packagesToProcess.Count == 0)
                return;

            foreach (var packet in ___packagesToProcess)
            {
                if (packet != null)
                {
                    NetworkDiagnostics.OnPacketObserved(packet);
                }
            }
        }
        catch
        {
            // Swallow everything - this patch must never disrupt packet processing
        }
    }
}
