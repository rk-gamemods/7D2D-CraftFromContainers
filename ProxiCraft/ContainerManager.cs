using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Platform;
using UnityEngine;

namespace ProxiCraft;

/// <summary>
/// Wrapper for entity-based storage (vehicles, drones) that tracks the entity reference
/// for live position checks. Unlike tile entities, entities can move.
/// </summary>
public class EntityStorage
{
    public Entity Entity { get; }
    public Bag Bag { get; }
    public ITileEntityLootable LootContainer { get; }
    public StorageType Type { get; }
    
    public EntityStorage(Entity entity, Bag bag, StorageType type)
    {
        Entity = entity;
        Bag = bag;
        LootContainer = null;
        Type = type;
    }
    
    public EntityStorage(Entity entity, ITileEntityLootable lootContainer, StorageType type)
    {
        Entity = entity;
        Bag = null;
        LootContainer = lootContainer;
        Type = type;
    }
    
    /// <summary>
    /// Gets the current world position of this entity's storage.
    /// Returns entity's live position since entities can move.
    /// </summary>
    public Vector3 GetCurrentPosition()
    {
        return Entity?.position ?? Vector3.zero;
    }
    
    /// <summary>
    /// Checks if the entity is still valid (not despawned/removed).
    /// </summary>
    public bool IsValid()
    {
        return Entity != null && !Entity.IsDead();
    }
}

/// <summary>
/// Manages container storage discovery and item operations for the ProxiCraft mod.
///
/// RESPONSIBILITIES:
/// - Discovering nearby storage containers (TileEntityComposite, TileEntitySecureLootContainer)
/// - Discovering nearby vehicles, drones, dew collectors, and workstation outputs
/// - Counting items across all accessible storage sources
/// - Removing items from storage when crafting
/// - Caching storage references for performance
/// - Tracking the currently open container for live item counting
///
/// STORAGE SOURCES:
/// - Player storage containers (TileEntityComposite with TEFeatureStorage)
/// - Legacy secure loot containers (TileEntitySecureLootContainer)
/// - Vehicle storage (EntityVehicle.bag)
/// - Drone storage (EntityDrone.bag or lootContainer)
/// - Dew collectors (TileEntityCollector.items)
/// - Workstation outputs (TileEntityWorkstation.output)
///
/// CONTAINER COUNTING STRATEGY:
/// Open containers require special handling because their UI data may differ from TileEntity data.
/// When a container is open:
/// - CurrentOpenContainer holds a live reference set by XUiC_LootContainer.OnOpen patch
/// - GetOpenContainerItemCount() reads from this live reference
/// - GetItemCount() skips the open container's TileEntity to avoid double-counting
///
/// Closed containers are counted directly from their TileEntity storage.
///
/// MULTIPLAYER SUPPORT:
/// - LockedList tracks containers locked by other players
/// - NetPackagePCLock syncs lock state across clients
/// - Containers in LockedList are excluded from operations
///
/// PERFORMANCE:
/// - Uses scan cooldown (SCAN_COOLDOWN) to limit rescans
/// - Caches container references between scans
/// - Caches item counts to avoid repeated entity iteration
/// - Force refresh flag for immediate recache when needed
/// </summary>
public static class ContainerManager
{
    // Thread-safe caches for storage references
    private static readonly Dictionary<Vector3i, object> _knownStorageDict = new Dictionary<Vector3i, object>();
    private static readonly Dictionary<Vector3i, object> _currentStorageDict = new Dictionary<Vector3i, object>();
    
    // Lock positions from multiplayer sync - with timestamps for expiration and ordering
    public static readonly HashSet<Vector3i> LockedList = new HashSet<Vector3i>();
    private static readonly Dictionary<Vector3i, long> _lockTimestamps = new Dictionary<Vector3i, long>();
    private static readonly Dictionary<Vector3i, long> _lockPacketTimestamps = new Dictionary<Vector3i, long>(); // For ordering
    
    // Cache timing to avoid excessive scanning
    private static float _lastScanTime;
    private static Vector3 _lastScanPosition;
    private const float SCAN_COOLDOWN = 0.1f; // Don't rescan more than 10 times per second
    private const float POSITION_CHANGE_THRESHOLD = 1f; // Rescan if player moved more than 1 unit
    
    // Cleanup timing for unlimited range mode (remove destroyed containers)
    private static float _lastCleanupTime;
    private const float CLEANUP_INTERVAL = 10f; // Clean up stale refs every 10 seconds
    
    // Flag to force cache refresh (set when containers change)
    private static bool _forceCacheRefresh = true;
    
    // Flag to force storage refresh after crash prevention catches an error
    // This clears stale references that may have caused the error
    private static bool _storageRefreshNeeded = false;
    
    // ====================================================================================
    // ITEM COUNT CACHE - PERFORMANCE OPTIMIZATION
    // ====================================================================================
    // Caches item counts per item type to avoid repeated iteration over all entities.
    // 
    // CACHING STRATEGY:
    // - Cache is built on first request in a frame, reused for subsequent requests
    // - Cache auto-expires after a short duration to ensure freshness
    // - Cache is invalidated immediately when:
    //   a) Container is opened/closed (InvalidateCache called)
    //   b) Items are moved in/out of storage (InvalidateCache called)
    //   c) Player position changes significantly
    //
    // WHY THIS IS SAFE:
    // - For crafting: Multiple ingredients checked in same frame use same cache = fast
    // - For challenges: Events fire when player inventory changes, at which point
    //   we scan fresh because enough time has passed or cache was invalidated
    // - Cache duration is short enough (100ms) that any manual container changes
    //   by the player will be reflected almost immediately
    //
    // PERFORMANCE:
    // - Single scan of 100+ storage locations: ~5-20ms (acceptable)
    // - Checking 20 recipe ingredients with cache: ~0.1ms (vs 100-400ms without)
    // - Net result: Smooth UI, no lag spikes
    // ====================================================================================
    private static readonly Dictionary<int, int> _itemCountCache = new Dictionary<int, int>();
    private static float _lastItemCountTime;
    private const float ITEM_COUNT_CACHE_DURATION = 0.1f; // Cache counts for 100ms (reduced from 250ms for responsiveness)
    private static bool _itemCountCacheValid = false;
    private static int _lastItemCountFrame = -1; // Track frame number for per-frame caching
    
    // ====================================================================================
    // LIVE OPEN CONTAINER REFERENCE
    // ====================================================================================
    // These are set by LootContainer_OnOpen_Patch and cleared by LootContainer_OnClose_Patch.
    // This gives us direct access to the container's items during UI operations, which is
    // essential for accurate counting when items are being moved in real-time.
    // ====================================================================================
    public static ITileEntityLootable CurrentOpenContainer { get; set; }
    public static Vector3i CurrentOpenContainerPos { get; set; }
    
    // ====================================================================================
    // LIVE OPEN VEHICLE REFERENCE  
    // ====================================================================================
    // Set by VehicleStorageWindowGroup_OnOpen_Patch, cleared by OnClose.
    // When vehicle storage is open, we need to skip counting it separately to avoid
    // double-counting or stale data issues.
    // ====================================================================================
    public static EntityVehicle CurrentOpenVehicle { get; set; }
    
    // ====================================================================================
    // LIVE OPEN DRONE REFERENCE  
    // ====================================================================================
    // Drones use XUiC_LootContainer which sets CurrentOpenContainer. But we also need
    // to track the drone entity itself to skip it in CountAllDroneItems.
    // NOTE: drone.lootContainer.items and drone.bag.GetSlots() share the SAME array!
    // ====================================================================================
    public static EntityDrone CurrentOpenDrone { get; set; }
    
    // ====================================================================================
    // LIVE OPEN WORKSTATION REFERENCE  
    // ====================================================================================
    // Set by WorkstationWindowGroup_OnOpen_Patch, cleared by OnClose.
    // When a workstation is open, we need to count from its live UI data and skip
    // counting it separately in CountAllWorkstationOutputItems.
    // ====================================================================================
    public static TileEntityWorkstation CurrentOpenWorkstation { get; set; }
    
    /// <summary>
    /// Forces the next container scan to refresh the cache.
    /// Call this when containers may have changed contents.
    /// </summary>
    public static void InvalidateCache()
    {
        _forceCacheRefresh = true;
        _itemCountCacheValid = false;
    }

