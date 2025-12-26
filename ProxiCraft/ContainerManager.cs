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
/// - Counting items across all accessible containers
/// - Removing items from containers when crafting
/// - Caching container references for performance
/// - Tracking the currently open container for live item counting
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
    // LIVE OPEN CONTAINER REFERENCE
    // ====================================================================================
    // These are set by LootContainer_OnOpen_Patch and cleared by LootContainer_OnClose_Patch.
    // This gives us direct access to the container's items during UI operations, which is
    // essential for accurate counting when items are being moved in real-time.
    // ====================================================================================
    public static ITileEntityLootable CurrentOpenContainer { get; set; }
    public static Vector3i CurrentOpenContainerPos { get; set; }
    
    /// <summary>
    /// Forces the next container scan to refresh the cache.
    /// Call this when containers may have changed contents.
    /// </summary>
    public static void InvalidateCache()
    {
        _forceCacheRefresh = true;
    }

    /// <summary>
    /// Clears all cached storage references. Call when starting a new game.
    /// </summary>
    public static void ClearCache()
    {
        _knownStorageDict.Clear();
        _currentStorageDict.Clear();
        LockedList.Clear();
        _lastScanTime = 0f;
        _lastScanPosition = Vector3.zero;
        _forceCacheRefresh = true;
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
    /// Uses "one source of truth" - open containers read from cached reference, closed from TileEntity.
    /// </summary>
    public static int GetItemCount(ModConfig config, ItemValue item)
    {
        if (!config.modEnabled || item == null)
            return 0;

        int count = 0;

        try
        {
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (player == null)
                return 0;

            Vector3 playerPos = player.position;
            
            // FIRST: Get count from open container via cached reference (live data)
            Vector3i openContainerPos;
            int openCount = GetOpenContainerItemCount(item, out openContainerPos);
            if (openContainerPos != Vector3i.zero)
            {
                count += openCount;
            }
            
            // Scan closed containers from TileEntities
            for (int clusterIdx = 0; clusterIdx < world.ChunkClusters.Count; clusterIdx++)
            {
                var cluster = world.ChunkClusters[clusterIdx];
                if (cluster == null) continue;

                var chunks = ((WorldChunkCache)cluster).chunks?.dict?.Values?.ToArray();
                if (chunks == null) continue;

                foreach (var chunk in chunks)
                {
                    if (chunk == null) continue;

                    chunk.EnterReadLock();
                    try
                    {
                        var tileEntityKeys = chunk.tileEntities?.dict?.Keys?.ToArray();
                        if (tileEntityKeys == null) continue;

                        foreach (var key in tileEntityKeys)
                        {
                            if (!chunk.tileEntities.dict.TryGetValue(key, out var tileEntity))
                                continue;

                            var worldPos = tileEntity.ToWorldPos();
                            
                            // Skip the open container - we already counted it
                            if (openContainerPos != Vector3i.zero && worldPos == openContainerPos)
                                continue;
                            
                            // Range check
                            if (config.range > 0f && Vector3.Distance(playerPos, (Vector3)worldPos) >= config.range)
                                continue;

                            // Skip locked containers in multiplayer
                            if (LockedList.Contains(worldPos))
                                continue;

                            int containerCount = 0;
                            
                            if (tileEntity is TileEntityComposite composite)
                            {
                                var storage = composite.GetFeature<TEFeatureStorage>();
                                if (storage != null && storage.bPlayerStorage)
                                {
                                    // Check lock
                                    var lockable = composite.GetFeature<ILockable>();
                                    if (lockable != null && lockable.IsLocked() && !config.allowLockedContainers)
                                        continue;
                                    if (lockable != null && lockable.IsLocked() && !lockable.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                                        continue;

                                    // Get items directly from storage feature
                                    var items = storage.items;
                                    if (items != null)
                                    {
                                        containerCount = items
                                            .Where(i => i?.itemValue?.type == item.type)
                                            .Sum(i => i.count);
                                    }
                                }
                            }
                            else if (tileEntity is TileEntitySecureLootContainer secureLoot)
                            {
                                if (secureLoot.IsLocked() && !secureLoot.IsUserAllowed(PlatformManager.InternalLocalUserIdentifier))
                                    continue;

                                var items = secureLoot.items;
                                if (items != null)
                                {
                                    containerCount = items
                                        .Where(i => i?.itemValue?.type == item.type)
                                        .Sum(i => i.count);
                                }
                            }

                            if (containerCount > 0)
                            {
                                count += containerCount;
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
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error counting items: {ex.Message}");
        }

        return count;
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
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogWarning($"Error removing items from container at {kvp.Key}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ProxiCraft.LogError($"Error removing items: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// Refreshes the list of accessible storage containers near the player.
    /// </summary>
    private static void RefreshStorages(ModConfig config)
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
        bool shouldRescan = 
            _forceCacheRefresh ||
            containerOpenByPlayer ||  // Always refresh when player has a container open
            currentTime - _lastScanTime > SCAN_COOLDOWN ||
            Vector3.Distance(playerPos, _lastScanPosition) > POSITION_CHANGE_THRESHOLD;

        if (!shouldRescan && _currentStorageDict.Count > 0)
            return;

        _forceCacheRefresh = false;
        _lastScanTime = currentTime;
        _lastScanPosition = playerPos;

        _currentStorageDict.Clear();
        _knownStorageDict.Clear();

        try
        {
            // Scan all chunk clusters
            for (int clusterIdx = 0; clusterIdx < world.ChunkClusters.Count; clusterIdx++)
            {
                var cluster = world.ChunkClusters[clusterIdx];
                if (cluster == null) continue;

                var chunks = ((WorldChunkCache)cluster).chunks?.dict?.Values?.ToArray();
                if (chunks == null) continue;

                foreach (var chunk in chunks)
                {
                    if (chunk == null) continue;

                    chunk.EnterReadLock();
                    try
                    {
                        // Scan vehicles if enabled
                        if (config.enableFromVehicles)
                        {
                            ScanChunkVehicles(chunk, playerPos, config);
                        }

                        // Scan tile entities (containers)
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
    }

    private static void ScanChunkVehicles(Chunk chunk, Vector3 playerPos, ModConfig config)
    {
        if (chunk.entityLists == null) return;

        foreach (var entityList in chunk.entityLists)
        {
            if (entityList == null) continue;

            foreach (var entity in entityList)
            {
                try
                {
                    if (!(entity is EntityVehicle vehicle))
                        continue;

                    // Only include vehicles owned by the local player
                    if (!vehicle.LocalPlayerIsOwner())
                        continue;

                    var bag = ((EntityAlive)vehicle).bag;
                    if (bag == null)
                        continue;

                    var vehiclePos = new Vector3i(vehicle.position);
                    _knownStorageDict[vehiclePos] = bag;

                    // Check range
                    if (config.range <= 0f || Vector3.Distance(playerPos, vehicle.position) < config.range)
                    {
                        ProxiCraft.LogDebug($"Adding vehicle {((EntityAlive)vehicle).EntityName} at {vehiclePos}");
                        _currentStorageDict[vehiclePos] = bag;
                    }
                }
                catch (Exception ex)
                {
                    ProxiCraft.LogWarning($"Error scanning vehicle: {ex.Message}");
                }
            }
        }
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

                var worldPos = tileEntity.ToWorldPos();

                // Skip if this container is locked by another player in multiplayer
                if (LockedList.Contains(worldPos))
                    continue;

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

        // Check range
        if (config.range <= 0f || Vector3.Distance(playerPos, (Vector3)worldPos) < config.range)
        {
            _currentStorageDict[worldPos] = storage;
        }
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

        // Check range
        if (config.range <= 0f || Vector3.Distance(playerPos, (Vector3)worldPos) < config.range)
        {
            _currentStorageDict[worldPos] = secureLoot;
        }
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
