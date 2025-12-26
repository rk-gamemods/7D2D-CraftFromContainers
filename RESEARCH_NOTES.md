# ProxiCraft - Challenge Tracker Research Notes

## Problem Statement
Challenge tracker shows incorrect item counts when items are moved between player inventory and storage containers.

## Root Cause (Confirmed)
When a container is open, the TileEntity.items array is **stale**. The UI maintains a separate buffer (`XUiC_ItemStackGrid.items`) that only syncs back to the TileEntity when the container is closed.

---

## Attempted Solutions

### 1. Skip Open Containers (via lockedTileEntities)
**Date:** 2024-12-26  
**Result:** ❌ FAILED - Broke all container counting  
**Reason:** The `lockedTileEntities` dictionary appeared to keep containers marked as "open" even after closing, causing ALL containers to be skipped.  
**Code Location:** `ContainerManager.GetContainersOpenByLocalPlayer()`  
**Log Evidence:** `Skipping OPEN container at 947, 61, 812` even when container was closed

### 2. Force Cache Refresh Every Call
**Date:** 2024-12-26  
**Result:** ❌ FAILED - Still stale data  
**Reason:** Cache refresh doesn't help because TileEntity.items itself is stale, not our cache of references to it.  
**Code Location:** `ContainerManager.GetItemCount()` - added `_forceCacheRefresh = true`

### 3. Direct TileEntity Scan (No Caching)
**Date:** 2024-12-26  
**Result:** ❌ FAILED - Still stale data  
**Reason:** Same as #2 - we're reading from the stale source regardless of whether we cache or not.  
**Code Location:** Rewrote `GetItemCount()` to scan chunks directly

### 4. Delayed Refresh Timer (50ms)
**Date:** 2024-12-26  
**Result:** ❌ FAILED - Data still stale after delay  
**Reason:** The TileEntity.items doesn't sync until container closes. No amount of waiting helps.  
**Code Location:** `DelayedRefreshHelper` MonoBehaviour in ProxiCraft.cs

---

## Key Observations

1. **Pause/Unpause Fixes Counts** - Something syncs when game pauses. Need to identify what.

2. **TileEntity.items vs UI Buffer**
   - TileEntity.items: Stale during UI operations
   - XUiC_ItemStackGrid.items: Live data during UI operations
   - Sync happens on container close

3. **lockedTileEntities Dictionary**
   - Maps ITileEntity → entityId of player who has it open
   - Should clear when container closes, but behavior was unreliable in testing

---

## Current Approach: One Source of Truth

**Strategy:** For each container, read from the authoritative source:
- Container CLOSED → Read TileEntity.items
- Container OPEN → Read from xui.lootContainer.items (same backing store, but via UI reference)

### DECOMPILED CODE ANALYSIS (2024-12-26)

