# 7 Days to Die Modding - Technical Reference

This document contains verified technical details discovered during ProxiCraft mod development.

## Game Paths

### Log File Location
```
C:\Users\Admin\AppData\Local\Temp\The Fun Pimps\7 Days To Die\Player.log
```

### Game Installation
```
C:\Steam\steamapps\common\7 Days To Die\
```

### Managed Assemblies
```
C:\Steam\steamapps\common\7 Days To Die\7DaysToDie_Data\Managed\
```
- `Assembly-CSharp.dll` - Main game code
- `0Harmony.dll` - Located in `Mods\0_TFP_Harmony\`

### XUi Configuration Files
```
C:\Steam\steamapps\common\7 Days To Die\Data\Config\XUi\
├── controls.xml    # UI control definitions and bindings
├── styles.xml      # UI styles
├── windows.xml     # Window layouts
└── xui.xml         # Window groups
```

---

## Harmony Patching

### Parameter Name Matching
**CRITICAL**: Harmony Postfix/Prefix parameter names must match the original method's parameter names exactly.

Example from `XUiController.GetBindingValue`:
- Public method uses: `_value`, `_bindingName`  
- Internal method uses: `value`, `bindingName`

Wrong parameter names cause: `HarmonyException: Parameter "X" not found in method`

### Patch Priority
Use `[HarmonyPriority(Priority.Low)]` to run after other mods.

### Dynamic Method Targeting
For types not directly accessible, use `[HarmonyPatch]` with `TargetMethod()`:
```csharp
[HarmonyPatch]
private static class MyPatch
{
    static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("Namespace.ClassName");
        return type?.GetMethod("MethodName");
    }
    
    public static void Postfix(...) { }
}
```

---

## XUi Binding System

### How Bindings Work
1. XML defines bindings with `{bindingname}` syntax
2. Controller class implements `GetBindingValueInternal(ref string value, string bindingName)`
3. Method sets `value` and returns `true` if binding handled

### Quest Tracker Bindings (XUiC_QuestTrackerObjectiveEntry)
Found in `controls.xml` line 758:
- `{objectiveoptional}` - Optional marker
- `{objectivephasehexcolor}` - Phase color
- `{objectivedescription}` - Description text
- `{objectivecompletehexcolor}` - Completion color
- `{objectivestate}` - Status text (e.g., "5/10")

### Challenge Entry Bindings (XUiC_ChallengeEntry)
- Uses `{entrydescription}` for objective display
- Controller: `ChallengeEntry`

---

## Quest System vs Challenge System

### Quest Objectives (Traditional Quests)
**Namespace**: Root namespace  
**Base Class**: `BaseObjective`

Key Classes:
- `ObjectiveFetch` - Fetch items from inventory
- `ObjectiveBaseFetchContainer` - Fetch from containers
- `ObjectiveCraft` - Craft items
- `ObjectiveGoto` - Go to location

`ObjectiveFetch` Fields:
- `expectedItem` (ItemValue)
- `expectedItemClass` (ItemClass)
- `currentCount` (int)
- `itemCount` (int)

### Challenge Objectives (Challenge System)
**Namespace**: `Challenges`  
**Base Class**: `Challenges.BaseChallengeObjective`

### Class Hierarchy
```
System.Object
  └── Challenges.BaseChallengeObjective
        └── Challenges.ChallengeBaseTrackedItemObjective
              └── Challenges.ChallengeObjectiveGather
                    └── (GatherByTag, GatherIngredient, Harvest, HarvestByTag)
