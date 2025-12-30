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
/// 
/// PERCENTILE TRACKING:
/// Tracks last N samples to calculate percentiles (P50, P90, P99, P99.9).
/// This helps identify spike patterns vs one-off spikes.
/// Example: If P99 is high but P90 is low, spikes are rare (1 in 100).
/// If P90 is also high, spikes happen frequently (1 in 10).
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

    // Sample buffer sizes
    private const int ALL_TIME_SAMPLES = 10000;  // Large buffer for "all time" percentiles
    private const int RECENT_SAMPLES = 1000;     // Smaller buffer for "recent" percentiles

    /// <summary>
    /// Tracks statistics for a single operation type.
    /// Maintains two sample buffers: all-time (10k) and recent (1k).
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
        
        // All-time sample buffer (large)
        private readonly double[] _allSamples = new double[ALL_TIME_SAMPLES];
        private int _allIndex = 0;
        private int _allCount = 0;
        
        // Recent sample buffer (small)
        private readonly double[] _recentSamples = new double[RECENT_SAMPLES];
        private int _recentIndex = 0;
        private int _recentCount = 0;
        
        public double AvgMs => CallCount > 0 ? TotalMs / CallCount : 0;
        public double HitRate => (CacheHits + CacheMisses) > 0 
            ? (double)CacheHits / (CacheHits + CacheMisses) * 100 
            : 0;
        
        /// <summary>
        /// Records a sample time to both buffers.
        /// </summary>
        public void RecordSample(double ms)
        {
            // All-time buffer
            _allSamples[_allIndex] = ms;
            _allIndex = (_allIndex + 1) % ALL_TIME_SAMPLES;
            if (_allCount < ALL_TIME_SAMPLES)
                _allCount++;
            
            // Recent buffer
            _recentSamples[_recentIndex] = ms;
            _recentIndex = (_recentIndex + 1) % RECENT_SAMPLES;
            if (_recentCount < RECENT_SAMPLES)
                _recentCount++;
        }
        
        // ===== ALL-TIME STATS (from large buffer) =====
        
        public int AllSampleCount => _allCount;
        
        public double AllAvgMs
        {
            get
            {
                if (_allCount == 0) return 0;
                double sum = 0;
                for (int i = 0; i < _allCount; i++)
                    sum += _allSamples[i];
                return sum / _allCount;
            }
        }
        
        public double AllMaxMs
        {
            get
            {
                if (_allCount == 0) return 0;
                double max = 0;
                for (int i = 0; i < _allCount; i++)
                    if (_allSamples[i] > max) max = _allSamples[i];
                return max;
            }
        }
        
        public double GetAllPercentile(double percentile)
        {
            return GetPercentileFromBuffer(_allSamples, _allCount, percentile);
        }
        
        // ===== RECENT STATS (from small buffer) =====
        
        public int RecentSampleCount => _recentCount;
        
        public double RecentAvgMs
        {
            get
            {
                if (_recentCount == 0) return 0;
                double sum = 0;
                for (int i = 0; i < _recentCount; i++)
                    sum += _recentSamples[i];
                return sum / _recentCount;
            }
        }
        
        public double RecentMaxMs
        {
            get
            {
                if (_recentCount == 0) return 0;
                double max = 0;
                for (int i = 0; i < _recentCount; i++)
                    if (_recentSamples[i] > max) max = _recentSamples[i];
                return max;
            }
        }
        
        public double GetRecentPercentile(double percentile)
        {
            return GetPercentileFromBuffer(_recentSamples, _recentCount, percentile);
        }
        
        // ===== HELPER =====
        
        private static double GetPercentileFromBuffer(double[] buffer, int count, double percentile)
        {
            if (count == 0)
                return 0;
            
            var samples = new double[count];
            Array.Copy(buffer, samples, count);
            Array.Sort(samples);
            
            double rank = (percentile / 100.0) * (count - 1);
            int lowerIndex = (int)Math.Floor(rank);
            int upperIndex = (int)Math.Ceiling(rank);
            
            if (lowerIndex == upperIndex)
                return samples[lowerIndex];
            
            double weight = rank - lowerIndex;
            return samples[lowerIndex] * (1 - weight) + samples[upperIndex] * weight;
        }
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
    
    // New operations for centralized systems
    public const string OP_IS_IN_RANGE = "IsInRange";
    public const string OP_GET_STORAGE_ITEMS = "GetStorageItems";
    public const string OP_CLEANUP_STALE = "CleanupStale";
    public const string OP_PREWARM_CACHE = "PreWarmCache";
    public const string OP_SCAN_ENTITIES = "ScanEntities";
    public const string OP_SCAN_TILE_ENTITIES = "ScanTileEntities";

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
            stats.RecordSample(ms); // Track for percentile calculations
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
    /// Simple text format that pastes well to forums/comments.
    /// Shows both all-time stats and recent (last 1000) stats per operation.
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
            sb.AppendLine("=== ProxiCraft Performance Report ===");
            sb.AppendLine();
            
            // Sort by total time descending (most expensive first)
            var sortedStats = _stats.Values.OrderByDescending(s => s.TotalMs).ToList();

            // Main stats table - simple text format
            sb.AppendLine("Operation                | Calls  | Avg    | P50    | P90    | P99    | Max    | Cache");
            sb.AppendLine("-------------------------|--------|--------|--------|--------|--------|--------|-------");

            foreach (var stat in sortedStats)
            {
                string name = stat.Name.Length > 24 ? stat.Name.Substring(0, 21) + "..." : stat.Name;
                string cacheInfo = (stat.CacheHits + stat.CacheMisses) > 0 
                    ? $"{stat.HitRate:F0}%" 
                    : "-";

                // ALL TIME row (from 10k sample buffer)
                if (stat.AllSampleCount > 0)
                {
                    double allP50 = stat.GetAllPercentile(50);
                    double allP90 = stat.GetAllPercentile(90);
                    double allP99 = stat.GetAllPercentile(99);
                    sb.AppendLine($"{name,-24} | {stat.CallCount,6} | {FormatMs(stat.AllAvgMs),6} | {FormatMs(allP50),6} | {FormatMs(allP90),6} | {FormatMs(allP99),6} | {FormatMs(stat.AllMaxMs),6} | {cacheInfo,5}");
                }
                else
                {
                    sb.AppendLine($"{name,-24} | {stat.CallCount,6} | {FormatMs(stat.AvgMs),6} | {"-",6} | {"-",6} | {"-",6} | {FormatMs(stat.MaxMs),6} | {cacheInfo,5}");
                }
                
                // RECENT row (from 1k sample buffer)
                if (stat.RecentSampleCount > 0)
                {
                    double recentP50 = stat.GetRecentPercentile(50);
                    double recentP90 = stat.GetRecentPercentile(90);
                    double recentP99 = stat.GetRecentPercentile(99);
                    
                    string recentLabel = $"  (last {stat.RecentSampleCount})";
                    sb.AppendLine($"{recentLabel,-24} | {stat.RecentSampleCount,6} | {FormatMs(stat.RecentAvgMs),6} | {FormatMs(recentP50),6} | {FormatMs(recentP90),6} | {FormatMs(recentP99),6} | {FormatMs(stat.RecentMaxMs),6} | {"-",5}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Times in milliseconds. First row = all time (10k samples), indented = recent (1k samples).");
            sb.AppendLine("P50=median, P90=90th percentile, P99=99th percentile.");
            
            // Quick interpretation
            sb.AppendLine();
            sb.AppendLine("=== Analysis ===");
            
            var cacheStats = _stats.TryGetValue(OP_GET_ITEM_COUNT, out var gs) ? gs : null;
            var rebuildStats = _stats.TryGetValue(OP_REBUILD_CACHE, out var rs) ? rs : null;
            
            if (cacheStats != null && (cacheStats.CacheHits + cacheStats.CacheMisses) > 0)
            {
                string cacheStatus = cacheStats.HitRate >= 90 ? "excellent" : 
                                     cacheStats.HitRate >= 70 ? "good" : "low";
                sb.AppendLine($"Cache hit rate: {cacheStats.HitRate:F1}% ({cacheStatus})");
            }

            // Spike detection - only report notable issues
            bool hasSpikes = false;
            foreach (var stat in sortedStats.Where(s => s.AllSampleCount >= 10))
            {
                double p90 = stat.GetAllPercentile(90);
                double p99 = stat.GetAllPercentile(99);
                
                if (stat.AllMaxMs > p99 * 3 && stat.AllMaxMs > 20)
                {
                    sb.AppendLine($"* {stat.Name}: Rare spike detected (max {stat.AllMaxMs:F0}ms vs P99 {p99:F1}ms)");
                    hasSpikes = true;
                }
                else if (p99 > p90 * 2.5 && p99 > 10)
                {
                    sb.AppendLine($"* {stat.Name}: Occasional spikes (~1% of calls hit {p99:F1}ms)");
                    hasSpikes = true;
                }
                else if (p90 > stat.AllAvgMs * 3 && p90 > 10)
                {
                    sb.AppendLine($"* {stat.Name}: Frequent variance (~10% of calls hit {p90:F1}ms)");
                    hasSpikes = true;
                }
            }

            if (!hasSpikes)
            {
                sb.AppendLine("No significant spikes detected.");
            }

            // Simple recommendations
            sb.AppendLine();
            if (rebuildStats?.AvgMs > 30)
            {
                sb.AppendLine("Tip: Reduce 'range' in config.json for better performance.");
            }
            else if (rebuildStats?.GetAllPercentile(99) > 50)
            {
                sb.AppendLine("Tip: Occasional spikes are normal when loading new chunks.");
            }
            else
            {
                sb.AppendLine("Performance looks good!");
            }

            return sb.ToString();
        }
    }
    
    /// <summary>
    /// Formats milliseconds for display - compact format.
    /// </summary>
    private static string FormatMs(double ms)
    {
        if (ms < 0.01) return "0";
        if (ms < 1) return $"{ms:F2}";
        if (ms < 10) return $"{ms:F1}";
        return $"{ms:F0}";
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