Set up automated decompiler: `7D2DMods\DecompileGameCode.ps1`
Output: `7D2DMods\7D2DCodebase\Assembly-CSharp\` (4237 .cs files)

**Key Findings from Decompiled Code:**

1. **XUiC_LootContainer.OnOpen()** sets `xui.lootContainer = localTileEntity`
2. **XUiC_LootContainer.HandleLootSlotChangedEvent()** immediately calls `localTileEntity.UpdateSlot(slotNumber, stack)` which modifies `items[]` directly
3. So `ITileEntityLootable.items` SHOULD be live data when reading via `xui.lootContainer`!

4. **TileEntityComposite/TEFeatureStorage** flow:
   - User clicks "Search" on storage container
   - `TEFeatureStorage.OnBlockActivated("Search")` is called
   - Calls `TELockServer(0, blockPos, entityId, playerId, "container")`
   - Should trigger `GameManager.OpenTileEntityUi()` with `_customUi = "container"`
   - Which calls `lootContainerOpened()` 
   - Which opens `windowManager.Open("looting")`

5. **BUT** our diagnostic logs show `looting=False` even when container is open!
   - `lockedTileEntities` has the container locked by player
   - But `windowManager.IsWindowOpen("looting")` returns false
   - And `xui.lootContainer` is null

**Hypothesis:** Something is preventing the "looting" window from opening for TileEntityComposite containers. Perhaps another mod interference, or the composite path has a different code flow.

---

### ITEMS[] STALE DATA CONFIRMED (2024-12-27)

**Test Scenario:** Container open with 4 stones in slot 37. Shift+click stones to inventory.

**Log Evidence:**
```
[10:33:16.890] [COUNT] TEFeatureStorage slots: [37]=4xresourceRockSmall
```
Even AFTER shift+click moved stones to inventory, `items[]` still shows 4 stones!

**Root Cause Identified:**
- `TEFeatureStorage.UpdateSlot()` does `items[_idx] = _item.Clone()` 
- But the `items[]` array is updated AFTER the count is read
- The UI's `itemControllers[i].ItemStack` is updated IMMEDIATELY
- Reading from `items[]` = stale data
- Reading from `itemControllers[i].ItemStack` = live data

### 5. Read UI itemControllers Directly (Fix #5)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - UI lookup not working  
**Approach:** Instead of reading `ITileEntityLootable.items[]`, read from `XUiC_LootContainer.itemControllers[i].ItemStack`
**Code Change:** `ContainerManager.GetOpenContainerItemCount()` now:
1. Gets `XUiC_LootContainer` via `xui.FindWindowGroupByName(XUiC_LootWindowGroup.ID).GetChildByType<XUiC_LootContainer>()`
2. Gets `itemControllers` field via reflection
3. Reads each `itemControllers[i].ItemStack` for LIVE UI data
4. Falls back to `items[]` only if UI read fails

**Log Evidence (10:38:36):**
```
[FALLBACK] Player has locked TileEntity, UI read failed - using items[] (may be stale!)
[COUNT] TEFeatureStorage slots: [3]=4xresourceRockSmall ...
```
The code is ALWAYS hitting the fallback path. Either `FindWindowGroupByName` returns null, or `GetChildByType<XUiC_LootContainer>` returns null, or `localTileEntity` is null.

**Next Step:** Added diagnostic logging to show exactly which step fails:
- `[UI-PATH] lootWindowGroup={true/false}, ID={...}`
- `[UI-PATH] lootContainerUI={true/false}`

### 5b. Added UI Path Diagnostics
**Date:** 2024-12-27  
**Result:** ❌ CONFIRMED - FindWindowGroupByName("looting") returns null!
**Log Evidence (10:42:38):**
```
[UI-PATH] lootWindowGroup=False, ID=looting
```
The "looting" window group is NOT being found even though the container UI is visible!

**Analysis:** 
- Container IS visually open (you can move items)
- TileEntity IS locked by player (lockedTileEntities has it)
- But `xui.FindWindowGroupByName("looting")` returns null
- This means the "looting" window group isn't registered or has different ID

### 5c. Multiple UI Lookup Methods
**Date:** 2024-12-27  
**Result:** ❌ FAILED - ALL three methods return null!
**Log Evidence (10:46:09):**
```
[UI-PATH] FindWindowGroupByName('looting')=False
[UI-PATH] GetChildByType<XUiC_LootWindowGroup>=False
[UI-PATH] windowManager.GetWindow('looting')=False
```
Even `windowManager.GetWindow("looting")` returns null - the window is NOT registered!

**Analysis:** This is very strange because:
- The container UI IS visually open
- GameManager.lootContainerOpened() calls `windowManager.Open("looting")`
- But the window doesn't exist in the window manager

**Hypothesis:** Maybe there's another mod overriding the UI, or player storage containers use a completely different window type

### 5d. Enumerate All Open Windows
**Date:** 2024-12-27  
**Result:** ❌ MAJOR DISCOVERY - No container window showing!
**Log Evidence (10:55:27):**
```
[BROAD-SCAN] Showing windows: toolTip saveIndicator CalloutGroup
[BROAD-SCAN] Player has locked: TileEntityComposite
[BROAD-SCAN] Composite has TEFeatureStorage=True, ILockable=True
```

**CRITICAL FINDING:** When the container UI is visible on screen:
- The TileEntity IS locked by the player ✓
- But NO window with `isShowing=true` contains an item grid!
- Only `toolTip`, `saveIndicator`, `CalloutGroup` are showing
- The container UI is NOT in the WindowGroups list as "showing"

This is VERY strange - the container UI is visible but not tracked as a "showing" window.

### 5e. Scan ALL Windows (Not Just Showing)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - xui.WindowGroups only contains MENU windows!
**Log Evidence (11:xx:xx):**
```
[BROAD-SCAN] ALL windows: toolTip(SHOW) saveIndicator(SHOW) exitingGame(hide) customCharacterSystem(hide) optionsMenu(hide) ...
```

**CRITICAL DISCOVERY:** `xui.WindowGroups` contains ONLY menu windows:
- `toolTip`, `saveIndicator`, `exitingGame`, `optionsMenu`, `keyBindings`, etc.
- NO in-game windows like "looting", "backpack", "crafting" exist in this list!
- The in-game UI uses a completely different system than the menu UI

**Conclusion:** WindowGroups is the WRONG place to look for the container UI.

---

### 6. LocalPlayerUI.activeItemStackGrids (Fix #6)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - activeItemStackGrids is EMPTY when container is open!
**Approach:** Use `LocalPlayerUI.activeItemStackGrids` instead of `xui.WindowGroups`

**Log Evidence:**
```
ActiveGrids: EMPTY
```

Even with container visually open, the activeItemStackGrids list is empty.

---

### 7. Comprehensive 7-Source Scan (Fix #7)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - ALL UI access methods return null/empty
**Approach:** Scan every possible source for open container items

**Sources Checked:**
1. `player.bag.GetSlots()` - Player backpack ✓ Works
2. `player.inventory.GetSlots()` - Player toolbelt ✓ Works  
3. `xui.lootContainer.items[]` - NULL
4. `GameManager.lockedTileEntities` → `ITileEntityLootable.items[]` - Returns 0 (stale!)
5. `windowManager.GetWindow("looting")` - NULL
6. `playerUI.activeItemStackGrids` - EMPTY
7. `xui.PlayerInventory.GetItemCount()` - Only counts backpack+toolbelt

**Log Evidence:**
```
xui.lootContainer: NULL
LockedTE.items[]: 0 at 947, 61, 812 (TileEntityComposite)
WindowMgr.GetWindow('looting'): NULL
ActiveGrids: EMPTY
```

**Conclusion:** There is NO way to access the live container UI data through any of these APIs.

---

### 8. Event-Based Recount (Fix #8)
**Date:** 2024-12-27  
**Result:** ✅ PARTIAL SUCCESS - Shift+click from container→inventory now works!
**Approach:** Don't try to READ container data - instead TRIGGER a recount when containers change

**Key Insight:** 
The problem isn't that we can't count items - vanilla counts correctly! The issue is:
- Challenges listen to `OnBackpackItemsChangedInternal` and `OnToolbeltItemsChangedInternal`
- Container changes DON'T fire these events
- So challenges don't recount when items move to/from containers

**Solution:** Patch `XUiC_LootContainer.HandleLootSlotChangedEvent` to fire `OnBackpackItemsChangedInternal` after container slot changes. This triggers all challenge objectives to recount.

**Code Added:** `ProxiCraft.cs`
```csharp
[HarmonyPatch(typeof(XUiC_LootContainer), "HandleLootSlotChangedEvent")]
private static class LootContainer_SlotChanged_Patch
{
    public static void Postfix(XUiC_LootContainer __instance)
    {
        // Fire OnBackpackItemsChangedInternal to trigger challenge recounts
        var player = GameManager.Instance?.World?.GetPrimaryPlayer();
        var eventField = player.bag.GetType().GetField("OnBackpackItemsChangedInternal", ...);
        var eventDelegate = eventField.GetValue(player.bag) as Delegate;
        eventDelegate?.DynamicInvoke();
    }
}
```

**Test Results:**
- ✅ Shift+click from storage → inventory: COUNT NOW UPDATES CORRECTLY!
- ❌ Split stack in inventory, place half in storage: Count shows only inventory portion

**Remaining Issue Analysis:**
When placing items INTO container:
1. Items leave cursor (not backpack) → go into container
2. Our patch fires backpack event → challenge recounts backpack (unchanged)
3. Our StatusText tries to add container count, but can't access open container data
4. Result: only backpack items show (container items not counted)

---

### 8b. Cached Open Container Reference (Fix #8b)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - Manual drag still not triggering recount
**Approach:** Cache the open container's `localTileEntity` reference so our counting code can access live data

**Test Results:**
- ✅ Shift+click storage → inventory: Works
- ✅ Shift+click inventory → storage: Works  
- ❌ Manual drag inventory → storage: Count stays at 2

**Analysis:**
Shift+click uses different code path than manual drag. Manual drag uses `SwapItem()` which:
1. Calls `HandleSlotChangeEvent()` on SOURCE slot (backpack)
2. Then calls `childByType2?.SetSlots()` to refresh container UI
3. Our `HandleLootSlotChangedEvent` patch is NOT triggered for manual drag!

---

### 8c. Patch XUiC_ItemStack.HandleSlotChangeEvent (Fix #8c)
**Date:** 2024-12-27  
**Result:** ❌ FAILED - Caused infinite item duplication glitch!
**Approach:** Patch the universal slot change handler to fire `OnBackpackItemsChangedInternal`

**Problem:** Firing `OnBackpackItemsChangedInternal` DURING item transfers interfered with the game's item movement logic, causing items to be duplicated instead of moved.

---

### 8d. Use DragAndDropItemChanged Event (Fix #8d) - CURRENT
**Date:** 2024-12-27  
**Result:** ✅ PARTIAL SUCCESS
**Approach:** Fire `DragAndDropItemChanged` instead of `OnBackpackItemsChangedInternal`

**Key Insight from ChallengeObjectiveGather.HandleAddHooks():**
```csharp
playerInventory.Backpack.OnBackpackItemsChangedInternal += ItemsChangedInternal;
playerInventory.Toolbelt.OnToolbeltItemsChangedInternal += ItemsChangedInternal;
player.DragAndDropItemChanged += ItemsChangedInternal;  // <-- Challenges ALREADY listen to this!
```

**Implementation:**
1. `LootContainer_SlotChanged_Patch` - Fire `DragAndDropItemChanged` when container slot changes (Postfix)
2. `ItemStack_SlotChanged_Patch` - Fire `DragAndDropItemChanged` for backpack/toolbelt changes when container open (skip LootContainer slots to avoid double-fire)

**Test Results:**
- ✅ No item duplication (DragAndDropItemChanged is safe)
- ✅ Shift+click from storage → inventory: Works
- ✅ Shift+click from inventory → storage: Works
- ❌ Pick up item in inventory → count decreases (expected, but doesn't restore when placed in storage)
- ❌ Pick up from storage → count doesn't decrease

**Analysis:**
`HandleLootSlotChangedEvent` only fires when items are PLACED into container, not when picked up.
Need to also trigger recount when items are PICKED UP from container slots.

**Awaiting Test:** Added `ItemStack_SlotChanged_Patch` for ALL slot changes (not just container).

---

### 8e. Set Current Field in HandleUpdatingCurrent (Fix #8e) - FINAL SOLUTION
**Date:** 2024-12-27  
**Result:** ✅ SUCCESS - ALL SCENARIOS WORKING!
**Approach:** Actually SET the `Current` field to include container count

**Key Discovery from Logs:**
The counting was working correctly (`containers=4, total=4`) but the UI wasn't updating because:
1. Vanilla `HandleUpdatingCurrent` sets `base.Current = inventoryCount` (backpack+toolbelt only)
2. Our patch ran AFTER but only LOGGED - didn't modify the field
3. The `Current` field drives the UI display

**Fix:** In `HandleUpdatingCurrent_Patch.Postfix()`, SET the `current` field:
```csharp
if (containerCount > 0 && currentField != null && displayCount != inventoryCount)
{
    currentField.SetValue(__instance, displayCount);
}
```

**Why This Is Safe:**
- Previous duplication bug was caused by firing `OnBackpackItemsChangedInternal` during transfers
- Now we use `DragAndDropItemChanged` which doesn't interfere with item transfers
- Modifying the `Current` field only affects DISPLAY, not actual item movement

**Final Working Solution Components:**
1. `LootContainer_OnOpen_Patch` - Cache container reference when opened
2. `LootContainer_OnClose_Patch` - Clear cache when closed
3. `LootContainer_SlotChanged_Patch` - Fire `DragAndDropItemChanged` when items placed in container
4. `ItemStack_SlotChanged_Patch` - Fire `DragAndDropItemChanged` for inventory/toolbelt changes when container open
5. `HandleUpdatingCurrent_Patch` - SET `Current` field to include container items

---

## Final Working Test Results

| Scenario | Expected | Status |
|----------|----------|--------|
| Items in closed containers | Shows total from all containers | ✅ Works |
| Shift+click container → inventory | Count updates immediately | ✅ Works |
| Shift+click inventory → container | Count updates immediately | ✅ Works |
| Drag item from inventory to container | Count updates immediately | ✅ Works |
| Drag item from container to inventory | Count updates immediately | ✅ Works |
| Split stack, place in container | Count updates immediately | ✅ Works |
| Pick up item from container | Count updates immediately | ✅ Works |
| No item duplication | Items transfer correctly | ✅ Works |
| Challenge completes when total >= max | Green highlight appears | ✅ Works |

---

### UI Hierarchy (Confirmed from Decompiled Code)

```
LocalPlayerUI.primaryUI
  → xui
    → lootContainer (ITileEntityLootable) - SET BY XUiC_LootContainer.OnOpen()
    → FindWindowGroupByName("looting")
      → XUiC_LootWindowGroup
        → XUiC_LootWindow (childByType)
          → XUiC_LootContainer (childByType)
            → GetSlots() returns localTileEntity.items