    /// <summary>
    /// Triggers a UI refresh for the open workstation's output grid.
    /// Called after removing items from CurrentOpenWorkstation.Output.
    /// 
    /// This may be redundant with vanilla's natural refresh cycle, but ensures
    /// the UI updates immediately. If profiling shows this as a performance issue,
    /// we can remove this call - vanilla's UpdateBackend fires on craft completion anyway.
    /// </summary>
    private static void TriggerWorkstationOutputRefresh()
    {
        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return;
            
            var xui = LocalPlayerUI.GetUIForPlayer(player)?.xui;
            if (xui == null) return;
            
            // Use the convenient currentWorkstationOutputGrid property
            var outputGrid = xui.currentWorkstationOutputGrid;
            if (outputGrid != null)
            {
                outputGrid.IsDirty = true;
                ProxiCraft.LogDebug("Marked workstation output grid as dirty");
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogWarning($"Error triggering workstation output refresh: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the workstation output grid UI if a workstation is currently open.
    /// This is needed because modifying the TileEntity directly doesn't work - 
    /// the UI has its own copy of the slots that gets synced back to TileEntity on close.
    /// </summary>
    private static XUiC_WorkstationOutputGrid GetCurrentWorkstationOutputGrid()
    {
        try
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return null;
            
            var xui = LocalPlayerUI.GetUIForPlayer(player)?.xui;
            return xui?.currentWorkstationOutputGrid;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes items from the workstation output UI slots directly.
    /// Returns the number of items removed.
    /// 
    /// CRITICAL: We MUST modify the UI slots, not the TileEntity.Output directly!
    /// When the workstation is open, the UI has its own copy of the slots.
    /// On close, syncTEfromUI() copies UI slots back to TileEntity, overwriting any
    /// direct TileEntity modifications we made.
    /// </summary>
    private static int RemoveFromWorkstationOutputUI(ItemValue item, int count, out bool handled)
    {
        handled = false;
        int removed = 0;
        
        try
        {
            var outputGrid = GetCurrentWorkstationOutputGrid();
            if (outputGrid == null) return 0;
            
            var itemControllers = outputGrid.GetItemStackControllers();
            if (itemControllers == null) return 0;
            
            int remaining = count;
            
            for (int i = 0; i < itemControllers.Length && remaining > 0; i++)
            {
                var controller = itemControllers[i];
                if (controller == null) continue;
                
                var stack = controller.ItemStack;
                if (stack == null || stack.IsEmpty()) continue;
                if (stack.itemValue?.type != item.type) continue;
                
                int toRemove = Math.Min(remaining, stack.count);
                int countBefore = stack.count;
                
                ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName()} from workstation output UI slot {i} (count before: {countBefore})");
                
                if (stack.count <= toRemove)
                {
                    // Clear the slot completely
                    controller.ItemStack = ItemStack.Empty.Clone();
                    ProxiCraft.LogDebug($"  UI Slot {i} cleared");
                }
                else
                {
                    // Reduce the count - need to create new ItemStack since count might be readonly
                    var newStack = new ItemStack(stack.itemValue.Clone(), stack.count - toRemove);
                    controller.ItemStack = newStack;
                    ProxiCraft.LogDebug($"  UI Slot {i} count: {countBefore} -> {newStack.count}");
                }
                
                remaining -= toRemove;
                removed += toRemove;
            }
            
            if (removed > 0)
            {
                // Mark the grid as dirty to trigger UI refresh
                outputGrid.IsDirty = true;
                handled = true;
                ProxiCraft.LogDebug($"Removed {removed} items from workstation output UI");
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogWarning($"Error removing from workstation output UI: {ex.Message}");
        }
        
        return removed;
    }

    /// <summary>
    /// Clears all cached storage references. Call when starting a new game.
    /// </summary>
    public static void ClearCache()
    {
        _knownStorageDict.Clear();
        _currentStorageDict.Clear();
        _itemCountCache.Clear();
        LockedList.Clear();
        _lockTimestamps.Clear();
        _lockPacketTimestamps.Clear();
        _lastScanTime = 0f;
        _lastScanPosition = Vector3.zero;
        _forceCacheRefresh = true;
        _itemCountCacheValid = false;
        _lastItemCountFrame = -1;
        CurrentOpenContainer = null;
        CurrentOpenContainerPos = Vector3i.zero;
        CurrentOpenVehicle = null;
        CurrentOpenDrone = null;
        CurrentOpenWorkstation = null;
        // Reset scan method decision to force re-evaluation on next scan
        _scanMethodCalculated = false;
    }

    // ====================================================================================
    // CONTAINER LOCK MANAGEMENT - Multiplayer coordination
    // ====================================================================================
    // Tracks which containers are locked by other players to prevent conflicts.
    // Features:
    // - Last-write-wins ordering using packet timestamps (handles out-of-order packets)
    // - Lock expiration (handles ghost locks from crashed clients)
    // - Lazy expiration check (no background threads)
    // ====================================================================================

    /// <summary>
    /// Adds a container lock. Uses timestamps for last-write-wins ordering.
    /// Called from NetPackagePCLock.ProcessPackage() when lock packet received.
    /// </summary>
    /// <param name="position">World position of the container</param>
    /// <param name="packetTimestamp">UTC ticks from the packet (for ordering)</param>
    public static void AddLock(Vector3i position, long packetTimestamp)
    {
        // Last-write-wins: Only apply if this packet is newer than the last one for this position
        if (_lockPacketTimestamps.TryGetValue(position, out var lastTimestamp) && packetTimestamp < lastTimestamp)
        {
            ProxiCraft.LogDebug($"[Network] Ignoring stale LOCK packet for {position} (packet={packetTimestamp}, last={lastTimestamp})");
            return;
        }

        _lockPacketTimestamps[position] = packetTimestamp;
        _lockTimestamps[position] = DateTime.UtcNow.Ticks;
        LockedList.Add(position);
        ProxiCraft.LogDebug($"[Network] Container locked at {position}");
    }

    /// <summary>
    /// Removes a container lock. Uses timestamps for last-write-wins ordering.
    /// Called from NetPackagePCLock.ProcessPackage() when unlock packet received.
    /// </summary>
    /// <param name="position">World position of the container</param>
    /// <param name="packetTimestamp">UTC ticks from the packet (for ordering)</param>
    public static void RemoveLock(Vector3i position, long packetTimestamp)
    {
        // Last-write-wins: Only apply if this packet is newer or equal (unlock wins ties)
        if (_lockPacketTimestamps.TryGetValue(position, out var lastTimestamp) && packetTimestamp < lastTimestamp)
        {
            ProxiCraft.LogDebug($"[Network] Ignoring stale UNLOCK packet for {position} (packet={packetTimestamp}, last={lastTimestamp})");
            return;
        }

        _lockPacketTimestamps[position] = packetTimestamp;
        _lockTimestamps.Remove(position);
        LockedList.Remove(position);
        ProxiCraft.LogDebug($"[Network] Container unlocked at {position}");
    }

    /// <summary>
    /// Checks if a container is locked by another player.
    /// Includes lazy expiration check - expired locks are auto-removed.
    /// </summary>
    /// <param name="position">World position to check</param>
    /// <returns>True if container is locked (and not expired)</returns>
    public static bool IsContainerLocked(Vector3i position)
    {
        if (!LockedList.Contains(position))
            return false;

        // Check expiration
        var expirySeconds = ProxiCraft.Config?.containerLockExpirySeconds ?? 300f;
        if (expirySeconds > 0 && _lockTimestamps.TryGetValue(position, out var lockTicks))
        {
            var lockTime = new DateTime(lockTicks, DateTimeKind.Utc);
            var elapsed = (DateTime.UtcNow - lockTime).TotalSeconds;
            
            if (elapsed > expirySeconds)
            {
                // Lock expired - auto-remove
                LockedList.Remove(position);
                _lockTimestamps.Remove(position);
                _lockPacketTimestamps.Remove(position);
                ProxiCraft.Log($"[Network] Lock expired for container at {position} (after {elapsed:F0}s)");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets diagnostic info about currently locked containers.
    /// </summary>
    public static string GetLockDiagnostics()
    {
        if (LockedList.Count == 0)
            return "No containers locked";

        var lines = new List<string> { $"Locked containers: {LockedList.Count}" };
        var expirySeconds = ProxiCraft.Config?.containerLockExpirySeconds ?? 300f;
        
        foreach (var pos in LockedList.ToList()) // ToList to avoid modification during iteration
        {
            if (_lockTimestamps.TryGetValue(pos, out var lockTicks))
            {
                var lockTime = new DateTime(lockTicks, DateTimeKind.Utc);
                var elapsed = (DateTime.UtcNow - lockTime).TotalSeconds;
                var remaining = expirySeconds > 0 ? expirySeconds - elapsed : -1;
                lines.Add($"  {pos}: locked {elapsed:F0}s ago" + 
                         (remaining > 0 ? $" (expires in {remaining:F0}s)" : " (no expiry)"));
            }
            else
            {
                lines.Add($"  {pos}: no timestamp (legacy lock)");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Gets diagnostic info about the current entity scan method.
    /// </summary>
    public static string GetScanMethodDiagnostics(ModConfig config)
    {
        var world = GameManager.Instance?.World;
        if (world == null)
            return "World not loaded";
        
        int entityCount = world.Entities?.list?.Count ?? 0;
        float range = config.range;
        
        string method;
        string reason;
        
        if (range <= 0f)
        {
            method = "Entity Iteration";
            reason = "Unlimited range requires full entity scan";
        }
        else if (range <= SPATIAL_ALWAYS_BETTER_RANGE)
        {
            method = "Spatial Query";
            reason = $"Range ≤{SPATIAL_ALWAYS_BETTER_RANGE} always uses spatial";
        }
        else
        {
            float chunksPerSide = range / 8f;
            float chunkLookupCost = chunksPerSide * chunksPerSide;
            float entityCostThreshold = entityCount * 2f;
            
            bool useSpatial = chunkLookupCost < entityCostThreshold;
            method = useSpatial ? "Spatial Query" : "Entity Iteration";
            reason = $"ChunkLookups={chunkLookupCost:F0} vs EntityCost={entityCostThreshold:F0}";
        }
        
        return $"Method: {method} | Range: {(range <= 0 ? "Unlimited" : range.ToString("F0"))} | " +
               $"Entities: {entityCount} | {reason}";
    }

    /// <summary>
    /// Forces re-evaluation of the scan method. Call after config reload.
    /// </summary>
    public static void ResetScanMethodCache()
    {
        _scanMethodCalculated = false;
    }

    // ====================================================================================
    // CENTRALIZED RANGE CHECK
    // ====================================================================================
    // All container access should go through this function to check if within range.
    // Uses current player position (not cached) since vehicles/drones/player all move.
    // ====================================================================================
    
    /// <summary>
    /// Checks if a world position is within the configured range of the player.
    /// Uses current player position for accuracy with moving entities.
    /// </summary>
    /// <param name="config">Mod configuration</param>
    /// <param name="worldPos">Position to check (can be Vector3 or Vector3i)</param>
    /// <returns>True if within range, or if range is unlimited (-1 or 0)</returns>
    public static bool IsInRange(ModConfig config, Vector3 worldPos)
    {
        // Unlimited range
        if (config.range <= 0f)
            return true;
        
        var player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null)
            return false;
        
        // Use squared distance to avoid sqrt
        float dx = player.position.x - worldPos.x;
        float dy = player.position.y - worldPos.y;
        float dz = player.position.z - worldPos.z;
        float distSquared = dx * dx + dy * dy + dz * dz;
        float rangeSquared = config.range * config.range;
        
        return distSquared <= rangeSquared;
    }
    
    /// <summary>
    /// Checks if a world position is within the configured range of the player.
    /// Overload for Vector3i positions.
    /// </summary>
    public static bool IsInRange(ModConfig config, Vector3i worldPos)
    {
        return IsInRange(config, new Vector3(worldPos.x, worldPos.y, worldPos.z));
    }

    /// <summary>
    /// Pre-warms the container cache by scanning for nearby storage.
    /// Called on player spawn to eliminate first-access lag.
    /// </summary>
    public static void PreWarmCache(ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_PREWARM_CACHE);
        try
        {
            if (!config.modEnabled)
                return;
            
            // Force a full cache refresh
            _forceCacheRefresh = true;
            RefreshStorages(config);
            
            ProxiCraft.LogDebug($"Cache pre-warmed: {_currentStorageDict.Count} containers found");
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_PREWARM_CACHE);
        }
    }

    /// <summary>
    /// Gets items from all accessible storage containers.
    /// Used by crafting UI to determine available materials.
    /// Respects locked slots when config.respectLockedSlots is true.
    /// </summary>
    public static List<ItemStack> GetStorageItems(ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_GET_STORAGE_ITEMS);
        var items = new List<ItemStack>();

        if (!config.modEnabled)
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_STORAGE_ITEMS);
            return items;
        }

        try
        {
            RefreshStorages(config);

            foreach (var kvp in _currentStorageDict)
            {
                try
                {
                    // Determine actual position for range check
                    // For EntityStorage (vehicles/drones), use their current live position
                    // For StorageSourceInfo (workstation outputs, dew collectors), use the TileEntity position
                    //   (dict key has y+10000 offset to avoid collision, so we can't use it for range check)
                    // For tile entities, use the dict key (fixed position)
                    Vector3 containerPos;
                    if (kvp.Value is EntityStorage entityStorage)
                    {
                        if (!entityStorage.IsValid())
                            continue; // Entity was despawned
                        containerPos = entityStorage.GetCurrentPosition();
                    }
                    else if (kvp.Value is StorageSourceInfo sourceInfo && sourceInfo.TileEntity != null)
                    {
                        // Use actual TileEntity position, not the dict key (which may have y+10000 offset)
                        var tePos = sourceInfo.TileEntity.ToWorldPos();
                        containerPos = new Vector3(tePos.x, tePos.y, tePos.z);
                    }
                    else
                    {
                        containerPos = new Vector3(kvp.Key.x, kvp.Key.y, kvp.Key.z);
                    }
                    
                    // Centralized range check
                    if (!IsInRange(config, containerPos))
                        continue;

                    // Handle EntityStorage (vehicles/drones with live position tracking)
                    if (kvp.Value is EntityStorage es)
                    {
                        if (es.Bag != null)
                        {
                            var slots = es.Bag.GetSlots();
                            if (slots != null)
                            {
                                var lockedSlots = es.Bag.LockedSlots;
                                for (int i = 0; i < slots.Length; i++)
                                {
                                    if (config.respectLockedSlots && lockedSlots != null &&
                                        i < lockedSlots.Length && lockedSlots[i])
                                        continue;
                                    var item = slots[i];
                                    if (item != null && !item.IsEmpty())
                                        items.Add(item);
                                }
                            }
                        }
                        else if (es.LootContainer != null)
                        {
                            var lootItems = es.LootContainer.items;
                            if (lootItems != null)
                            {
                                for (int i = 0; i < lootItems.Length; i++)
                                {
                                    var item = lootItems[i];
                                    if (item != null && !item.IsEmpty())
                                        items.Add(item);
                                }
                            }
                        }
                        continue;
                    }
                    
                    // Handle tile entity lootables (storage crates, etc.)
                    if (kvp.Value is ITileEntityLootable lootable)
                    {
                        var lootItems = lootable.items;
                        if (lootItems == null) continue;

                        // Get locked slots for this container
                        PackedBoolArray lockedSlots = null;
                        if (config.respectLockedSlots)
                        {
                            if (lootable is TEFeatureStorage storage)
                                lockedSlots = storage.SlotLocks;
                            else if (lootable is TileEntitySecureLootContainer secureLoot)
                                lockedSlots = secureLoot.SlotLocks;
                        }

                        for (int i = 0; i < lootItems.Length; i++)
                        {
                            // Skip locked slots when respectLockedSlots is enabled
                            if (config.respectLockedSlots && lockedSlots != null &&
                                i < lockedSlots.Length && lockedSlots[i])
                                continue;

                            var item = lootItems[i];
                            if (item != null && !item.IsEmpty())
                                items.Add(item);
                        }
                    }
                    else if (kvp.Value is Bag bag)
                    {
                        // Legacy: Bag stored directly (shouldn't happen anymore)
                        var slots = bag.GetSlots();
                        if (slots == null) continue;

                        var lockedSlots = bag.LockedSlots;

                        for (int i = 0; i < slots.Length; i++)
                        {
                            // Skip locked slots when respectLockedSlots is enabled
                            if (config.respectLockedSlots && lockedSlots != null &&
                                i < lockedSlots.Length && lockedSlots[i])
                                continue;

                            var item = slots[i];
                            if (item != null && !item.IsEmpty())
                                items.Add(item);
                        }
                    }
                    else if (kvp.Value is StorageSourceInfo sourceInfo)
                    {
                        // Dew collectors and workstation outputs via wrapper
                        // These don't typically have locked slots
                        if (sourceInfo.Items != null)
                            items.AddRange(sourceInfo.Items.Where(i => i != null && !i.IsEmpty()));
                    }
                    else if (kvp.Value is ItemStack[] itemArray)
                    {
                        // Direct ItemStack array (fallback)
                        items.AddRange(itemArray.Where(i => i != null && !i.IsEmpty()));
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogWarning($"Error reading container at {kvp.Key}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error getting storage items: {ex.Message}");
        }

        PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_STORAGE_ITEMS);
        return items;
    }

    /// <summary>
    /// Gets item count from the currently open container (if any).
    /// Uses our cached reference from XUiC_LootContainer patch for live data.
    /// </summary>
    private static int GetOpenContainerItemCount(ItemValue item, out Vector3i openContainerPos)
    {
        openContainerPos = Vector3i.zero;
        
        try
        {
            // PRIMARY SOURCE: Our cached open container reference
            // This is set by our XUiC_LootContainer patch and is always current
            if (CurrentOpenContainer?.items != null)
            {
                openContainerPos = CurrentOpenContainerPos;
                int count = 0;
                foreach (var stack in CurrentOpenContainer.items)
                {
                    if (stack?.itemValue?.type == item.type)
                        count += stack.count;
                }
                return count;
            }
            
            // FALLBACK: Check lockedTileEntities for containers opened by local player
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return 0;
            
            var lockedTileEntities = GameManager.Instance?.lockedTileEntities;
            if (lockedTileEntities != null)
            {
                foreach (var kvp in lockedTileEntities)
                {
                    if (kvp.Value == player.entityId && kvp.Key is TileEntity te)
                    {
                        openContainerPos = te.ToWorldPos();
                        
                        ITileEntityLootable lootable = te as ITileEntityLootable 
                            ?? (te as TileEntityComposite)?.GetFeature<ITileEntityLootable>();
                        
                        if (lootable?.items != null)
                        {
                            int count = 0;
                            foreach (var stack in lootable.items)
                            {
                                if (stack?.itemValue?.type == item.type)
                                    count += stack.count;
                            }
                            return count;
                        }
                        break;
                    }
                }
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"GetOpenContainerItemCount error: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Counts items of a specific type across all accessible storage.
    /// Uses cached counts when available to avoid repeated entity iteration.
    /// 
    /// CACHING BEHAVIOR:
    /// - Within same frame: Always uses cache (ingredient checks happen in bursts)
    /// - After cache duration: Rebuilds cache (ensures freshness)
    /// - After InvalidateCache(): Rebuilds cache (responds to storage changes)
    /// </summary>
    public static int GetItemCount(ModConfig config, ItemValue item)
    {
        if (!config.modEnabled || item == null)
            return 0;

        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);

        try
        {
            float currentTime = Time.time;
            int currentFrame = Time.frameCount;
            
            // FAST PATH: Same frame as last cache build - always use cache
            // This handles the common case of checking multiple recipe ingredients in one update
            if (_itemCountCacheValid && currentFrame == _lastItemCountFrame)
            {
                PerformanceProfiler.RecordCacheHit(PerformanceProfiler.OP_GET_ITEM_COUNT);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
                return _itemCountCache.TryGetValue(item.type, out int frameCount) ? frameCount : 0;
            }
            
            // Check if we have a valid cached count (time-based expiry)
            if (_itemCountCacheValid && 
                (currentTime - _lastItemCountTime) < ITEM_COUNT_CACHE_DURATION &&
                _itemCountCache.TryGetValue(item.type, out int cachedCount))
            {
                PerformanceProfiler.RecordCacheHit(PerformanceProfiler.OP_GET_ITEM_COUNT);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
                return cachedCount;
            }

            // Cache miss or expired - rebuild the entire cache
            PerformanceProfiler.RecordCacheMiss(PerformanceProfiler.OP_GET_ITEM_COUNT);
            RebuildItemCountCache(config);
            
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
            // Return the count (may be 0 if item not in storage)
            return _itemCountCache.TryGetValue(item.type, out int count) ? count : 0;
        }
        catch (Exception ex)
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
            ProxiCraft.LogError($"Error counting items: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Overload that takes int itemId directly (used by radial menu via XUiM_PlayerInventory.GetItemCount(int)).
    /// Uses the same cache as GetItemCount(ItemValue) since the cache is keyed by item type (int).
    /// </summary>
    /// <param name="config">Mod configuration</param>
    /// <param name="itemId">The item type ID to count</param>
    /// <returns>Count of matching items in nearby containers</returns>
    public static int GetItemCount(ModConfig config, int itemId)
    {
        if (!config.modEnabled || itemId <= 0)
            return 0;

        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);

        try
        {
            float currentTime = Time.time;
            int currentFrame = Time.frameCount;
            
            // FAST PATH: Same frame as last cache build - always use cache
            if (_itemCountCacheValid && currentFrame == _lastItemCountFrame)
            {
                PerformanceProfiler.RecordCacheHit(PerformanceProfiler.OP_GET_ITEM_COUNT);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
                return _itemCountCache.TryGetValue(itemId, out int frameCount) ? frameCount : 0;
            }
            
            // Check if we have a valid cached count (time-based expiry)
            if (_itemCountCacheValid && 
                (currentTime - _lastItemCountTime) < ITEM_COUNT_CACHE_DURATION &&
                _itemCountCache.TryGetValue(itemId, out int cachedCount))
            {
                PerformanceProfiler.RecordCacheHit(PerformanceProfiler.OP_GET_ITEM_COUNT);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
                return cachedCount;
            }

            // Cache miss or expired - rebuild the entire cache
            PerformanceProfiler.RecordCacheMiss(PerformanceProfiler.OP_GET_ITEM_COUNT);
            RebuildItemCountCache(config);
            
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
            return _itemCountCache.TryGetValue(itemId, out int count) ? count : 0;
        }
        catch (Exception ex)
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_GET_ITEM_COUNT);
            ProxiCraft.LogError($"Error counting items by ID: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Rebuilds the item count cache by scanning all storage sources once.
    /// This is much more efficient than scanning per-item-type.
    /// </summary>
    private static void RebuildItemCountCache(ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_REBUILD_CACHE);
        
        _itemCountCache.Clear();
        _lastItemCountTime = Time.time;
        _lastItemCountFrame = Time.frameCount;
        _itemCountCacheValid = true;

        try
        {
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (player == null)
            {
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_REBUILD_CACHE);
                return;
            }

            Vector3 playerPos = player.position;
            float rangeSquared = config.range > 0f ? config.range * config.range : -1f;
            
            // Track open container position to avoid double-counting
            Vector3i openContainerPos = CurrentOpenContainerPos;
            
            // FIRST: Count from open container via cached reference (live data)
            if (CurrentOpenContainer?.items != null)
            {
                var items = CurrentOpenContainer.items;

                // Get locked slots for open container (depends on concrete type)
                PackedBoolArray openContainerLockedSlots = null;
                if (config.respectLockedSlots)
                {
                    if (CurrentOpenContainer is TEFeatureStorage storage)
                        openContainerLockedSlots = storage.SlotLocks;
                    else if (CurrentOpenContainer is TileEntitySecureLootContainer secureLoot)
                        openContainerLockedSlots = secureLoot.SlotLocks;
                }

                for (int i = 0; i < items.Length; i++)
                {
                    // Skip locked slots when respectLockedSlots is enabled
                    if (config.respectLockedSlots && openContainerLockedSlots != null &&
                        i < openContainerLockedSlots.Length && openContainerLockedSlots[i])
                        continue;

                    var stack = items[i];
                    if (stack?.itemValue != null && !stack.IsEmpty())
                        AddToCountCache(stack.itemValue.type, stack.count);
                }
            }

            // SECOND: Count from open vehicle via cached reference (live data)
            // When vehicle storage is open, its bag has the live data
            if (CurrentOpenVehicle != null)
            {
                var vehicleBag = ((EntityAlive)CurrentOpenVehicle).bag;
                if (vehicleBag != null)
                {
                    var slots = vehicleBag.GetSlots();
                    if (slots != null)
                    {
                        var vehicleLockedSlots = vehicleBag.LockedSlots;

                        for (int i = 0; i < slots.Length; i++)
                        {
                            // Skip locked slots when respectLockedSlots is enabled
                            if (config.respectLockedSlots && vehicleLockedSlots != null &&
                                i < vehicleLockedSlots.Length && vehicleLockedSlots[i])
                                continue;

                            var stack = slots[i];
                            if (stack?.itemValue != null && !stack.IsEmpty())
                                AddToCountCache(stack.itemValue.type, stack.count);
                        }
                    }
                }
            }
            
            // THIRD: Count from open workstation's OUTPUT only (live data)
            // When a workstation is open, count from its Output property
            if (CurrentOpenWorkstation != null)
            {
                var output = CurrentOpenWorkstation.Output;
                if (output != null)
                {
                    for (int i = 0; i < output.Length; i++)
                    {
                        var stack = output[i];
                        if (stack?.itemValue != null && !stack.IsEmpty())
                            AddToCountCache(stack.itemValue.type, stack.count);
                    }
                }
            }
            
            // Scan closed containers from TileEntities
            PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_CONTAINERS);
            int clusterCount = world.ChunkClusters.Count;
            for (int clusterIdx = 0; clusterIdx < clusterCount; clusterIdx++)
            {
                var cluster = world.ChunkClusters[clusterIdx];
                if (cluster == null) continue;

                var chunkDict = ((WorldChunkCache)cluster).chunks?.dict;
                if (chunkDict == null) continue;

                // Iterate directly over dictionary to avoid ToArray() allocation
                foreach (var chunkKvp in chunkDict)
                {
                    var chunk = chunkKvp.Value;
                    if (chunk == null) continue;

                    chunk.EnterReadLock();
                    try
                    {
                        var tileEntityDict = chunk.tileEntities?.dict;
                        if (tileEntityDict == null) continue;

                        // Iterate directly to avoid ToArray() allocation
                        foreach (var teKvp in tileEntityDict)
                        {
                            var tileEntity = teKvp.Value;
                            if (tileEntity == null) continue;

                            var worldPos = tileEntity.ToWorldPos();
                            
                            // Skip the open container - we already counted it
                            if (openContainerPos != Vector3i.zero && worldPos == openContainerPos)
                                continue;
                            
                            // Range check using squared distance (avoids sqrt)
                            if (rangeSquared > 0f)
                            {
                                float dx = playerPos.x - worldPos.x;
                                float dy = playerPos.y - worldPos.y;
                                float dz = playerPos.z - worldPos.z;
                                if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                                    continue;
                            }

                            // Skip locked containers in multiplayer (with expiration check)
                            if (IsContainerLocked(worldPos))
                                continue;

                            CountTileEntityItems(tileEntity, config);
                        }
                    }
                    finally
                    {
                        chunk.ExitReadLock();
                    }
                }
            }
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_CONTAINERS);

            // ================================================================
            // COUNT FROM ADDITIONAL STORAGE SOURCES
            // ================================================================

            // Count from vehicles (method has internal profiler timing)
            if (config.pullFromVehicles)
            {
                CountAllVehicleItems(world, playerPos, config);
            }

            // Count from drones (method has internal profiler timing)
            if (config.pullFromDrones)
            {
                CountAllDroneItems(world, playerPos, config);
            }

            // Count from dew collectors (TileEntityCollector)
            if (config.pullFromDewCollectors)
            {
                PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_DEWCOLLECTORS);
                CountAllDewCollectorItems(world, playerPos, config, openContainerPos);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_DEWCOLLECTORS);
            }

            // Count from workstation outputs (TileEntityWorkstation)
            if (config.pullFromWorkstationOutputs)
            {
                PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_WORKSTATIONS);
                CountAllWorkstationOutputItems(world, playerPos, config, openContainerPos);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_WORKSTATIONS);
            }
            
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_REBUILD_CACHE);
        }
        catch (Exception ex)
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_REBUILD_CACHE);
            ProxiCraft.LogError($"Error rebuilding item count cache: {ex.Message}");
            _itemCountCacheValid = false;
        }
    }

    /// <summary>
    /// Adds to the item count cache for a specific item type.
    /// Uses checked arithmetic to prevent integer overflow.
    /// </summary>
    private static void AddToCountCache(int itemType, int count)
    {
        if (_itemCountCache.TryGetValue(itemType, out int existing))
            _itemCountCache[itemType] = AdaptivePatching.SafeAddCount(existing, count);
        else
            _itemCountCache[itemType] = count;
    }

    /// <summary>
    /// Counts all items in a tile entity and adds to cache.
    /// Respects locked slots when config.respectLockedSlots is true.
    /// </summary>
    private static void CountTileEntityItems(TileEntity tileEntity, ModConfig config)
    {
        ItemStack[] items = null;
        PackedBoolArray lockedSlots = null;

        if (tileEntity is TileEntityComposite composite)
        {
            var storage = composite.GetFeature<TEFeatureStorage>();
            if (storage != null && storage.bPlayerStorage)
            {
                // Check lock
                var lockable = composite.GetFeature<ILockable>();
                if (lockable != null && lockable.IsLocked() && !config.allowLockedContainers)
                    return;
                if (lockable != null && lockable.IsLocked() && !lockable.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                    return;

                items = storage.items;
                lockedSlots = storage.SlotLocks;
            }
        }
        else if (tileEntity is TileEntitySecureLootContainer secureLoot)
        {
            if (secureLoot.IsLocked() && !secureLoot.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                return;

            items = secureLoot.items;
            lockedSlots = secureLoot.SlotLocks;
        }

        if (items != null)
        {
            for (int i = 0; i < items.Length; i++)
            {
                // Skip locked slots when respectLockedSlots is enabled
                if (config.respectLockedSlots && lockedSlots != null &&
                    i < lockedSlots.Length && lockedSlots[i])
                    continue;

                var stack = items[i];
                if (stack?.itemValue != null && !stack.IsEmpty())
                    AddToCountCache(stack.itemValue.type, stack.count);
            }
        }
    }

    /// <summary>
    /// Counts all items in nearby vehicles and adds to cache.
    /// Skips the currently open vehicle to avoid stale data issues.
    /// Respects locked slots when config.respectLockedSlots is true.
    /// </summary>
    private static void CountAllVehicleItems(World world, Vector3 playerPos, ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_VEHICLES);
        try
        {
            var entities = world.Entities?.list;
            if (entities == null) return;

            float rangeSquared = config.range > 0f ? config.range * config.range : 0f;

            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null || !(entity is EntityVehicle vehicle))
                    continue;

                try
                {
                    // Skip the currently open vehicle - its data may be changing
                    // When vehicle storage is open, we count from the live UI data instead
                    if (CurrentOpenVehicle != null && vehicle.entityId == CurrentOpenVehicle.entityId)
                        continue;

                    if (rangeSquared > 0f)
                    {
                        float dx = playerPos.x - vehicle.position.x;
                        float dy = playerPos.y - vehicle.position.y;
                        float dz = playerPos.z - vehicle.position.z;
                        if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                            continue;
                    }
                    if (!vehicle.LocalPlayerIsOwner())
                        continue;
                    if (!vehicle.hasStorage())
                        continue;

                    var bag = ((EntityAlive)vehicle).bag;
                    if (bag == null) continue;

                    var slots = bag.GetSlots();
                    if (slots == null) continue;

                    var lockedSlots = bag.LockedSlots;

                    for (int j = 0; j < slots.Length; j++)
                    {
                        // Skip locked slots when respectLockedSlots is enabled
                        if (config.respectLockedSlots && lockedSlots != null &&
                            j < lockedSlots.Length && lockedSlots[j])
                            continue;

                        var stack = slots[j];
                        if (stack?.itemValue != null && !stack.IsEmpty())
                            AddToCountCache(stack.itemValue.type, stack.count);
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogDebug($"Error counting vehicle items: {ex.Message}");
                }
            }
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_VEHICLES);
        }
    }

    /// <summary>
    /// Counts all items in player's drones and adds to cache.
    /// NOTE: drone.lootContainer.items and drone.bag.GetSlots() share the SAME array!
    /// We only count from lootContainer to avoid double-counting.
    /// Respects locked slots when config.respectLockedSlots is true.
    /// </summary>
    private static void CountAllDroneItems(World world, Vector3 playerPos, ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_DRONES);
        try
        {
            var entities = world.Entities?.list;
            if (entities == null) return;

            float rangeSquared = config.range > 0f ? config.range * config.range : 0f;

            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null || !(entity is EntityDrone drone))
                    continue;

                try
                {
                    // Skip the currently open drone - it's counted via CurrentOpenContainer
                    // (drone storage opens via XUiC_LootContainer which sets CurrentOpenContainer)
                    if (CurrentOpenDrone != null && drone.entityId == CurrentOpenDrone.entityId)
                        continue;

                    if (rangeSquared > 0f)
                    {
                        float dx = playerPos.x - drone.position.x;
                        float dy = playerPos.y - drone.position.y;
                        float dz = playerPos.z - drone.position.z;
                        if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                            continue;
                    }

                    // Only include drones owned by the local player
                    if (!drone.LocalPlayerIsOwner())
                        continue;

                    // Count from lootContainer ONLY (not bag - they share the same items array!)
                    // See EntityDrone line 2230: base.lootContainer.items = bag.GetSlots();
                    var lootContainer = drone.lootContainer;
                    var items = lootContainer?.items;
                    if (items != null)
                    {
                        // Get locked slots from drone's bag (shares same underlying array)
                        var lockedSlots = drone.bag?.LockedSlots;

                        for (int j = 0; j < items.Length; j++)
                        {
                            // Skip locked slots when respectLockedSlots is enabled
                            if (config.respectLockedSlots && lockedSlots != null &&
                                j < lockedSlots.Length && lockedSlots[j])
                                continue;

                            var stack = items[j];
                            if (stack?.itemValue != null && !stack.IsEmpty())
                                AddToCountCache(stack.itemValue.type, stack.count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogDebug($"Error counting drone items: {ex.Message}");
                }
            }
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_DRONES);
        }
    }

    /// <summary>
    /// Counts all items in dew collectors and adds to cache.
    /// </summary>
    private static void CountAllDewCollectorItems(World world, Vector3 playerPos, ModConfig config, Vector3i openContainerPos)
    {
        float rangeSquared = config.range > 0f ? config.range * config.range : -1f;
        
        int clusterCount = world.ChunkClusters.Count;
        for (int clusterIdx = 0; clusterIdx < clusterCount; clusterIdx++)
        {
            var cluster = world.ChunkClusters[clusterIdx];
            if (cluster == null) continue;

            var chunkDict = ((WorldChunkCache)cluster).chunks?.dict;
            if (chunkDict == null) continue;

            // Iterate directly to avoid ToArray() allocation
            foreach (var chunkKvp in chunkDict)
            {
                var chunk = chunkKvp.Value;
                if (chunk == null) continue;

                chunk.EnterReadLock();
                try
                {
                    var tileEntityDict = chunk.tileEntities?.dict;
                    if (tileEntityDict == null) continue;

                    foreach (var teKvp in tileEntityDict)
                    {
                        var tileEntity = teKvp.Value;
                        if (tileEntity == null || !(tileEntity is TileEntityCollector collector))
                            continue;

                        var worldPos = tileEntity.ToWorldPos();
                        if (openContainerPos != Vector3i.zero && worldPos == openContainerPos)
                            continue;
                        
                        // Range check using squared distance
                        if (rangeSquared > 0f)
                        {
                            float dx = playerPos.x - worldPos.x;
                            float dy = playerPos.y - worldPos.y;
                            float dz = playerPos.z - worldPos.z;
                            if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                                continue;
                        }

                        var items = collector.items;
                        if (items != null)
                        {
                            for (int i = 0; i < items.Length; i++)
                            {
                                var stack = items[i];
                                if (stack?.itemValue != null && !stack.IsEmpty())
                                    AddToCountCache(stack.itemValue.type, stack.count);
                            }
                        }
                    }
                }
                finally
                {
                    chunk.ExitReadLock();
                }
            }
        }
    }

    /// <summary>
    /// Counts all items in workstation outputs and adds to cache.
    /// Only counts from OUTPUT slots, NOT input/fuel/tool slots.
    /// Skips the currently open workstation (already counted via CurrentOpenWorkstation).
    /// </summary>
    private static void CountAllWorkstationOutputItems(World world, Vector3 playerPos, ModConfig config, Vector3i openContainerPos)
    {
        float rangeSquared = config.range > 0f ? config.range * config.range : -1f;
        
        int clusterCount = world.ChunkClusters.Count;
        for (int clusterIdx = 0; clusterIdx < clusterCount; clusterIdx++)
        {
            var cluster = world.ChunkClusters[clusterIdx];
            if (cluster == null) continue;

            var chunkDict = ((WorldChunkCache)cluster).chunks?.dict;
            if (chunkDict == null) continue;

            // Iterate directly to avoid ToArray() allocation
            foreach (var chunkKvp in chunkDict)
            {
                var chunk = chunkKvp.Value;
                if (chunk == null) continue;

                chunk.EnterReadLock();
                try
                {
                    var tileEntityDict = chunk.tileEntities?.dict;
                    if (tileEntityDict == null) continue;

                    foreach (var teKvp in tileEntityDict)
                    {
                        var tileEntity = teKvp.Value;
                        if (tileEntity == null || !(tileEntity is TileEntityWorkstation workstation))
                            continue;

                        // Skip the currently open workstation - already counted via CurrentOpenWorkstation
                        if (CurrentOpenWorkstation != null && workstation == CurrentOpenWorkstation)
                            continue;

                        var worldPos = tileEntity.ToWorldPos();
                        if (openContainerPos != Vector3i.zero && worldPos == openContainerPos)
                            continue;
                        
                        // Range check using squared distance
                        if (rangeSquared > 0f)
                        {
                            float dx = playerPos.x - worldPos.x;
                            float dy = playerPos.y - worldPos.y;
                            float dz = playerPos.z - worldPos.z;
                            if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                                continue;
                        }

                        var output = workstation.Output;
                        if (output != null)
                        {
                            for (int i = 0; i < output.Length; i++)
                            {
                                var stack = output[i];
                                if (stack?.itemValue != null && !stack.IsEmpty())
                                    AddToCountCache(stack.itemValue.type, stack.count);
                            }
                        }
                    }
                }
                finally
                {
                    chunk.ExitReadLock();
                }
            }
        }
    }

    /// <summary>
    /// Removes items from storage containers.
    /// Respects locked slots when config.respectLockedSlots is true.
    /// 
    /// ORDER OF OPERATIONS (critical for sync - see RESEARCH_NOTES.md):
    /// 1. Remove from CurrentOpenWorkstation.Output FIRST (if open and has items)
    /// 2. Then iterate _currentStorageDict for other containers
    /// 3. Skip the open workstation in dict iteration (avoid double-removal)
    /// 4. Call SetModified() after modification
    /// 5. Trigger UI refresh via UpdateBackend
    /// 6. InvalidateCache() after all removals
    /// </summary>
    /// <param name="config">Mod configuration</param>
    /// <param name="item">The item type to remove</param>
    /// <param name="count">Number of items to remove</param>
    /// <returns>Number of items actually removed</returns>
    public static int RemoveItems(ModConfig config, ItemValue item, int count)
    {
        if (!config.modEnabled || item == null || count <= 0)
            return 0;

        int remaining = count;
        int removed = 0;
        bool handledOpenWorkstation = false;

        try
        {
            RefreshStorages(config);
            
            // ====================================================================================
            // When a workstation is open, we need to remove from its live Output reference
            // before iterating the storage dict. This ensures the UI sees the removal immediately.
            // We track this with a flag to skip the same workstation in the dict iteration.
            //
            // CRITICAL: We must modify the UI slots directly, NOT the TileEntity!
            // The UI has its own copy of slots. On close, syncTEfromUI() copies UI -> TileEntity,
            // which would overwrite any direct TileEntity modifications.
            // ====================================================================================
            if (config.pullFromWorkstationOutputs && CurrentOpenWorkstation != null && remaining > 0)
            {
                // Check range to the open workstation
                var workstationPos = CurrentOpenWorkstation.ToWorldPos();
                if (IsInRange(config, workstationPos))
                {
                    // Use the new UI-based removal
                    int uiRemoved = RemoveFromWorkstationOutputUI(item, remaining, out handledOpenWorkstation);
                    remaining -= uiRemoved;
                    removed += uiRemoved;
                }
            }

            // Iterate storages in priority order (configured in storagePriority config section)
            foreach (var kvp in StoragePriority.OrderStorages(_currentStorageDict, GetStorageType))
            {
                if (remaining <= 0)
                    break;

                try
                {
                    // Determine actual position for range check
                    // For EntityStorage (vehicles/drones), use their current live position
                    // For StorageSourceInfo (workstation outputs, dew collectors), use the TileEntity position
                    //   (dict key has y+10000 offset to avoid collision, so we can't use it for range check)
                    // For tile entities, use the dict key (fixed position)
                    Vector3 containerPos;
                    if (kvp.Value is EntityStorage entityStorage)
                    {
                        if (!entityStorage.IsValid())
                            continue; // Entity was despawned
                        containerPos = entityStorage.GetCurrentPosition();
                    }
                    else if (kvp.Value is StorageSourceInfo sourceInfo && sourceInfo.TileEntity != null)
                    {
                        // Use actual TileEntity position, not the dict key (which may have y+10000 offset)
                        var tePos = sourceInfo.TileEntity.ToWorldPos();
                        containerPos = new Vector3(tePos.x, tePos.y, tePos.z);
                    }
                    else
                    {
                        containerPos = new Vector3(kvp.Key.x, kvp.Key.y, kvp.Key.z);
                    }
                    
                    // Centralized range check
                    if (!IsInRange(config, containerPos))
                        continue;

                    // Handle EntityStorage (vehicles/drones with live position tracking)
                    // NOTE: Entity could despawn during iteration. We use try-catch as safety net.
                    // FUTURE: If crashes persist, add re-validation per slot: if (!es.IsValid()) break;
                    if (kvp.Value is EntityStorage es)
                    {
                        try
                        {
                            if (es.Bag != null)
                            {
                                var slots = es.Bag.GetSlots();
                                if (slots != null)
                                {
                                    var lockedSlots = es.Bag.LockedSlots;
                                    for (int i = 0; i < slots.Length && remaining > 0; i++)
                                    {
                                        // Defensive bounds check - slots array could change
                                        if (i >= slots.Length || slots[i] == null)
                                            continue;
                                        if (config.respectLockedSlots && lockedSlots != null &&
                                            i < lockedSlots.Length && lockedSlots[i])
                                            continue;
                                        if (slots[i].itemValue?.type != item.type)
                                            continue;
                                        int toRemove = Math.Min(remaining, slots[i].count);
                                        ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName() ?? "unknown"} from entity storage");
                                        if (slots[i].count <= toRemove)
                                            slots[i].Clear();
                                        else
                                            slots[i].count -= toRemove;
                                        remaining -= toRemove;
                                        removed += toRemove;
                                        es.Bag.onBackpackChanged();
                                    }
                                }
                            }
                            else if (es.LootContainer != null)
                            {
                                var lootItems = es.LootContainer.items;
                                if (lootItems != null)
                                {
                                    for (int i = 0; i < lootItems.Length && remaining > 0; i++)
                                    {
                                        // Defensive bounds check - lootItems array could change
                                        if (i >= lootItems.Length || lootItems[i] == null)
                                            continue;
                                        if (lootItems[i].itemValue?.type != item.type)
                                            continue;
                                        int toRemove = Math.Min(remaining, lootItems[i].count);
                                        ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName() ?? "unknown"} from entity loot container");
                                        if (lootItems[i].count <= toRemove)
                                            lootItems[i].Clear();
                                        else
                                            lootItems[i].count -= toRemove;
                                        remaining -= toRemove;
                                        removed += toRemove;
                                    }
                                }
                            }
                        }
                        catch (Exception esEx)
                        {
                            // Entity likely despawned during iteration - log and continue with other containers
                            ProxiCraft.LogWarning($"[CrashPrevention] EntityStorage became invalid during item removal at {kvp.Key}: {esEx.GetType().Name}");
                            // Refresh storages to clear stale references
                            _storageRefreshNeeded = true;
                        }
                        continue;
                    }

                    // Handle tile entity lootables (storage crates, etc.)
                    // CRASH PREVENTION: Check IsRemoving, use chunk locks, wrap in try-catch
                    if (kvp.Value is ITileEntityLootable lootable)
                    {
                        // Skip if tile entity is being destroyed (crash prevention #1)
                        if (lootable is TileEntity te && te.IsRemoving)
                        {
                            ProxiCraft.LogDebug($"Skipping container at {kvp.Key} - TileEntity is being removed");
                            continue;
                        }

                        // Get chunk and acquire read lock (crash prevention #2)
                        var world = GameManager.Instance?.World;
                        var chunk = world?.GetChunkFromWorldPos(kvp.Key) as Chunk;
                        if (chunk == null)
                        {
                            ProxiCraft.LogDebug($"Skipping container at {kvp.Key} - chunk not loaded");
                            continue;
                        }

                        chunk.EnterReadLock();
                        try
                        {
                            // Re-validate after acquiring lock (chunk could have unloaded between check and lock)
                            if (lootable is TileEntity te2 && te2.IsRemoving)
                            {
                                ProxiCraft.LogDebug($"Skipping container at {kvp.Key} - TileEntity removed while acquiring lock");
                                continue;
                            }

                            var items = lootable.items;
                            if (items == null) continue;

                            // Get locked slots for this container
                            PackedBoolArray lockedSlots = null;
                            if (config.respectLockedSlots)
                            {
                                if (lootable is TEFeatureStorage storage)
                                    lockedSlots = storage.SlotLocks;
                                else if (lootable is TileEntitySecureLootContainer secureLoot)
                                    lockedSlots = secureLoot.SlotLocks;
                            }

                            for (int i = 0; i < items.Length && remaining > 0; i++)
                            {
                                // Defensive bounds + null check (crash prevention #3)
                                if (i >= items.Length || items[i] == null)
                                    continue;

                                // Skip locked slots when respectLockedSlots is enabled
                                if (config.respectLockedSlots && lockedSlots != null &&
                                    i < lockedSlots.Length && lockedSlots[i])
                                    continue;

                                if (items[i].itemValue?.type != item.type)
                                    continue;

                                int toRemove = Math.Min(remaining, items[i].count);

                                ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName() ?? "unknown"} from container");

                                if (items[i].count <= toRemove)
                                {
                                    items[i].Clear();
                                }
                                else
                                {
                                    items[i].count -= toRemove;
                                }

                                remaining -= toRemove;
                                removed += toRemove;

                                // Mark tile entity as modified so it saves
                                if (lootable is ITileEntity tileEntity)
                                {
                                    tileEntity.SetModified();
                                }
                            }
                        }
                        catch (Exception teEx)
                        {
                            // TileEntity likely destroyed during iteration - log and continue with other containers
                            ProxiCraft.LogWarning($"[CrashPrevention] TileEntity error during item removal at {kvp.Key}: {teEx.GetType().Name} - {teEx.Message}");
                            // Flag for refresh to clear stale references
                            _storageRefreshNeeded = true;
                        }
                        finally
                        {
                            chunk.ExitReadLock();
                        }
                    }
                    else if (kvp.Value is Bag bag)
                    {
                        // Legacy: Bag stored directly (shouldn't happen anymore)
                        var slots = bag.GetSlots();
                        if (slots == null) continue;

                        var lockedSlots = bag.LockedSlots;

                        for (int i = 0; i < slots.Length && remaining > 0; i++)
                        {
                            // Defensive bounds + null check (crash prevention #3)
                            if (i >= slots.Length || slots[i] == null)
                                continue;

                            // Skip locked slots when respectLockedSlots is enabled
                            if (config.respectLockedSlots && lockedSlots != null &&
                                i < lockedSlots.Length && lockedSlots[i])
                                continue;

                            if (slots[i].itemValue?.type != item.type)
                                continue;

                            int toRemove = Math.Min(remaining, slots[i].count);

                            ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName() ?? "unknown"} from vehicle");

                            if (slots[i].count <= toRemove)
                            {
                                slots[i].Clear();
                            }
                            else
                            {
                                slots[i].count -= toRemove;
                            }

                            remaining -= toRemove;
                            removed += toRemove;

                            bag.onBackpackChanged();
                        }
                    }
                    else if (kvp.Value is StorageSourceInfo sourceInfo)
                    {
                        // Handle dew collectors and workstation outputs via StorageSourceInfo wrapper
                        
                        // Skip if this is the open workstation we already handled above
                        if (handledOpenWorkstation && sourceInfo.TileEntity == CurrentOpenWorkstation)
                        {
                            ProxiCraft.LogDebug($"Skipping {sourceInfo.SourceType} - already handled as open workstation");
                            continue;
                        }
                        
                        var slots = sourceInfo.Items;
                        if (slots == null) continue;

                        ProxiCraft.LogDebug($"Checking {sourceInfo.SourceType} with {slots.Length} slots for item type {item.type} ({item.ItemClass?.GetItemName()})");
                        
                        for (int i = 0; i < slots.Length && remaining > 0; i++)
                        {
                            // Defensive bounds + null check (crash prevention #3)
                            if (i >= slots.Length || slots[i] == null || slots[i].itemValue == null)
                            {
                                ProxiCraft.LogDebug($"  Slot {i}: null or empty");
                                continue;
                            }
                            
                            ProxiCraft.LogDebug($"  Slot {i}: type={slots[i].itemValue.type}, count={slots[i].count}, looking for type={item.type}");
                            
                            if (slots[i].itemValue.type != item.type)
                                continue;

                            int toRemove = Math.Min(remaining, slots[i].count);

                            ProxiCraft.LogDebug($"Removing {toRemove}/{remaining} {item.ItemClass?.GetItemName() ?? "unknown"} from {sourceInfo.SourceType}");

                            if (slots[i].count <= toRemove)
                            {
                                slots[i].Clear();
                            }
                            else
                            {
                                slots[i].count -= toRemove;
                            }

                            remaining -= toRemove;
                            removed += toRemove;

                            // Mark the source as modified - for workstations, reassign Output property
                            sourceInfo.MarkModified();
                        }
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogWarning($"Error removing items from container at {kvp.Key}: {ex.Message}");
                }
            }
            
            // Invalidate cache since storage contents changed
            if (removed > 0)
            {
                InvalidateCache();
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error removing items: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// Wrapper class to hold storage source info with its items and modification callback.
    /// Used for dew collectors, workstation outputs, and other non-standard storage.
    /// </summary>
    private class StorageSourceInfo
    {
        public string SourceType { get; }
        public ItemStack[] Items { get; }
        public TileEntity TileEntity { get; }
        public StorageType Type { get; }

        public StorageSourceInfo(string sourceType, ItemStack[] items, TileEntity tileEntity, StorageType type)
        {
            SourceType = sourceType;
            Items = items;
            TileEntity = tileEntity;
            Type = type;
        }

        public void MarkModified()
        {
            if (TileEntity == null) return;
            
            // For workstations, we must reassign the Output property to trigger proper sync
            // The Output setter does: output = ItemStack.Clone(value); setModified();
            // Just calling SetModified() doesn't properly sync the item changes
            if (TileEntity is TileEntityWorkstation workstation && SourceType == "workstation output")
            {
                workstation.Output = Items;
            }
            else
            {
                TileEntity.SetModified();
            }
        }
    }

    /// <summary>
    /// Gets the StorageType for a storage object in the dictionary.
    /// Used for priority ordering during iteration.
    /// </summary>
    private static StorageType GetStorageType(object storage)
    {
        return storage switch
        {
            EntityStorage es => es.Type,
            StorageSourceInfo si => si.Type,
            // Containers (TEFeatureStorage, TileEntitySecureLootContainer) default to Container type
            _ => StorageType.Container
        };
    }

    /// <summary>
    /// Refreshes the list of accessible storage containers near the player.
    /// For unlimited range (-1), uses incremental scanning to avoid repeated full scans.
    /// </summary>
    private static void RefreshStorages(ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_REFRESH_STORAGES);
        try
        {
            // Get player position
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            
            if (player == null)
            {
                _currentStorageDict.Clear();
                return;
            }

            Vector3 playerPos = player.position;
            float currentTime = Time.time;

            // Check if we should skip the scan (caching)
            // Always rescan if force flag is set or if a container is currently open by local player
            bool containerOpenByPlayer = IsAnyContainerOpenByLocalPlayer();
            
            // Use squared distance for position change check (avoids sqrt)
            float dx = playerPos.x - _lastScanPosition.x;
            float dy = playerPos.y - _lastScanPosition.y;
            float dz = playerPos.z - _lastScanPosition.z;
            float distSquared = dx * dx + dy * dy + dz * dz;
            
            // If crash prevention flagged a refresh need, force a full rescan to clear stale refs
            if (_storageRefreshNeeded)
            {
                ProxiCraft.LogDebug("[CrashPrevention] Forcing storage refresh due to previous error");
                _storageRefreshNeeded = false;
                _forceCacheRefresh = true;
                // Clear both dicts to force full rebuild
                _currentStorageDict.Clear();
                _knownStorageDict.Clear();
            }
            
            bool shouldRescan = 
                _forceCacheRefresh ||
                containerOpenByPlayer ||  // Always refresh when player has a container open
                currentTime - _lastScanTime > SCAN_COOLDOWN ||
                distSquared > POSITION_CHANGE_THRESHOLD * POSITION_CHANGE_THRESHOLD;

            if (!shouldRescan && _currentStorageDict.Count > 0)
                return;

            _forceCacheRefresh = false;
            _lastScanTime = currentTime;
            _lastScanPosition = playerPos;

            // OPTIMIZATION: Don't clear the storage dict on every rescan.
            // - New containers are added as they're discovered
            // - Out-of-range containers stay cached (range is checked at access time anyway)
            // - Stale refs (destroyed containers) are cleaned up periodically
            // - Memory cost is trivial (~50KB for 1000 containers)
            // This makes subsequent scans much faster as we only add, never rebuild.

            // Scan entities (vehicles, drones) from the world entity list
            ScanWorldEntities(world, playerPos, config);

            // Scan all chunk clusters for tile entities
            for (int clusterIdx = 0; clusterIdx < world.ChunkClusters.Count; clusterIdx++)
            {
                var cluster = world.ChunkClusters[clusterIdx];
                if (cluster == null) continue;

                var chunkCache = cluster as WorldChunkCache;
                var chunkDict = chunkCache?.chunks?.dict;
                if (chunkDict == null) continue;

                // Iterate dictionary directly without ToArray() allocation
                foreach (var kvp in chunkDict)
                {
                    var chunk = kvp.Value;
                    if (chunk == null) continue;

                    chunk.EnterReadLock();
                    try
                    {
                        // Scan tile entities (containers, dew collectors, workstations)
                        ScanChunkTileEntities(chunk, playerPos, config);
                    }
                    finally
                    {
                        chunk.ExitReadLock();
                    }
                }
            }
            
            // Periodically clean up stale entries (every ~10 seconds)
            // This handles containers that were destroyed
            if (currentTime - _lastCleanupTime > CLEANUP_INTERVAL)
            {
                CleanupStaleContainers(world);
                _lastCleanupTime = currentTime;
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error refreshing storages: {ex.Message}");
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_REFRESH_STORAGES);
        }
    }
    
    /// <summary>
    /// Removes containers that no longer exist in the world.
    /// Called periodically for unlimited range to prevent stale refs.
    /// </summary>
    private static void CleanupStaleContainers(World world)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_CLEANUP_STALE);
        var keysToRemove = new List<Vector3i>();
        
        foreach (var kvp in _currentStorageDict)
        {
            bool stillExists = false;
            
            if (kvp.Value is EntityStorage entityStorage)
            {
                // EntityStorage wraps vehicles/drones with their entity reference
                // Check if entity is still valid
                stillExists = entityStorage.IsValid();
            }
            else if (kvp.Value is Bag)
            {
                // Legacy: Bags from vehicles/drones stored directly
                // These are re-added on each entity scan, if entity is gone it won't be re-added
                stillExists = true;
            }
            else if (kvp.Value is ITileEntityLootable || kvp.Value is StorageSourceInfo)
            {
                // Tile entities - check if chunk still has it
                var chunk = world.GetChunkFromWorldPos(kvp.Key) as Chunk;
                if (chunk != null && chunk.tileEntities?.dict != null)
                {
                    var localPos = World.toBlock(kvp.Key);
                    stillExists = chunk.tileEntities.dict.ContainsKey(localPos);
                }
            }
            
            if (!stillExists)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            _currentStorageDict.Remove(key);
            _knownStorageDict.Remove(key);
        }
        
        if (keysToRemove.Count > 0)
        {
            ProxiCraft.LogDebug($"Cleaned up {keysToRemove.Count} stale container refs");
        }
        PerformanceProfiler.StopTimer(PerformanceProfiler.OP_CLEANUP_STALE);
    }

    // Reusable lists for entity scanning (avoid allocations)
    private static readonly List<Entity> _vehicleScanList = new List<Entity>(16);
    private static readonly List<Entity> _droneScanList = new List<Entity>(8);

    // Cached decision for which scan method to use (calculated once at startup)
    private static bool _useSpatialQuery = true;
    private static bool _scanMethodCalculated = false;

    // Threshold below which spatial query is always better (625 chunk lookups max)
    private const float SPATIAL_ALWAYS_BETTER_RANGE = 200f;

    /// <summary>
    /// Determines the most efficient scan method based on world conditions.
    /// Called once at startup or when config is reloaded.
    /// For range ≤200, spatial is always better. For larger ranges, compare costs.
    /// </summary>
    private static bool ShouldUseSpatialQuery(World world, float range)
    {
        // Unlimited range always uses entity iteration
        if (range <= 0f) return false;
        
        // Small ranges: spatial query is always better (≤625 chunk lookups)
        if (range <= SPATIAL_ALWAYS_BETTER_RANGE) return true;
        
        // Large ranges: compare chunk lookup cost vs entity iteration cost
        // Chunk lookup cost: (range*2/16)^2 = (range/8)^2
        float chunksPerSide = range / 8f;
        float chunkLookupCost = chunksPerSide * chunksPerSide;
        
        // Entity iteration cost (type checks are ~2x faster than chunk lookups)
        int entityCount = world.Entities?.list?.Count ?? 0;
        const float ENTITY_COST_MULTIPLIER = 2f;
        
        bool useSpatial = chunkLookupCost < (entityCount * ENTITY_COST_MULTIPLIER);
        
        ProxiCraft.LogDebug($"Scan method decision: range={range}, chunks={chunkLookupCost:F0}, " +
            $"entities={entityCount}, useSpatial={useSpatial}");
        
        return useSpatial;
    }

    /// <summary>
    /// Calculates and caches the optimal scan method. Call on world load or config reload.
    /// </summary>
    public static void CalculateScanMethod(ModConfig config)
    {
        var world = GameManager.Instance?.World;
        if (world == null)
        {
            // Default to spatial for reasonable ranges until world loads
            _useSpatialQuery = config.range > 0f && config.range <= SPATIAL_ALWAYS_BETTER_RANGE;
            return;
        }
        
        _useSpatialQuery = ShouldUseSpatialQuery(world, config.range);
        _scanMethodCalculated = true;
        
        string method = _useSpatialQuery ? "Spatial Query" : "Entity Iteration";
        ProxiCraft.Log($"Entity scan method: {method} (range={config.range}, entities={world.Entities?.list?.Count ?? 0})");
    }

    /// <summary>
    /// Scans world entities for vehicles and drones using the optimal method.
    /// Method is chosen once at startup based on range and entity count.
    /// </summary>
    private static void ScanWorldEntities(World world, Vector3 playerPos, ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_SCAN_ENTITIES);
        try
        {
            // Calculate scan method once on first use (in case it wasn't done at startup)
            if (!_scanMethodCalculated)
            {
                CalculateScanMethod(config);
            }
            
            if (_useSpatialQuery && config.range > 0f)
            {
                // Bounded range: use spatial query
                var bounds = new Bounds(playerPos, new Vector3(config.range * 2f, config.range * 2f, config.range * 2f));
                ScanWorldEntitiesBounded(world, playerPos, config, bounds);
            }
            else
            {
                // Unlimited range or large range with high entity density: iterate all entities
                ScanWorldEntitiesUnlimited(world, playerPos, config);
            }
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_SCAN_ENTITIES);
        }
    }

    /// <summary>
    /// Scans entities using spatial bounds query. Much faster for bounded ranges.
    /// </summary>
    private static void ScanWorldEntitiesBounded(World world, Vector3 playerPos, ModConfig config, Bounds bounds)
    {
        // Scan for vehicles using spatial query
        if (config.pullFromVehicles)
        {
            _vehicleScanList.Clear();
            world.GetEntitiesInBounds(typeof(EntityVehicle), bounds, _vehicleScanList);
            
            for (int i = 0; i < _vehicleScanList.Count; i++)
            {
                if (_vehicleScanList[i] is EntityVehicle vehicle)
                {
                    try
                    {
                        ProcessVehicleEntity(vehicle, playerPos, config);
                    }
                    catch (Exception ex)
                    {
                        ProxiCraft.LogWarning($"Error processing vehicle: {ex.Message}");
                    }
                }
            }
        }

        // Scan for drones using spatial query
        if (config.pullFromDrones)
        {
            _droneScanList.Clear();
            world.GetEntitiesInBounds(typeof(EntityDrone), bounds, _droneScanList);
            
            for (int i = 0; i < _droneScanList.Count; i++)
            {
                if (_droneScanList[i] is EntityDrone drone)
                {
                    try
                    {
                        ProcessDroneEntity(drone, playerPos, config);
                    }
                    catch (Exception ex)
                    {
                        ProxiCraft.LogWarning($"Error processing drone: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Scans all world entities for unlimited range mode.
    /// Slower but necessary for truly unlimited range.
    /// Still filters by type - only checks vehicles and drones, not zombies/animals.
    /// </summary>
    private static void ScanWorldEntitiesUnlimited(World world, Vector3 playerPos, ModConfig config)
    {
        var entities = world.Entities?.list;
        if (entities == null) return;

        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity == null) continue;

            try
            {
                // Type check first - skip zombies, animals, players, etc.
                if (config.pullFromVehicles && entity is EntityVehicle vehicle)
                {
                    ProcessVehicleEntity(vehicle, playerPos, config);
                }
                else if (config.pullFromDrones && entity is EntityDrone drone)
                {
                    ProcessDroneEntity(drone, playerPos, config);
                }
                // All other entity types are ignored (zombies, animals, etc.)
            }
            catch (Exception ex)
            {
                ProxiCraft.LogWarning($"Error scanning entity {entity.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Processes a vehicle entity for storage.
    /// </summary>
    private static void ProcessVehicleEntity(EntityVehicle vehicle, Vector3 playerPos, ModConfig config)
    {
        // Only include vehicles owned by the local player
        if (!vehicle.LocalPlayerIsOwner())
            return;

        // Check if vehicle has storage
        if (!vehicle.hasStorage())
            return;

        var bag = ((EntityAlive)vehicle).bag;
        if (bag == null || bag.IsEmpty())
            return;

        var vehiclePos = new Vector3i(vehicle.position);
        
        // Wrap in EntityStorage for live position tracking
        var entityStorage = new EntityStorage(vehicle, bag, StorageType.Vehicle);
        _knownStorageDict[vehiclePos] = entityStorage;
        _currentStorageDict[vehiclePos] = entityStorage;

        ProxiCraft.LogDebug($"Adding vehicle {((EntityAlive)vehicle).EntityName} at {vehiclePos}");
    }

    /// <summary>
    /// Processes a drone entity for storage.
    /// </summary>
    private static void ProcessDroneEntity(EntityDrone drone, Vector3 playerPos, ModConfig config)
    {
        // Only include drones owned by the local player
        if (!drone.IsOwner(PlatformManager.InternalLocalUserIdentifier))
            return;

        // Skip if drone is in a bad state
        if (drone.isInteractionLocked || drone.isOwnerSyncPending)
            return;
        if (drone.isShutdownPending || drone.isShutdown)
            return;

        // Check if user is allowed access
        if (!drone.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
            return;

        // Get drone storage - try lootContainer first, then bag
        if (drone.lootContainer != null)
        {
            // lootContainer is ITileEntityLootable, but we need to get items from it
            var lootItems = drone.lootContainer.items;
            if (lootItems != null && lootItems.Any(i => i != null && !i.IsEmpty()))
            {
                var dronePos = new Vector3i(drone.position);
                var droneLootStorage = new EntityStorage(drone, drone.lootContainer, StorageType.Drone);
                _knownStorageDict[dronePos] = droneLootStorage;
                _currentStorageDict[dronePos] = droneLootStorage;
                ProxiCraft.LogDebug($"Adding drone lootContainer at {dronePos}");
                return;
            }
        }

        var droneBag = drone.bag;
        if (droneBag == null || droneBag.IsEmpty())
            return;

        var pos = new Vector3i(drone.position);
        var droneBagStorage = new EntityStorage(drone, droneBag, StorageType.Drone);
        _knownStorageDict[pos] = droneBagStorage;
        _currentStorageDict[pos] = droneBagStorage;
        ProxiCraft.LogDebug($"Adding drone bag at {pos}");
    }

    private static void ScanChunkTileEntities(Chunk chunk, Vector3 playerPos, ModConfig config)
    {
        PerformanceProfiler.StartTimer(PerformanceProfiler.OP_SCAN_TILE_ENTITIES);
        try
        {
            var tileEntityKeys = chunk.tileEntities?.dict?.Keys?.ToArray();
            if (tileEntityKeys == null) return;

            foreach (var key in tileEntityKeys)
            {
                try
                {
                    if (!chunk.tileEntities.dict.TryGetValue(key, out var tileEntity))
                        continue;

                    // Skip if being removed
                    if (tileEntity.IsRemoving)
                        continue;

                    var worldPos = tileEntity.ToWorldPos();

                    // Range check early for performance
                    if (config.range > 0f && Vector3.Distance(playerPos, (Vector3)worldPos) >= config.range)
                        continue;

                    // Skip if this container is locked by another player in multiplayer (with expiration check)
                    if (IsContainerLocked(worldPos))
                        continue;

                    // Process dew collectors
                    if (config.pullFromDewCollectors && tileEntity is TileEntityCollector dewCollector)
                    {
                        ProcessDewCollector(dewCollector, worldPos, playerPos, config);
                        continue;
                    }

                    // Process workstation outputs
                    if (config.pullFromWorkstationOutputs && tileEntity is TileEntityWorkstation workstation)
                    {
                        ProcessWorkstationOutput(workstation, worldPos, playerPos, config);
                        continue;
                    }

                    // Handle composite tile entities (newer container type)
                    if (tileEntity is TileEntityComposite composite)
                    {
                        ProcessCompositeTileEntity(composite, worldPos, playerPos, config);
                    }
                    // Handle legacy secure loot containers
                    else if (tileEntity is TileEntitySecureLootContainer secureLoot)
                    {
                        ProcessSecureLootContainer(secureLoot, worldPos, playerPos, config);
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogWarning($"Error scanning tile entity: {ex.Message}");
                }
            }
        }
        finally
        {
            PerformanceProfiler.StopTimer(PerformanceProfiler.OP_SCAN_TILE_ENTITIES);
        }
    }

    /// <summary>
    /// Processes a dew collector for item storage.
    /// </summary>
    private static void ProcessDewCollector(TileEntityCollector dewCollector, Vector3i worldPos, Vector3 playerPos, ModConfig config)
    {
        // Skip if someone else is using it
        if (dewCollector.bUserAccessing)
            return;

        var items = dewCollector.items;
        if (items == null || !items.Any(i => i != null && !i.IsEmpty()))
            return;

        var sourceInfo = new StorageSourceInfo("dew collector", items, dewCollector, StorageType.DewCollector);
        _knownStorageDict[worldPos] = sourceInfo;
        _currentStorageDict[worldPos] = sourceInfo;
        ProxiCraft.LogDebug($"Adding dew collector at {worldPos}");
    }

    /// <summary>
    /// Processes a workstation output for item storage.
    /// Only includes OUTPUT slots, not input/fuel slots.
    /// </summary>
    private static void ProcessWorkstationOutput(TileEntityWorkstation workstation, Vector3i worldPos, Vector3 playerPos, ModConfig config)
    {
        // Only process player-placed workstations
        if (!workstation.IsPlayerPlaced)
            return;

        // Get output slots only (not input or fuel)
        var outputItems = workstation.Output;
        if (outputItems == null || !outputItems.Any(i => i != null && !i.IsEmpty()))
            return;

        var sourceInfo = new StorageSourceInfo("workstation output", outputItems, workstation, StorageType.Workstation);
        // Use a modified position to avoid collision with container scanning
        var outputPos = new Vector3i(worldPos.x, worldPos.y + 10000, worldPos.z);
        _knownStorageDict[outputPos] = sourceInfo;
        _currentStorageDict[outputPos] = sourceInfo;
        ProxiCraft.LogDebug($"Adding workstation output at {worldPos}");
    }

    private static void ProcessCompositeTileEntity(TileEntityComposite composite, Vector3i worldPos, Vector3 playerPos, ModConfig config)
    {
        var storageFeature = composite.GetFeature<ITileEntityLootable>();

        if (!(storageFeature is TEFeatureStorage storage))
            return;

        // Only include player storage containers
        if (!storage.bPlayerStorage)
            return;

        // Check if locked
        var lockable = composite.GetFeature<ILockable>();
        if (lockable != null && lockable.IsLocked())
        {
            if (!config.allowLockedContainers)
                return;
            if (!lockable.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                return;
        }

        // Check if another player has it open
        if (IsContainerInUse((TileEntity)(object)composite))
            return;

        _knownStorageDict[worldPos] = storage;
        _currentStorageDict[worldPos] = storage;
        ProxiCraft.LogDebug($"Adding storage container at {worldPos}");
    }

    private static void ProcessSecureLootContainer(TileEntitySecureLootContainer secureLoot, Vector3i worldPos, Vector3 playerPos, ModConfig config)
    {
        // Check if locked
        if (secureLoot.IsLocked() && !secureLoot.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
            return;

        // Check if another player has it open
        if (IsContainerInUse(secureLoot))
            return;

        _knownStorageDict[worldPos] = secureLoot;
        _currentStorageDict[worldPos] = secureLoot;
        ProxiCraft.LogDebug($"Adding secure loot container at {worldPos}");
    }

    private static bool IsContainerInUse(TileEntity tileEntity)
    {
        try
        {
            var lockedTileEntities = GameManager.Instance?.lockedTileEntities;
            if (lockedTileEntities == null)
                return false;

            if (!lockedTileEntities.ContainsKey((ITileEntity)(object)tileEntity))
                return false;

            int entityId = lockedTileEntities[(ITileEntity)(object)tileEntity];
            var entity = GameManager.Instance.World.GetEntity(entityId) as EntityAlive;

            // If entity is null or dead, the container isn't really in use
            if (entity == null || entity.IsDead())
                return false;
            
            // Allow containers opened by the LOCAL player - we want to include our own containers
            // Only exclude containers opened by OTHER players
            var localPlayer = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (localPlayer != null && entity.entityId == localPlayer.entityId)
                return false; // Local player has it open - allow access
            
            return true; // Someone else has it open - exclude
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Checks if the local player currently has any container open.
    /// Used to force cache refresh while player is moving items.
    /// </summary>
    private static bool IsAnyContainerOpenByLocalPlayer()
    {
        try
        {
            var lockedTileEntities = GameManager.Instance?.lockedTileEntities;
            if (lockedTileEntities == null || lockedTileEntities.Count == 0)
                return false;

            var localPlayer = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (localPlayer == null)
                return false;

            foreach (var kvp in lockedTileEntities)
            {
                if (kvp.Value == localPlayer.entityId)
                    return true;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
}
