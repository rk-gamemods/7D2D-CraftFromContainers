# BeyondStorage2 Feature Integration Plan

This document outlines the plan to integrate features from BeyondStorage2 into ProxiCraft while maintaining our stability-focused architecture.

## Reference

- **Source**: [BeyondStorage2 on GitHub](https://github.com/superguru/7d2d_mod_BeyondStorage2)
- **License**: MIT (compatible with ProxiCraft's MIT license)
- **Credits**: superguru, gazorper, unv_Annihilator, aedenthorn

## Current ProxiCraft Features

| Feature | Status |
|---------|--------|
| Crafting from containers | ✅ |
| Reloading weapons | ✅ |
| Refueling vehicles | ✅ |
| Block repair/upgrade | ✅ |
| Trader purchases | ✅ |
| Challenge tracker integration | ✅ |
| Vehicle storage | ✅ |

## Newly Integrated Features (from BeyondStorage2)

| Feature | Status | Implementation |
|---------|--------|----------------|
| Drone storage | ✅ Implemented | ContainerManager.ProcessDroneEntity() |
| Dew collector contents | ✅ Implemented | ContainerManager.ProcessDewCollector() |
| Workstation outputs | ✅ Implemented | ContainerManager.ProcessWorkstationOutput() |
| Painting from containers | ✅ Implemented | ItemActionTextureBlock PREFIX patches |
| Lockpicking from containers | ✅ Covered | Via existing XUiM_PlayerInventory patches |
| Generator refuel | ✅ Implemented | XUiC_PowerSourceStats TRANSPILER patch |
| Item repair (weapons/tools) | ✅ Covered | Via existing XUiM_PlayerInventory patches |

## Features To Add (Original Plan)

### Phase 1: Expand Storage Sources (No New Patches Required)

These features only require expanding `ContainerManager.cs` to find additional storage types.

#### 1.1 Drone Storage (`pullFromDrones`)

**What**: Include player's drone storage in the search for items.

**Implementation**:
- Iterate `GameManager.Instance.World.Entities.list`
- Find entities of type `EntityDrone`
- Check ownership: `drone.IsOwner(PlatformManager.InternalLocalUserIdentifier)`
- Check accessibility: not `isInteractionLocked`, not `isShutdownPending`, not `isShutdown`
- Access items via `drone.bag` or `drone.lootContainer`

**Reference**: `BeyondStorage2/Source/Storage/EntityItemDiscovery.cs` lines 159-235

#### 1.2 Dew Collector Contents (`pullFromDewCollectors`)

**What**: Use water/items from dew collectors for crafting.

**Implementation**:
- During chunk/tile entity iteration, find `TileEntityCollector`
- Check not `bUserAccessing` (someone else using it)
- Access items via `dewCollector.items` array
- Mark modified via `SetChunkModified()` and `SetModified()` after removal

**Reference**: `BeyondStorage2/Source/Storage/TileEntityItemDiscovery.cs` lines 121-170

#### 1.3 Workstation Outputs (`pullFromWorkstationOutputs`)

**What**: Use finished items from forges, campfires, cement mixers, etc.

**Implementation**:
- During chunk/tile entity iteration, find `TileEntityWorkstation`
- Check `IsPlayerPlaced` (only player-built workstations)
- Access OUTPUT items via `workstation.output` array (not input slots!)
- Mark modified via `SetChunkModified()` and `SetModified()` after removal

**Reference**: `BeyondStorage2/Source/Storage/TileEntityItemDiscovery.cs` lines 174-225

---

### Phase 2: New Patch Features

These features require new Harmony patches but can use our postfix/prefix approach.

#### 2.1 Painting from Containers (`enableForPainting`)

**What**: Use paint from storage when using the paint brush tool.

**Target Methods**:
- `ItemActionTextureBlock.checkAmmo` - Check if paint is available
- `ItemActionTextureBlock.decreaseAmmo` - Remove paint after use

**Implementation Approach**: **PREFIX** (same as BeyondStorage2)
- Prefix `checkAmmo`: Return true if storage has paint, skip original
- Prefix `decreaseAmmo`: Remove from inventory first, then storage for remainder

**Reference**: `BeyondStorage2/Source/HarmonyPatches/Item/Texture/ItemActionTextureBlock_Patches.cs`

**Why PREFIX works**: These methods have simple logic we can fully replace.

#### 2.2 Lockpicking from Containers (`enableForLockpicking`)

**What**: Use lockpicks from storage to pick locks.

**Target Methods**:
- `BlockSecureLoot.OnBlockActivated`
- `BlockSecureLootSigned.OnBlockActivated`
- `TEFeatureLockPickable` methods

**Implementation Approach**: **POSTFIX on GetItemCount**
- We already patch `XUiM_PlayerInventory.GetItemCount`
- Our existing infrastructure should make lockpicks "visible" to the lockpicking system
- May need additional patches if the lockpicking code uses different inventory methods

**Alternative**: Prefix that checks storage for lockpicks and handles consumption

**Reference**: `BeyondStorage2/Source/HarmonyPatches/Functions/BlockSecureLoot_Lockpick_Patches.cs`

#### 2.3 Generator/Power Source Refuel (`enableForGeneratorRefuel`)

**What**: Refuel generators using gas cans from storage.

**Target Method**: `XUiC_PowerSourceStats.BtnRefuel_OnPress`

**Implementation Approach**: **POSTFIX**
- Original method calls `Bag.DecItem` to remove fuel
- Postfix: Check if removal was incomplete, remove remainder from storage
- Pattern: `int remaining = neededAmount - removedFromBag; RemoveFromStorage(remaining);`

**Reference**: `BeyondStorage2/Source/HarmonyPatches/PowerSource/Refuel/XUiC_PowerSourceStats_Patches.cs`

**Why POSTFIX works**: We add behavior after the original, removing from storage what the inventory couldn't provide.

#### 2.4 Item Repair - Weapons/Tools (`enableForItemRepair`)

**What**: Repair weapons and tools using repair kits from storage.

**Target Methods**:
- `ItemActionEntryRepair.OnActivated` - Perform the repair
- `ItemActionEntryRepair.RefreshEnabled` - Show repair button as enabled

**Implementation Approach**: **POSTFIX on GetItemCount + PREFIX on removal**
- `RefreshEnabled`: Our existing GetItemCount patches should work
- `OnActivated`: May need prefix to handle the full removal flow

**Reference**: `BeyondStorage2/Source/HarmonyPatches/Item/Repair/ItemActionEntryRepair_Patches.cs`

---

### Phase 3: Config and Polish

#### 3.1 New Config Options

Add to `ModConfig.cs`:
```csharp
// New storage sources
public bool pullFromDrones = true;
public bool pullFromDewCollectors = true;
public bool pullFromWorkstationOutputs = true;

// New features
public bool enableForPainting = true;
public bool enableForLockpicking = true;
public bool enableForGeneratorRefuel = true;
public bool enableForItemRepair = true;
```

#### 3.2 Update Documentation

- Update `NEXUS_DESCRIPTION.txt` with new features
- Update `README.md` with new config options
- Add credits for BeyondStorage2 inspiration

#### 3.3 Console Command Updates

Update `pc status` and `pc diag` to show new feature states.

---

## Implementation Order

1. **Config First**: Add all new config options (disabled by default during dev)
2. **Storage Sources**: Expand ContainerManager for drones, dew collectors, workstations
3. **Painting**: Implement prefix patches (simplest new patch)
4. **Generator Refuel**: Implement postfix pattern
5. **Lockpicking**: Test if existing patches work, add specific patches if needed
6. **Item Repair**: Implement remaining patches
7. **Testing**: Verify all features work together
8. **Documentation**: Update all docs and release notes

---

## Architecture Principles

### Why We Avoid Transpilers

BeyondStorage2 uses many transpilers (IL code injection). We prefer:

1. **Prefix patches**: Intercept before method runs, optionally skip original
2. **Postfix patches**: Add behavior after method completes, modify `__result`
3. **Priority.Low**: Run after other mods to avoid conflicts

### Benefits of Our Approach

| Aspect | Transpiler | Prefix/Postfix |
|--------|------------|----------------|
| Stability | Fragile - breaks on game updates | Robust - survives minor changes |
| Compatibility | High conflict risk | Low conflict risk |
| Debugging | Hard to trace | Easy to trace |
| Maintenance | Requires IL knowledge | Standard C# |

### When Transpilers Are Necessary

Only when you MUST inject code in the MIDDLE of a method and:
- Cannot use prefix to replace the method
- Cannot use postfix to clean up after
- Cannot patch the underlying methods being called

For our features, **none require transpilers**.

---

## Testing Checklist

### New Storage Sources

#### Drone Storage (`pullFromDrones`)
- [ ] Place items in player's drone storage
- [ ] Items appear in crafting recipe counts
- [ ] Items can be used for crafting (consumed from drone)
- [ ] Only player's own drones are accessed
- [ ] Drones in shutdown/locked state are skipped

#### Dew Collector Contents (`pullFromDewCollectors`)
- [ ] Place/wait for water in dew collector
- [ ] Water appears in crafting recipe counts
- [ ] Water can be consumed for recipes (murky water, etc.)
- [ ] Collector contents persist correctly after use

#### Workstation Outputs (`pullFromWorkstationOutputs`)
- [ ] Smelt iron in forge, leave in output slot
- [ ] Iron appears in crafting recipe counts
- [ ] Iron can be used for crafting directly from forge output
- [ ] Only OUTPUT slots are used (not fuel/input)
- [ ] Works with: Forge, Campfire, Chemistry Station, Cement Mixer

### New Feature Patches

#### Painting (`enableForPainting`)
- [ ] Put paint cans in nearby container
- [ ] Equip paint brush (no paint in inventory)
- [ ] Paint tool shows available (checks containers)
- [ ] Successfully paint a block (consumes from container)
- [ ] Works in: Single paint, Paint all faces, Flood fill modes

#### Lockpicking (`enableForLockpicking`)
- [ ] Put lockpicks in nearby container
- [ ] Find a locked container/safe (no lockpicks in inventory)
- [ ] "Pick Lock" option available
- [ ] Successfully pick lock (consumes lockpick from container)

#### Generator Refuel (`enableForGeneratorRefuel`)
- [ ] Put gas cans in nearby container
- [ ] Place generator and connect power (no gas in inventory)
- [ ] Open generator UI
- [ ] "Refuel" button works
- [ ] Gas consumed from container

#### Item Repair (`enableForItemRepair`)
- [ ] Put repair kits in nearby container
- [ ] Equip damaged weapon/tool (no repair kits in inventory)
- [ ] Open item radial menu
- [ ] "Repair" option available and works
- [ ] Repair kit consumed from container

### Regression Tests (Existing Features)

- [ ] Crafting from containers still works
- [ ] Weapon reload from containers still works
- [ ] Vehicle refuel from containers still works
- [ ] Block repair/upgrade from containers still works
- [ ] Trader purchases with dukes from containers still works
- [ ] Challenge tracker counts items in containers

### Edge Cases

- [ ] Range limit respected for new storage sources
- [ ] Locked containers respected (`allowLockedContainers` setting)
- [ ] Multiplayer: Other players' containers not accessed
- [ ] Performance: No noticeable lag with many drones/workstations
- [ ] Config toggle: Each feature can be disabled independently

### Config File

- [ ] New options appear in config.json after update
- [ ] Default values are all `true`
- [ ] Setting to `false` disables the feature
- [ ] Old configs (without new options) work with defaults

---

## Credits

This integration is based on analysis of BeyondStorage2, which traces its lineage:
- **superguru**: 7D2D v2 refactor and current maintainer
- **gazorper**: Beyond Storage 2 mod page
- **unv_Annihilator**: 7D2D v1 fork
- **aedenthorn**: Original CraftFromContainers concept

All code will be reimplemented using ProxiCraft's architecture, but the feature ideas and game API knowledge come from studying their work.