```

### Classes Documented
- `XUiC_LootContainer` - Grid controller, sets xui.lootContainer in OnOpen()
- `XUiC_LootWindowGroup` - Window group, has `te` field for ITileEntityLootable
- `XUiC_LootWindow` - Window wrapper
- `TEFeatureStorage` - Storage feature for TileEntityComposite
- `GameManager.lootContainerOpened()` - Opens looting window

---

## Test Cases

| Scenario | Expected | Status |
|----------|----------|--------|
| 10 items in closed container | Shows 10 | ✓ Works |
| Shift+click from container to inventory | Shows correct total | ✓ FIXED (Fix #8) |
| Split stack, place half in container | Shows correct total | 🔄 Testing (Fix #8b) |
| Close container after moving | Shows correct | ✓ Works |
| Multiple containers, one open | Shows sum of all | ❌ Untested |

---

## Files Modified
- `ProxiCraft/ContainerManager.cs` - Container scanning and counting
- `ProxiCraft/ProxiCraft.cs` - Harmony patches for challenge system

## Useful Log Patterns
```
GetItemCount: Container at X, Y, Z has N itemName
StatusText_Patch: Item=X, actualInv=Y, containers=Z, totalPossession=W
CheckComplete_Patch: Item=X, actualInv=Y, containers=Z
```