```

### Key Classes
- `Challenges.ChallengeObjectiveGather` - Gather items
- `Challenges.ChallengeObjectiveGatherByTag` - Gather by tag
- `Challenges.ChallengeObjectiveCraft` - Craft items
- `Challenges.ChallengeObjectiveHarvest` - Harvest resources

### `BaseChallengeObjective` Fields (inherited)
- `current` (int) - Current count
- `MaxCount` (int) - Required count
- `complete` (bool) - Whether objective is complete
- `Owner` (Challenge) - Parent challenge
- `ValueChanged` (ObjectiveValueChanged) - Event for UI updates

### `ChallengeBaseTrackedItemObjective` Fields (inherited)
- `expectedItem` (ItemValue)
- `expectedItemClass` (ItemClass)

### `ChallengeObjectiveGather` Methods
- `get_DescriptionText` - Returns formatted string like "Gather Wood (5/10)"
- `CheckObjectiveComplete(bool)` - Returns bool if objective met
- `HandleUpdatingCurrent()` - **Updates `current` field from inventory** (KEY!)
- `ItemsChangedInternal()` - Called when inventory changes
- `CheckForNeededItem()` - Queries Player.bag/inventory directly
- `UpdateStatus()` - Refreshes status
- `HandleAddHooks()` - Subscribes to inventory change events

### Challenge Item Count Flow (CRITICAL)
```
Player picks up item
    ↓
Inventory.Changed() fires
    ↓
Challenge hooks receive event
    ↓
ChallengeObjectiveGather.ItemsChangedInternal()
    ↓
HandleUpdatingCurrent() queries Player.bag.GetItemCount() ← DIRECT ACCESS
    ↓
`current` field is SET from bag count
    ↓
UI calls get_DescriptionText()
```

**KEY INSIGHT**: Challenges use `Player.bag.GetItemCount()` and `Player.inventory.GetItemCount()` DIRECTLY, NOT through `XUiM_PlayerInventory`. To modify challenge counts, must patch at the bag/inventory level OR patch `HandleUpdatingCurrent()`.

---

## Inventory System

### Low-Level Inventory Classes
**IMPORTANT**: Many game systems query these directly, bypassing XUiM_PlayerInventory.

**Bag Class** (backpack):
- `Bag.GetItemCount(ItemValue, int, int, bool)` - Count items in bag
- `Bag.GetItemCountByTag(FastTags<TagGroup.Global>)` - Count by tag

**Inventory Class** (toolbelt):
- `Inventory.GetItemCount(ItemValue, bool, int, int, bool)` - Count in toolbelt
- `Inventory.GetItemCountByTag(FastTags<TagGroup.Global>)` - Count by tag

### XUiM_PlayerInventory Methods (UI Layer)
- `GetItemCount(ItemValue)` - Returns count of specific item
- `GetItemCount(int itemId)` - By item ID
- `GetAllItemStacks()` - Returns List<ItemStack> of all items
- `HasItems(IList<ItemStack>, int multiplier)` - Checks if player has items
- `RemoveItems(IList<ItemStack>, int multiplier)` - Removes items from inventory

### Which Systems Use Which Layer
| System | Uses | Patch Point |
|--------|------|-------------|
| Crafting UI | XUiM_PlayerInventory | ✅ Currently patched |
| Recipe availability | XUiC_RecipeList | ✅ Currently patched |
| Challenge objectives | Player.bag/inventory directly | ✅ Patched (Fix #8e via events) |
| Quest objectives | BaseObjective internal methods | ❌ May need patch |

### Patching GetItemCount
Direct Postfix on `XUiM_PlayerInventory.GetItemCount` works reliably for:
- Crafting ingredient display
- Recipe availability checks

---

## Container System

### Container Types
- `TileEntitySecureLootContainer` - Standard storage containers (legacy)
- `TileEntityComposite` - Composite containers with TEFeatureStorage (newer system)
- `TileEntityCollector` - Dew collectors (water collection)
- `TileEntityWorkstation` - Forges, workbenches, campfires, chemistry stations

### Vehicle Storage
Vehicles store items in their `bag` property (inherited from `EntityAlive`):
```csharp
EntityVehicle vehicle = ...;
var bag = ((EntityAlive)vehicle).bag;
var slots = bag.GetSlots(); // ItemStack[]
```

### Drone Storage
**CRITICAL:** Drones have a shared array between `lootContainer` and `bag`:
```csharp
EntityDrone drone = ...;
// These share the SAME ItemStack[] array!
drone.lootContainer.items == drone.bag.GetSlots(); // TRUE!
```
Always count from ONE source only (lootContainer recommended) to avoid double-counting.

### Workstation Slots
Workstations have multiple slot types - only OUTPUT should be counted:
```csharp
TileEntityWorkstation workstation = ...;
workstation.Tool    // Tool slots (anvil, beaker) - DO NOT COUNT
workstation.Input   // Input slots (ore, materials being processed) - DO NOT COUNT  
workstation.Fuel    // Fuel slots (wood, coal being burned) - DO NOT COUNT
workstation.Output  // Output slots (finished products) - COUNT THIS ONLY
```

### Container Access
Containers are accessed via chunk-based TileEntity queries through `GameManager.Instance.World`.

---

## UI Controllers

### Quest UI Hierarchy
```
XUiC_QuestTrackerWindow
└── XUiC_QuestTrackerObjectiveList
    └── XUiC_QuestTrackerObjectiveEntry (per objective)
        - questObjective (BaseObjective)
        - challengeObjective (Challenges.BaseChallengeObjective)
