using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ProxiCraft;

/// <summary>
/// Simple performance profiler for ProxiCraft operations.
/// 
/// Tracks timing and call counts for key operations to help identify
/// performance bottlenecks on lower-end systems.
/// 
/// Usage:
/// - Call StartTimer("operation") before an operation
/// - Call StopTimer("operation") after it completes
/// - Use GetReport() to see statistics
/// - Use "pc perf" console command to view report in-game
/// 
/// Performance data is only collected when profiling is enabled.
/// Enable via "pc perf on" or config setting.
/// </summary>
public static class PerformanceProfiler
{
    /// <summary>
    /// Whether profiling is currently enabled.
    /// Disabled by default to avoid any overhead in normal gameplay.
    /// </summary>
    public static bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Whether any profiling data has been collected.
    /// </summary>
    public static bool HasData
    {
        get
        {
            lock (_lock)
            {
                return _stats.Count > 0;
            }
        }
    }

    /// <summary>
    /// Tracks statistics for a single operation type
    /// </summary>
    public class OperationStats
    {
        public string Name { get; set; }
        public int CallCount { get; set; }
        public double TotalMs { get; set; }
        public double MinMs { get; set; } = double.MaxValue;
        public double MaxMs { get; set; } = double.MinValue;
        public double LastMs { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        
        public double AvgMs => CallCount > 0 ? TotalMs / CallCount : 0;
        public double HitRate => (CacheHits + CacheMisses) > 0 
            ? (double)CacheHits / (CacheHits + CacheMisses) * 100 
            : 0;
    }

    // Operation statistics storage
    private static readonly Dictionary<string, OperationStats> _stats = new();
    
    // Active timers (for nested timing support)
    private static readonly Dictionary<string, Stopwatch> _activeTimers = new();
    
    // Lock for thread safety
    private static readonly object _lock = new();

    // Predefined operation names for consistency
    public const string OP_REBUILD_CACHE = "RebuildItemCountCache";
    public const string OP_GET_ITEM_COUNT = "GetItemCount";
    public const string OP_REFRESH_STORAGES = "RefreshStorages";
    public const string OP_COUNT_VEHICLES = "CountVehicles";
    public const string OP_COUNT_DRONES = "CountDrones";
    public const string OP_COUNT_DEWCOLLECTORS = "CountDewCollectors";
    public const string OP_COUNT_WORKSTATIONS = "CountWorkstations";
    public const string OP_COUNT_CONTAINERS = "CountContainers";
    public const string OP_REMOVE_ITEMS = "RemoveItems";
    public const string OP_CHUNK_SCAN = "ChunkScan";

    /// <summary>
    /// Starts timing an operation. Call StopTimer with same name to record.
    /// </summary>
    public static void StartTimer(string operationName)
    {
        if (!IsEnabled) return;

        lock (_lock)
        {
            if (!_activeTimers.ContainsKey(operationName))
            {
                _activeTimers[operationName] = new Stopwatch();
            }
            _activeTimers[operationName].Restart();
        }
    }

    /// <summary>
    /// Stops timing an operation and records the duration.
    /// </summary>
    public static void StopTimer(string operationName)
    {
        if (!IsEnabled) return;

        lock (_lock)
        {
            if (!_activeTimers.TryGetValue(operationName, out var timer))
                return;

            timer.Stop();
            double ms = timer.Elapsed.TotalMilliseconds;

            if (!_stats.TryGetValue(operationName, out var stats))
            {
                stats = new OperationStats { Name = operationName };
                _stats[operationName] = stats;
            }

            stats.CallCount++;
            stats.TotalMs += ms;
            stats.LastMs = ms;
            if (ms < stats.MinMs) stats.MinMs = ms;
            if (ms > stats.MaxMs) stats.MaxMs = ms;
        }
    }

    /// <summary>
    /// Records a cache hit for an operation.
    /// </summary>
    public static void RecordCacheHit(string operationName)
    {
        if (!IsEnabled) return;

        lock (_lock)
        {
            if (!_stats.TryGetValue(operationName, out var stats))
            {
                stats = new OperationStats { Name = operationName };
                _stats[operationName] = stats;
            }
            stats.CacheHits++;
        }
    }

    /// <summary>
    /// Records a cache miss for an operation.
    /// </summary>
    public static void RecordCacheMiss(string operationName)
    {
        if (!IsEnabled) return;

        lock (_lock)
        {
            if (!_stats.TryGetValue(operationName, out var stats))
            {
                stats = new OperationStats { Name = operationName };
                _stats[operationName] = stats;
            }
            stats.CacheMisses++;
        }
    }

