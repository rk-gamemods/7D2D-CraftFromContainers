using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Platform;
using UnityEngine;

namespace ProxiCraft;

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
    
    // Lock positions from multiplayer sync
    public static readonly HashSet<Vector3i> LockedList = new HashSet<Vector3i>();
    
    // Cache timing to avoid excessive scanning
    private static float _lastScanTime;
    private static Vector3 _lastScanPosition;
    private const float SCAN_COOLDOWN = 0.1f; // Don't rescan more than 10 times per second
    private const float POSITION_CHANGE_THRESHOLD = 1f; // Rescan if player moved more than 1 unit
    
    // Flag to force cache refresh (set when containers change)
    private static bool _forceCacheRefresh = true;
    
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
    /// Clears all cached storage references. Call when starting a new game.
    /// </summary>
    public static void ClearCache()
    {
        _knownStorageDict.Clear();
        _currentStorageDict.Clear();
        _itemCountCache.Clear();
        LockedList.Clear();
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
    }

    /// <summary>
    /// Gets items from all accessible storage containers.
    /// Used by crafting UI to determine available materials.
    /// </summary>
    public static List<ItemStack> GetStorageItems(ModConfig config)
    {
        var items = new List<ItemStack>();
        
        if (!config.modEnabled)
            return items;

        try
        {
            RefreshStorages(config);
            
            foreach (var kvp in _currentStorageDict)
            {
                try
                {
                    if (kvp.Value is ITileEntityLootable lootable)
                    {
                        var lootItems = lootable.items;
                        if (lootItems != null)
                            items.AddRange(lootItems.Where(i => i != null && !i.IsEmpty()));
                    }
                    else if (kvp.Value is Bag bag)
                    {
                        var slots = bag.GetSlots();
                        if (slots != null)
                            items.AddRange(slots.Where(i => i != null && !i.IsEmpty()));
                    }
                    else if (kvp.Value is StorageSourceInfo sourceInfo)
                    {
                        // Dew collectors and workstation outputs via wrapper
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
                for (int i = 0; i < items.Length; i++)
                {
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
                        for (int i = 0; i < slots.Length; i++)
                        {
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

                            // Skip locked containers in multiplayer
                            if (LockedList.Contains(worldPos))
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

            // Count from vehicles
            if (config.pullFromVehicles)
            {
                PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_VEHICLES);
                CountAllVehicleItems(world, playerPos, config);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_VEHICLES);
            }

            // Count from drones
            if (config.pullFromDrones)
            {
                PerformanceProfiler.StartTimer(PerformanceProfiler.OP_COUNT_DRONES);
                CountAllDroneItems(world, playerPos, config);
                PerformanceProfiler.StopTimer(PerformanceProfiler.OP_COUNT_DRONES);
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
    /// </summary>
    private static void AddToCountCache(int itemType, int count)
    {
        if (_itemCountCache.TryGetValue(itemType, out int existing))
            _itemCountCache[itemType] = existing + count;
        else
            _itemCountCache[itemType] = count;
    }

    /// <summary>
    /// Counts all items in a tile entity and adds to cache.
    /// </summary>
    private static void CountTileEntityItems(TileEntity tileEntity, ModConfig config)
    {
        ItemStack[] items = null;

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
            }
        }
        else if (tileEntity is TileEntitySecureLootContainer secureLoot)
        {
            if (secureLoot.IsLocked() && !secureLoot.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                return;

            items = secureLoot.items;
        }

        if (items != null)
        {
            foreach (var stack in items)
            {
                if (stack?.itemValue != null && !stack.IsEmpty())
                    AddToCountCache(stack.itemValue.type, stack.count);
            }
        }
    }

    /// <summary>
    /// Counts all items in nearby vehicles and adds to cache.
    /// Skips the currently open vehicle to avoid stale data issues.
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

                    for (int j = 0; j < slots.Length; j++)
                    {
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
                        for (int j = 0; j < items.Length; j++)
                        {
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

        try
        {
            RefreshStorages(config);

            foreach (var kvp in _currentStorageDict)
            {
                if (remaining <= 0)
                    break;

                try
                {
                    if (kvp.Value is ITileEntityLootable lootable)
                    {
                        var items = lootable.items;
                        if (items == null) continue;

                        for (int i = 0; i < items.Length && remaining > 0; i++)
                        {
                            if (items[i]?.itemValue?.type != item.type)
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
                    else if (kvp.Value is Bag bag)
                    {
                        var slots = bag.GetSlots();
                        if (slots == null) continue;

                        for (int i = 0; i < slots.Length && remaining > 0; i++)
                        {
                            if (slots[i]?.itemValue?.type != item.type)
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
                        var slots = sourceInfo.Items;
                        if (slots == null) continue;

                        for (int i = 0; i < slots.Length && remaining > 0; i++)
                        {
                            if (slots[i]?.itemValue?.type != item.type)
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

                            // Mark the source as modified
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

        public StorageSourceInfo(string sourceType, ItemStack[] items, TileEntity tileEntity)
        {
            SourceType = sourceType;
            Items = items;
            TileEntity = tileEntity;
        }

        public void MarkModified()
        {
            if (TileEntity != null)
            {
                TileEntity.SetModified();
            }
        }
    }

    /// <summary>
    /// Refreshes the list of accessible storage containers near the player.
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

            _currentStorageDict.Clear();
            _knownStorageDict.Clear();

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
    /// Scans world entities for vehicles and drones.
    /// </summary>
    private static void ScanWorldEntities(World world, Vector3 playerPos, ModConfig config)
    {
        var entities = world.Entities?.list;
        if (entities == null) return;

        float rangeSquared = config.range > 0f ? config.range * config.range : 0f;

        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity == null) continue;

            try
            {
                // Range check first for performance (using squared distance to avoid sqrt)
                if (rangeSquared > 0f)
                {
                    float dx = playerPos.x - entity.position.x;
                    float dy = playerPos.y - entity.position.y;
                    float dz = playerPos.z - entity.position.z;
                    if (dx * dx + dy * dy + dz * dz >= rangeSquared)
                        continue;
                }

                // Process vehicles
                if (config.pullFromVehicles && entity is EntityVehicle vehicle)
                {
                    ProcessVehicleEntity(vehicle, playerPos, config);
                }
                // Process drones
                else if (config.pullFromDrones && entity is EntityDrone drone)
                {
                    ProcessDroneEntity(drone, playerPos, config);
                }
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
        _knownStorageDict[vehiclePos] = bag;

        // Already passed range check in ScanWorldEntities, but check again for clarity
        if (config.range <= 0f || Vector3.Distance(playerPos, vehicle.position) < config.range)
        {
            ProxiCraft.LogDebug($"Adding vehicle {((EntityAlive)vehicle).EntityName} at {vehiclePos}");
            _currentStorageDict[vehiclePos] = bag;
        }
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
        Bag droneBag = null;
        if (drone.lootContainer != null)
        {
            // lootContainer is ITileEntityLootable, but we need to get items from it
            var lootItems = drone.lootContainer.items;
            if (lootItems != null && lootItems.Any(i => i != null && !i.IsEmpty()))
            {
                var dronePos = new Vector3i(drone.position);
                _knownStorageDict[dronePos] = drone.lootContainer;
                _currentStorageDict[dronePos] = drone.lootContainer;
                ProxiCraft.LogDebug($"Adding drone lootContainer at {dronePos}");
                return;
            }
        }

        droneBag = drone.bag;
        if (droneBag == null || droneBag.IsEmpty())
            return;

        var pos = new Vector3i(drone.position);
        _knownStorageDict[pos] = droneBag;
        _currentStorageDict[pos] = droneBag;
        ProxiCraft.LogDebug($"Adding drone bag at {pos}");
    }

    private static void ScanChunkTileEntities(Chunk chunk, Vector3 playerPos, ModConfig config)
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

                // Skip if this container is locked by another player in multiplayer
                if (LockedList.Contains(worldPos))
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

        var sourceInfo = new StorageSourceInfo("dew collector", items, dewCollector);
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

        var sourceInfo = new StorageSourceInfo("workstation output", outputItems, workstation);
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