```

### Challenge UI Hierarchy
```
XUiC_ChallengeEntryListWindow
└── XUiC_ChallengeGroupList
    └── XUiC_ChallengeGroupEntry
        └── XUiC_ChallengeEntryList
            └── XUiC_ChallengeEntry (per challenge)
```

---

## Verified Working Patches

### Crafting - Item Count Display
```csharp
[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) })]
```
- Postfix adds container counts to return value
- Works for ingredient display in crafting UI

### Crafting - Recipe Availability  
```csharp
[HarmonyPatch(typeof(XUiC_RecipeList), "BuildRecipeInfosList")]
```
- **Must use Prefix** (not Postfix) to add items before calculation
- Parameter: `ref List<ItemStack> _items`

### Crafting - Has Items Check
```csharp
[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.HasItems))]
```
- Postfix checks containers when inventory returns false

---

## Assembly Inspection

### PowerShell: Load and Inspect Assembly
```powershell
$assembly = [System.Reflection.Assembly]::LoadFrom("path\to\Assembly-CSharp.dll")
$type = $assembly.GetType("ClassName")
$type.GetMethods() | Select-Object Name
$type.GetFields([System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic) | Select-Object Name, FieldType
```

### PowerShell: Search for Types
```powershell
$assembly.GetTypes() | Where-Object { $_.Name -like "*Pattern*" } | Select-Object Name, FullName
```

---

## Build Configuration

### Project Output
```xml
<OutputPath>Release\ProxiCraft\</OutputPath>
```
Builds directly to distribution folder.

### Target Framework
```xml
<TargetFramework>net48</TargetFramework>
```

---

## Debugging Tips

1. **Check log for Harmony exceptions** - Search for `Exception from user`
2. **Parameter mismatch errors** - Verify parameter names match original method exactly
3. **Patch not firing** - Verify method signature, check if method exists on target type
4. **Use Log() not LogDebug()** - For guaranteed output during debugging

---

## Performance: Item Count Caching Strategy

### The Problem
When checking recipe availability, the game calls `GetItemCount()` for each ingredient (potentially 10-20 items). Without caching, each call would scan:
- All chunks and TileEntities (storage containers)
- All world entities (vehicles, drones)
- All TileEntityCollectors (dew collectors)  
- All TileEntityWorkstations (forge/workbench outputs)

With 100+ storage locations, this could be 100-400ms per UI update = LAG SPIKE.

### The Solution: Multi-Level Caching

```
┌─────────────────────────────────────────────────────────────────┐
│                    GetItemCount() Called                        │
└─────────────────────────────────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  LEVEL 1: Same Frame Check                                      │
│  if (frameCount == lastCacheFrame) → return cached value        │
│  Purpose: Multiple ingredients in same update use same cache    │
└─────────────────────────────────────────────────────────────────┘
                               │ miss
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  LEVEL 2: Time-Based Cache Check                                │
│  if (timeSinceCache < 100ms && cacheValid) → return cached      │
│  Purpose: Rapid updates within 100ms reuse scan results         │
└─────────────────────────────────────────────────────────────────┘
                               │ miss
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  LEVEL 3: Full Rebuild                                          │
│  Scan ALL storage sources ONCE, cache ALL item types            │
│  Single pass: O(storage_count) instead of O(storage × items)    │
└─────────────────────────────────────────────────────────────────┘
```

### Cache Invalidation Points

| Event | Action | Why |
|-------|--------|-----|
| Container Opened | `InvalidateCache()` | Open container needs live reference |
| Container Closed | `InvalidateCache()` | Need to count from TileEntity now |
| Container Slot Changed | `InvalidateCache()` | Items moved in/out |
| Items Removed (crafting) | `InvalidateCache()` | Storage contents changed |
| Cache Duration Expired | Auto-rebuild on next call | Ensures eventual consistency |

### Performance Expectations

| Scenario | Without Caching | With Caching |
|----------|-----------------|--------------|
| Single ingredient check | ~5-20ms | ~5-20ms (first call) |
| 20 ingredients, same frame | ~100-400ms | ~5-20ms (cached) |
| Rapid UI updates (100ms) | Repeated full scans | Single scan, cached |
| Moving items in container | N/A | Auto-invalidates |

### Why This Is Safe for Challenge Tracking

1. **Event-Driven Updates**: Challenges fire `HandleUpdatingCurrent` when inventory changes
2. **Container Changes Invalidate**: Our patches on `HandleLootSlotChangedEvent` invalidate cache
3. **Short Cache Duration**: 100ms max means any "stuck" value resolves quickly
4. **Per-Frame Caching**: Within a single update frame, cache is consistent

### Key Code Locations

- Cache definitions: `ContainerManager.cs` lines 65-90
- GetItemCount with caching: `ContainerManager.cs` lines 254-300
- RebuildItemCountCache: `ContainerManager.cs` lines 304-420
- InvalidateCache calls: `ProxiCraft.cs` LootContainer patches

---

## Performance Profiler

### Overview

ProxiCraft includes a built-in performance profiler (`PerformanceProfiler.cs`) that tracks timing and cache statistics for key operations. Disabled by default to avoid any overhead.

### Tracked Operations

| Operation | Description |
|-----------|-------------|
| `RebuildItemCountCache` | Full storage scan to rebuild item cache |
| `GetItemCount` | Individual item count queries |
| `RefreshStorages` | Container discovery scan |
| `CountVehicles` | Vehicle storage enumeration |
| `CountDrones` | Drone storage enumeration |
| `CountDewCollectors` | Dew collector scanning |
| `CountWorkstations` | Workstation output scanning |

### Metrics Collected

- **Call Count** - How many times operation was called
- **Total Time** - Cumulative milliseconds
- **Avg Time** - Average ms per call
- **Min/Max Time** - Range of execution times
- **Cache Hit Rate** - Percentage of queries served from cache

### Console Commands

```
pc perf          - Brief status (is profiler on, basic stats)
pc perf on       - Enable profiling (resets data)
pc perf off      - Disable profiling
pc perf reset    - Clear collected data
pc perf report   - Detailed timing report
```

### Performance Optimizations Applied

1. **Squared Distance Comparison** - Avoids expensive `sqrt()` in distance checks
   ```csharp
   // Before (expensive):
   if (Vector3.Distance(a, b) >= range)
   
   // After (fast):
   float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
   if (dx*dx + dy*dy + dz*dz >= rangeSquared)
   ```

2. **Direct Dictionary Iteration** - Avoids allocation from `.ToArray()`
   ```csharp
   // Before (allocates array):
   foreach (var chunk in dict.Values.ToArray())
   
   // After (zero allocation):
   foreach (var kvp in dict)
   ```

3. **For Loops on Arrays** - Avoids enumerator allocation
   ```csharp
   // Before (allocates enumerator):
   foreach (var item in array)
   
   // After (zero allocation):
   for (int i = 0; i < array.Length; i++)
   ```

4. **Pre-computed Constants** - Calculate once, use many times
   ```csharp
   float rangeSquared = range * range; // Once before loop
   ```

### Real-World Performance Test Results

**Test Environment:**
- CPU: AMD Ryzen 9800X3D (high-end reference)
- Storage sources: ~20 containers of mixed types (chests, vehicles, drones, workstations, dew collectors)
- Game version: 7D2D V2.5

**Results:**
```
╔══════════════════════════════════════════════════════════════════╗
║ Operation              │ Calls │ Avg(ms) │ Max(ms) │ Cache Hit % ║
╟────────────────────────┼───────┼─────────┼─────────┼─────────────╢
║ GetItemCount           │  6627 │    0.00 │    0.30 │       98.7% ║
║ RebuildItemCountCache  │    86 │    0.15 │    0.22 │         N/A ║
║ CountContainers        │    86 │    0.09 │    0.14 │         N/A ║
║ RefreshStorages        │     8 │    0.44 │    2.39 │         N/A ║
║ CountDewCollectors     │    86 │    0.03 │    0.04 │         N/A ║
║ CountWorkstations      │    86 │    0.03 │    0.03 │         N/A ║
║ CountVehicles          │   172 │    0.01 │    0.01 │         N/A ║
║ CountDrones            │   172 │    0.00 │    0.00 │         N/A ║
╚══════════════════════════════════════════════════════════════════╝
```

**Analysis:**
- **6,627 item queries** handled with 98.7% cache hit rate
- **GetItemCount** averages 0.00ms (sub-microsecond when cached)
- **RefreshStorages max 2.39ms** is the cold-cache first scan; subsequent calls average 0.44ms
- **Full cache rebuild** takes only 0.15ms average

**Marginal Overhead Assessment:**
- ProxiCraft adds minimal overhead to whatever frame budget remains after base game + other mods
- Worst-case 2.39ms spike happens once (cold cache), then operations stay under 0.5ms
- Cache efficiency (98.7%) means most item queries add essentially zero overhead
- The optimizations (squared distance, no allocations) keep per-operation costs minimal
- Test hardware (9800X3D) is high-end; results on slower systems will vary, but the relative efficiency should hold

---

## Patch Strategy Summary

### The Problem with UI-Layer Patches
Patching only the UI layer (e.g., `XUiM_PlayerInventory`) doesn't work for systems that bypass it. The Challenge system directly queries `Player.bag` and `Player.inventory`, never going through `XUiM_PlayerInventory`.

### Recommended Patch Points by Feature

| Feature | Patch Target | Why |
|---------|--------------|-----|
| Crafting ingredient display | `XUiM_PlayerInventory.GetItemCount` | UI layer works here |
| Recipe availability | `XUiC_RecipeList.BuildRecipeInfosList` | Intercept before calculation |
| Challenge gather objectives | `ChallengeObjectiveGather.HandleUpdatingCurrent` | Modify `current` field after bag query |
| Quest fetch objectives | `ObjectiveFetch` internal methods | Direct item queries |

### The "Trick the game" approach
Instead of patching every consumer, patch at the source:
- `Bag.GetItemCount` - Would affect ALL bag queries
- `Inventory.GetItemCount` - Would affect ALL inventory queries

**Caution**: Low-level patches are more invasive and may have unintended side effects (item removal, duplication, etc.)