    /// <summary>
    /// Clears all recorded statistics.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _stats.Clear();
            _activeTimers.Clear();
        }
    }

    /// <summary>
    /// Gets a formatted performance report.
    /// </summary>
    public static string GetReport()
    {
        lock (_lock)
        {
            if (_stats.Count == 0)
            {
                return "No performance data collected.\nEnable profiling with 'pc perf on' and play for a bit.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║              ProxiCraft Performance Report                       ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║ Operation              │ Calls │ Avg(ms) │ Max(ms) │ Cache Hit % ║");
            sb.AppendLine("╟────────────────────────┼───────┼─────────┼─────────┼─────────────╢");

            // Sort by total time descending (most expensive first)
            var sortedStats = _stats.Values.OrderByDescending(s => s.TotalMs);

            foreach (var stat in sortedStats)
            {
                string name = stat.Name.Length > 22 ? stat.Name.Substring(0, 19) + "..." : stat.Name;
                string cacheInfo = (stat.CacheHits + stat.CacheMisses) > 0 
                    ? $"{stat.HitRate:F1}%" 
                    : "N/A";

                sb.AppendLine($"║ {name,-22} │ {stat.CallCount,5} │ {stat.AvgMs,7:F2} │ {stat.MaxMs,7:F2} │ {cacheInfo,11} ║");
            }

            sb.AppendLine("╚══════════════════════════════════════════════════════════════════╝");

            // Performance assessment
            sb.AppendLine();
            sb.AppendLine("Performance Assessment:");
            
            var cacheStats = _stats.TryGetValue(OP_GET_ITEM_COUNT, out var gs) ? gs : null;
            if (cacheStats != null && (cacheStats.CacheHits + cacheStats.CacheMisses) > 0)
            {
                sb.AppendLine($"  Cache efficiency: {cacheStats.HitRate:F1}% hit rate");
                if (cacheStats.HitRate < 50)
                    sb.AppendLine("  ⚠ Low cache hit rate - cache may be invalidating too often");
                else if (cacheStats.HitRate > 90)
                    sb.AppendLine("  ✓ Excellent cache performance");
            }

            var rebuildStats = _stats.TryGetValue(OP_REBUILD_CACHE, out var rs) ? rs : null;
            if (rebuildStats != null)
            {
                if (rebuildStats.AvgMs > 50)
                    sb.AppendLine($"  ⚠ Cache rebuild averaging {rebuildStats.AvgMs:F1}ms - consider reducing range");
                else if (rebuildStats.AvgMs > 20)
                    sb.AppendLine($"  Cache rebuild: {rebuildStats.AvgMs:F1}ms average (acceptable)");
                else
                    sb.AppendLine($"  ✓ Cache rebuild fast: {rebuildStats.AvgMs:F1}ms average");

                if (rebuildStats.MaxMs > 100)
                    sb.AppendLine($"  ⚠ Spike detected: {rebuildStats.MaxMs:F1}ms max rebuild time");
            }

            // Recommendations
            sb.AppendLine();
            sb.AppendLine("Recommendations:");
            bool hasRecommendations = false;

            if (rebuildStats?.AvgMs > 30)
            {
                sb.AppendLine("  • Reduce 'range' in config.json (try 10-15 blocks)");
                hasRecommendations = true;
            }

            if (cacheStats?.HitRate < 70)
            {
                sb.AppendLine("  • Cache is rebuilding frequently - this is normal during active gameplay");
                hasRecommendations = true;
            }

            if (!hasRecommendations)
            {
                sb.AppendLine("  ✓ Performance looks good! No changes needed.");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Gets a brief one-line status for the pc status command.
    /// </summary>
    public static string GetBriefStatus()
    {
        if (!IsEnabled)
            return "Profiling: Disabled (use 'pc perf on' to enable)";

        lock (_lock)
        {
            if (_stats.Count == 0)
                return "Profiling: Enabled, no data yet";

            var cacheStats = _stats.TryGetValue(OP_GET_ITEM_COUNT, out var gs) ? gs : null;
            var rebuildStats = _stats.TryGetValue(OP_REBUILD_CACHE, out var rs) ? rs : null;

            var parts = new List<string> { "Profiling: ON" };
            
            if (rebuildStats != null)
                parts.Add($"Rebuild: {rebuildStats.AvgMs:F1}ms avg");
            
            if (cacheStats != null && (cacheStats.CacheHits + cacheStats.CacheMisses) > 0)
                parts.Add($"Cache: {cacheStats.HitRate:F0}% hits");

            return string.Join(", ", parts);
        }
    }
}
