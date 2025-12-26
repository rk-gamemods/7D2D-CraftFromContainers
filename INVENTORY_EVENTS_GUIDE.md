# 7 Days to Die - Inventory Events & Management Guide

## Overview

This guide documents the inventory event system in 7 Days to Die (V1.0+), based on extensive reverse engineering and testing. It covers how items are tracked, what events fire when items move, and best practices for modding inventory-related systems.

---

## Table of Contents

1. [Inventory Architecture](#inventory-architecture)
2. [Event System](#event-system)
3. [Item Movement Flows](#item-movement-flows)
4. [Container Management](#container-management)
5. [Challenge/Quest Integration](#challengequest-integration)
6. [Modding Best Practices](#modding-best-practices)
7. [Common Pitfalls](#common-pitfalls)
8. [Key Classes Reference](#key-classes-reference)

---

## Inventory Architecture

### Player Inventory Components

```
EntityPlayerLocal
├── bag (Bag)                    // Player backpack (main inventory)
│   └── GetSlots() → ItemStack[]
├── inventory (Inventory)        // Player toolbelt (hotbar)
│   └── GetSlots() → ItemStack[]
├── equipment (Equipment)        // Worn equipment
└── dragAndDropItem (ItemStack)  // Currently held on cursor
```

### UI Components

```
LocalPlayerUI.primaryUI
├── xui (XUi)
│   ├── PlayerInventory (XUiM_PlayerInventory)
│   │   ├── Backpack → player.bag
│   │   └── Toolbelt → player.inventory
│   ├── lootContainer (ITileEntityLootable)  // Set when container UI open
│   └── windowManager (GUIWindowManager)
└── activeItemStackGrids (List<XUiC_ItemStackGrid>)  // NOT RELIABLE!
```

### Storage Containers

```
TileEntity (world block)
├── TileEntityLootContainer      // Simple loot containers
└── TileEntityComposite          // Player-placed storage
    └── TEFeatureStorage         // Storage feature component
        └── items[] (ItemStack[])
```

**CRITICAL:** `ITileEntityLootable.items[]` contains the actual item data, but may be STALE during UI operations (see [Container Management](#container-management)).

---

## Event System

### Inventory Change Events

| Event | Fires When | Subscribers |
|-------|-----------|-------------|
| `Bag.OnBackpackItemsChangedInternal` | Backpack contents change | Challenges, UI |
| `Inventory.OnToolbeltItemsChangedInternal` | Toolbelt contents change | Challenges, UI |
| `EntityPlayerLocal.DragAndDropItemChanged` | Item picked up/placed from cursor | Challenges, UI |
| `Equipment.OnChanged` | Equipment slot changes | UI |

### How Challenges Subscribe (ChallengeObjectiveGather.HandleAddHooks)

```csharp
playerInventory.Backpack.OnBackpackItemsChangedInternal += ItemsChangedInternal;
playerInventory.Toolbelt.OnToolbeltItemsChangedInternal += ItemsChangedInternal;
player.DragAndDropItemChanged += ItemsChangedInternal;
```

**KEY INSIGHT:** Challenges do NOT listen to container events! They only track backpack, toolbelt, and cursor changes.

### UI Slot Change Events

| Event | Location | Purpose |
|-------|----------|---------|
| `XUiC_ItemStack.SlotChangedEvent` | Any item grid slot | Notifies parent grid of changes |
| `XUiC_LootContainer.HandleLootSlotChangedEvent` | Container grid | Updates TileEntity when slot changes |

---

## Item Movement Flows

### Shift+Click (Fast Transfer)

```
User shift+clicks item in backpack
    ↓
XUiC_ItemStack detects click
    ↓
Item removed from source slot → DragAndDropItemChanged fires
    ↓
Item placed in destination slot
    ↓
OnBackpackItemsChangedInternal fires (if backpack involved)
    ↓
Challenges recount via ItemsChangedInternal()
```

### Manual Drag & Drop

```
User clicks item (picks up)
    ↓
DragAndDropItemChanged fires (item on cursor)
    ↓
User clicks destination slot
    ↓
HandleSlotChangeEvent fires on source slot
    ↓
HandleSlotChangeEvent fires on destination slot
    ↓
If container: HandleLootSlotChangedEvent fires
    ↓
TileEntity.UpdateSlot() called
```

### Split Stack (Right-Click Drag)

```
User right-clicks stack (picks up half)
    ↓
DragAndDropItemChanged fires
    ↓
Source slot updated (remaining items)
    ↓
User places in destination
    ↓
HandleSlotChangeEvent fires
```

---

## Container Management

### Container Open/Close Lifecycle

```csharp
// Opening a container:
XUiC_LootContainer.OnOpen()
    → localTileEntity.listeners.Add(this)
    → xui.lootContainer = localTileEntity  // IMPORTANT: Sets reference
    → QuestEventManager.Current.OpenedContainer(...)

// Closing a container:
XUiC_LootContainer.OnClose()
    → xui.lootContainer = null
    → localTileEntity.listeners.Remove(this)
    → QuestEventManager.Current.ClosedContainer(...)
```

### CRITICAL: Stale Data Problem

**When a container is open, `TileEntity.items[]` may be STALE!**

The UI maintains a separate buffer (`XUiC_ItemStack.ItemStack`) that only syncs back to the TileEntity via `HandleLootSlotChangedEvent` → `UpdateSlot()`.

**Safe ways to read container data:**

1. **Container CLOSED:** Read directly from `TileEntity.items[]` ✅
2. **Container OPEN:** Cache reference from `XUiC_LootContainer.localTileEntity` in OnOpen patch, then read from that cached reference (it stays synchronized)

### Locked TileEntities

```csharp
GameManager.Instance.lockedTileEntities  // Dictionary<ITileEntity, int>
// Maps TileEntity → entityId of player who has it open
```

This can be used to check if a container is currently open by a player.

---

## Challenge/Quest Integration

### Challenge Update Flow

```
Event fires (backpack/toolbelt/dragdrop changed)
    ↓
ChallengeObjectiveGather.ItemsChangedInternal()
    ↓
CheckObjectiveComplete()
    ↓
HandleUpdatingCurrent()
    ↓
    int itemCount = playerInventory.Backpack.GetItemCount(expectedItem);
    itemCount += playerInventory.Toolbelt.GetItemCount(expectedItem);
    base.Current = itemCount;  // THIS DRIVES THE UI DISPLAY
```

### Extending Challenge Counting (Our Solution)

To include container items in challenge counts:

1. **Fire events when containers change:**
   ```csharp
   [HarmonyPatch(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent")]
   static void Postfix() {
       // Fire DragAndDropItemChanged to trigger challenge recount
       player.DragAndDropItemChanged?.DynamicInvoke();
   }
   ```

2. **Modify the Current field in HandleUpdatingCurrent:**
   ```csharp
   [HarmonyPatch(typeof(ChallengeObjectiveGather), "HandleUpdatingCurrent")]
   static void Postfix(object __instance) {
       int containerCount = CountContainerItems(expectedItem);
       int total = inventoryCount + containerCount;
       currentField.SetValue(__instance, Math.Min(total, maxCount));
   }
   ```

---

## Modding Best Practices

### DO ✅

1. **Use `DragAndDropItemChanged` for safe event triggering** - Challenges already listen to it
2. **Cache container references in OnOpen patches** - Access live data during UI operations
3. **Use Postfix patches** - Let vanilla code complete before modifying
4. **Cap values at MaxCount** - Avoid display issues
5. **Check `IsGameReady()`** - Avoid null references during loading

### DON'T ❌

1. **Don't fire `OnBackpackItemsChangedInternal` during item transfers** - Causes item duplication!
2. **Don't rely on `xui.WindowGroups`** - Contains only menu windows, not gameplay UI
3. **Don't rely on `activeItemStackGrids`** - Often empty even when grids are visible
4. **Don't modify `TileEntity.items[]` directly** - Use `UpdateSlot()` to maintain sync
5. **Don't assume `xui.lootContainer` is set** - It's null until OnOpen completes

---

## Common Pitfalls

### Pitfall 1: Item Duplication

**Cause:** Firing `OnBackpackItemsChangedInternal` during an item transfer operation.

**Solution:** Use `DragAndDropItemChanged` instead - it's safe to fire in Postfix patches.

### Pitfall 2: Stale Container Data

**Cause:** Reading from `TileEntity.items[]` while container UI is open.

**Solution:** Cache the `localTileEntity` reference from `XUiC_LootContainer` in an OnOpen patch. The cached reference stays synchronized.

### Pitfall 3: UI Not Updating

**Cause:** Modifying data but not triggering the event that causes UI refresh.

**Solution:** Fire appropriate events after data changes. For challenges, the `Current` property setter triggers UI updates.

### Pitfall 4: Finding the Container UI

**Cause:** `xui.WindowGroups` doesn't contain gameplay windows.

**Solution:** 
- Use `xui.lootContainer` (set during OnOpen)
- Or patch `XUiC_LootContainer` methods directly

### Pitfall 5: Events Firing Too Early

**Cause:** Prefix patches or events that fire before the operation completes.

**Solution:** Always use Postfix patches for event triggering.

---

## Key Classes Reference

### Player & Inventory

| Class | Purpose | Key Members |
|-------|---------|-------------|
| `EntityPlayerLocal` | Local player entity | `bag`, `inventory`, `equipment`, `DragAndDropItemChanged` |
| `Bag` | Backpack inventory | `GetSlots()`, `GetItemCount()`, `OnBackpackItemsChangedInternal` |
| `Inventory` | Toolbelt inventory | `GetSlots()`, `GetItemCount()`, `OnToolbeltItemsChangedInternal` |
| `ItemStack` | Single item stack | `itemValue`, `count`, `Clone()` |
| `ItemValue` | Item type/quality | `type`, `ItemClass`, `GetItemName()` |

### UI Components

| Class | Purpose | Key Members |
|-------|---------|-------------|
| `LocalPlayerUI` | Player UI root | `primaryUI`, `xui`, `activeItemStackGrids` |
| `XUi` | Main UI controller | `PlayerInventory`, `lootContainer`, `windowManager` |
| `XUiM_PlayerInventory` | Inventory model | `Backpack`, `Toolbelt`, `GetItemCount()` |
| `XUiC_ItemStack` | Single slot controller | `ItemStack`, `SlotChangedEvent`, `HandleSlotChangeEvent()` |
| `XUiC_ItemStackGrid` | Grid of slots | `itemControllers[]`, `GetSlots()` |
| `XUiC_LootContainer` | Container grid | `localTileEntity`, `HandleLootSlotChangedEvent()`, `OnOpen()`, `OnClose()` |

### Storage & TileEntities

| Class | Purpose | Key Members |
|-------|---------|-------------|
| `TileEntity` | World block with data | `ToWorldPos()`, `listeners` |
| `ITileEntityLootable` | Lootable interface | `items[]`, `GetContainerSize()`, `UpdateSlot()` |
| `TileEntityLootContainer` | Simple container | Implements `ITileEntityLootable` |
| `TileEntityComposite` | Modular tile entity | `GetFeature<T>()` |
| `TEFeatureStorage` | Storage feature | `items[]`, implements `ITileEntityLootable` |

### Challenges

| Class | Purpose | Key Members |
|-------|---------|-------------|
| `ChallengeObjectiveGather` | Gather X items objective | `HandleAddHooks()`, `HandleUpdatingCurrent()`, `CheckObjectiveComplete()` |
| `BaseChallengeObjective` | Base objective class | `Current`, `MaxCount`, `Complete`, `StatusText` |
| `ChallengeBaseTrackedItemObjective` | Tracked item base | `expectedItem`, `expectedItemClass` |

---

## Useful Harmony Patch Targets

### For Inventory Mods

```csharp
// When items change in backpack
[HarmonyPatch(typeof(Bag), "OnBackpackItemsChangedInternal")]

// When items change in toolbelt  
[HarmonyPatch(typeof(Inventory), "OnToolbeltItemsChangedInternal")]

// When item picked up/placed from cursor
[HarmonyPatch(typeof(EntityPlayerLocal), "set_DragAndDropItem")]
```

### For Container Mods

```csharp
// When container opened
[HarmonyPatch(typeof(XUiC_LootContainer), "OnOpen")]

// When container closed
[HarmonyPatch(typeof(XUiC_LootContainer), "OnClose")]

// When item placed in container slot
[HarmonyPatch(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent")]

// When any item slot changes (universal)
[HarmonyPatch(typeof(XUiC_ItemStack), "HandleSlotChangeEvent")]
```

### For Challenge Mods

```csharp
// When challenge counts items
[HarmonyPatch(typeof(ChallengeObjectiveGather), "HandleUpdatingCurrent")]

// When challenge checks completion
[HarmonyPatch(typeof(ChallengeObjectiveGather), "CheckObjectiveComplete")]

// When challenge hooks are added
[HarmonyPatch(typeof(ChallengeObjectiveGather), "HandleAddHooks")]
```

---

## Version Information

- **Game Version:** 7 Days to Die V1.0+
- **Guide Version:** 1.0
- **Last Updated:** December 27, 2024
- **Based On:** ProxiCraft mod development research

---

## Credits

This guide was created during the development of the ProxiCraft mod, involving extensive decompilation analysis and iterative testing to understand the inventory event system.
