# 7 Days to Die Game Code Analysis

## Technical Reference for ProxiCraft mod

This document provides a comprehensive analysis of the 7 Days to Die game code relevant to crafting, inventory management, challenges, and related systems. The analysis was performed by decompiling `Assembly-CSharp.dll` (Version: 1.0 / Alpha 22+).

---

## 1. Challenge System

### 1.1 BaseChallengeObjective

**Location:** `Challenges.BaseChallengeObjective`

#### Key Fields and Properties

```csharp
public int MaxCount = 1;              // Target count to complete objective
protected int current;                 // Current progress count
private bool complete;                 // Completion flag
public bool IsTracking;               // Whether objective is actively tracked
```

#### StatusText Property
```csharp
public virtual string StatusText => $"{current}/{MaxCount}";
```
- Returns the "X/Y" format displayed in the UI
- **To modify display:** Override or patch this property's getter

#### FillAmount Property
```csharp
public virtual float FillAmount => (float)current / (float)MaxCount;
```
- Used for progress bar fill (0.0 to 1.0)
- Capped at 1.0 when current >= MaxCount

#### CheckObjectiveComplete Method
```csharp
public virtual bool CheckObjectiveComplete(bool handleComplete = true)
{
    HandleUpdatingCurrent();           // Updates 'current' from live data
    if (Current >= MaxCount)
    {
        Current = MaxCount;            // Cap at max
        Complete = true;
        if (handleComplete)
        {
            Owner.HandleComplete();    // Triggers challenge completion check
        }
        return true;
    }
    // ... returns false if not complete
}
```

**Key Insight:** The `current` field tracks gathered/progress items. `HandleUpdatingCurrent()` is called before checking completion, making it the ideal injection point.

### 1.2 ChallengeObjectiveGather

**Location:** `Challenges.ChallengeObjectiveGather`

#### Key Fields
```csharp
// Inherited from ChallengeBaseTrackedItemObjective:
protected ItemValue expectedItem;          // The item being tracked
protected ItemClass expectedItemClass;     // Item class reference
protected string itemClassID;              // Item name/ID string
```

#### How Item Tracking Works

1. **Hooks into inventory changes:**
```csharp
public override void HandleAddHooks()
{
    XUiM_PlayerInventory playerInventory = LocalPlayerUI.GetUIForPlayer(Owner.Owner.Player).xui.PlayerInventory;
    playerInventory.Backpack.OnBackpackItemsChangedInternal += ItemsChangedInternal;
    playerInventory.Toolbelt.OnToolbeltItemsChangedInternal += ItemsChangedInternal;
    player.DragAndDropItemChanged += ItemsChangedInternal;
    // ...
}
```

2. **HandleUpdatingCurrent - THE KEY METHOD:**
```csharp
protected override void HandleUpdatingCurrent()
{
    base.HandleUpdatingCurrent();
    XUiM_PlayerInventory playerInventory = LocalPlayerUI.GetUIForPlayer(Owner.Owner.Player).xui.PlayerInventory;
    int itemCount = playerInventory.Backpack.GetItemCount(expectedItem);
    itemCount += playerInventory.Toolbelt.GetItemCount(expectedItem);
    if (itemCount > MaxCount)
    {
        itemCount = MaxCount;  // Cap at max
    }
    if (current != itemCount)
    {
        base.Current = itemCount;  // Update tracked count
    }
}
```

**⚠️ CRITICAL FINDING:**
- This method directly queries `Backpack.GetItemCount()` and `Toolbelt.GetItemCount()`
- It does NOT use `XUiM_PlayerInventory.GetItemCount()` which ProxiCraft patches
- **To include container items:** Must patch `Bag.GetItemCount()` or `Inventory.GetItemCount()` directly, OR patch `HandleUpdatingCurrent`

3. **CheckForNeededItem - Completion Check:**
```csharp
private bool CheckForNeededItem()
{
    XUiM_PlayerInventory playerInventory = LocalPlayerUI.GetUIForPlayer(Owner.Owner.Player).xui.PlayerInventory;
    return playerInventory.Backpack.GetItemCount(expectedItem) + playerInventory.Toolbelt.GetItemCount(expectedItem) >= MaxCount;
}
```

### 1.3 UI Coloring for Challenges

**Location:** `XUiC_ChallengeEntry`

```csharp
// Key colors:
private string enabledColor;           // Normal active objectives
private string disabledColor;          // Completed/unavailable
private string redeemableColor = "0,255,0,255";  // GREEN - ready to complete!
private string trackedColor = "255, 180, 0, 255"; // Yellow/orange - tracked

// Color determination in GetBindingValueInternal:
case "iconcolor":
    if (entry.ChallengeState == ChallengeStates.Redeemed)
        value = disabledColor;
    else if (entry.ReadyToComplete)      // <- This controls GREEN
        value = redeemableColor;
    else if (entry.IsTracked)
        value = trackedColor;
    else
        value = enabledColor;
```

**`ReadyToComplete` Property (from Challenge class):**
```csharp
public bool ReadyToComplete
{
    get
    {
        if (ChallengeState != ChallengeStates.Completed)
        {
            if (ChallengeClass.RedeemAlways)
                return ChallengeState == ChallengeStates.Active;
            return false;
        }
        return true;  // Green when ChallengeState == Completed
    }
}
```

**To make objective show GREEN:** The objective's `Complete` property must be true, which sets `ChallengeState` to `Completed`.

---

## 2. XUiM_PlayerInventory

**Location:** `XUiM_PlayerInventory`

### 2.1 GetItemCount Methods

```csharp
public int GetItemCount(ItemValue _itemValue)
{
    return 0 + backpack.GetItemCount(_itemValue) + toolbelt.GetItemCount(_itemValue);
}

public int GetItemCount(int _itemId)
{
    ItemValue itemValue = new ItemValue(_itemId);
    return 0 + backpack.GetItemCount(itemValue) + toolbelt.GetItemCount(itemValue);
}
```

**⚠️ IMPORTANT:** This is what ProxiCraft patches. However, challenge objectives bypass this!

### 2.2 GetAllItemStacks

```csharp
public List<ItemStack> GetAllItemStacks()
{
    List<ItemStack> list = new List<ItemStack>();
    list.AddRange(GetBackpackItemStacks());   // backpack.GetSlots()
    list.AddRange(GetToolbeltItemStacks());   // toolbelt.GetSlots()
    return list;
}
```

**Used by:** Recipe checking, crafting operations

### 2.3 HasItems Method

```csharp
public bool HasItems(IList<ItemStack> _itemStacks, int _multiplier = 1)
{
    for (int i = 0; i < _itemStacks.Count; i++)
    {
        int num = _itemStacks[i].count * _multiplier;
        num -= backpack.GetItemCount(_itemStacks[i].itemValue);
        if (num > 0)
        {
            num -= toolbelt.GetItemCount(_itemStacks[i].itemValue);
        }
        if (num > 0)
        {
            return false;  // Not enough items
        }
    }
    return true;
}
```

**Key insight:** Uses `backpack.GetItemCount()` and `toolbelt.GetItemCount()` directly, NOT the combined `GetItemCount()` method.

### 2.4 RemoveItems Method

```csharp
public void RemoveItems(IList<ItemStack> _itemStacks, int _multiplier = 1, IList<ItemStack> _removedItems = null)
{
    if (!HasItems(_itemStacks))
        return;
    
    for (int i = 0; i < _itemStacks.Count; i++)
    {
        int num = _itemStacks[i].count * _multiplier;
        num -= backpack.DecItem(_itemStacks[i].itemValue, num, _ignoreModdedItems: true, _removedItems);
        if (num > 0)
        {
            toolbelt.DecItem(_itemStacks[i].itemValue, num, _ignoreModdedItems: true, _removedItems);
        }
    }
    onBackpackItemsChanged();
    onToolbeltItemsChanged();
}
```

**Removal order:** Backpack first, then Toolbelt

---

## 3. Inventory and Bag Classes

### 3.1 Inventory.GetItemCount

```csharp
public virtual int GetItemCount(ItemValue _itemValue, bool _bConsiderTexture = false, 
    int _seed = -1, int _meta = -1, bool _ignoreModdedItems = true)
{
    int num = 0;
    for (int i = 0; i < slots.Length; i++)
    {
        if ((!_ignoreModdedItems || !slots[i].itemValue.HasModSlots || !slots[i].itemValue.HasMods()) 
            && slots[i].itemStack.itemValue.type == _itemValue.type
            && (!_bConsiderTexture || slots[i].itemStack.itemValue.TextureFullArray == _itemValue.TextureFullArray)
            && (_seed == -1 || _seed == slots[i].itemValue.Seed) 
            && (_meta == -1 || _meta == slots[i].itemValue.Meta))
        {
            num += slots[i].itemStack.count;
        }
    }
    return num;
}
```

**Parameters explained:**
- `_ignoreModdedItems`: If true, skip items with mods installed (default)
- `_bConsiderTexture`: Match texture array exactly
- `_seed`, `_meta`: Match specific item variants

### 3.2 Inventory.DecItem

```csharp
public virtual int DecItem(ItemValue _itemValue, int _count, bool _ignoreModdedItems = false, 
    IList<ItemStack> _removedItems = null)
{
    int num = _count;
    int num2 = 0;
    while (_count > 0 && num2 < slots.Length - 1)  // Note: slots.Length - 1 excludes dummy slot
    {
        if (slots[num2].itemStack.itemValue.type == _itemValue.type 
            && (!_ignoreModdedItems || !slots[num2].itemValue.HasModSlots || !slots[num2].itemValue.HasMods()))
        {
            if (ItemClass.GetForId(slots[num2].itemStack.itemValue.type).CanStack())
            {
                int count = slots[num2].itemStack.count;
                int num3 = (count >= _count) ? _count : count;
                _removedItems?.Add(new ItemStack(slots[num2].itemStack.itemValue.Clone(), num3));
                slots[num2].itemStack.count -= num3;
                _count -= num3;
                if (slots[num2].itemStack.count <= 0)
                {
                    // Clear slot...
                }
            }
        }
        num2++;
    }
    return num - _count;  // Returns amount actually removed
}
```

**Returns:** Number of items actually removed (may be less than requested)

### 3.3 Bag.GetItemCount

```csharp
public int GetItemCount(ItemValue _itemValue, int _seed = -1, int _meta = -1, bool _ignoreModdedItems = true)
{
    ItemStack[] slots = GetSlots();
    int num = 0;
    for (int i = 0; i < slots.Length; i++)
    {
        if ((!_ignoreModdedItems || !slots[i].itemValue.HasModSlots || !slots[i].itemValue.HasMods()) 
            && slots[i].itemValue.type == _itemValue.type 
            && (_seed == -1 || _seed == slots[i].itemValue.Seed) 
            && (_meta == -1 || _meta == slots[i].itemValue.Meta))
        {
            num += slots[i].count;
        }
    }
    return num;
}
```

### 3.4 Bag.DecItem

```csharp
public int DecItem(ItemValue _itemValue, int _count, bool _ignoreModdedItems = false, 
    IList<ItemStack> _removedItems = null)
{
    int num = _count;
    ItemStack[] slots = GetSlots();
    int num2 = 0;
    while (_count > 0 && num2 < slots.Length)
    {
        if (slots[num2].itemValue.type == _itemValue.type 
            && (!_ignoreModdedItems || !slots[num2].itemValue.HasModSlots || !slots[num2].itemValue.HasMods()))
        {
            if (ItemClass.GetForId(slots[num2].itemValue.type).CanStack())
            {
                int count = slots[num2].count;
                int num3 = (count >= _count) ? _count : count;
                _removedItems?.Add(new ItemStack(slots[num2].itemValue.Clone(), num3));
                slots[num2].count -= num3;
                _count -= num3;
                if (slots[num2].count <= 0)
                {
                    slots[num2].Clear();
                }
            }
        }
        num2++;
    }
    return num - _count;
}
```

---

## 4. Crafting System

### 4.1 ItemActionEntryCraft.OnActivated

**Flow when player clicks "Craft":**

1. Gets the recipe from the recipe entry
2. Validates crafting requirements
3. Calls `hasItems()` to check materials:

```csharp
private bool hasItems(XUi _xui, Recipe _recipe)
{
    bool flag = false;
    List<ItemStack> allItemStacks = _xui.PlayerInventory.GetAllItemStacks();  // ← KEY CALL
    // ... process ingredients
    flag |= _xui.PlayerInventory.HasItems(tempIngredientList, craftCountControl.Count);
    
    // Also check workstation input grid if present
    XUiC_WorkstationInputGrid childByType = craftCountControl.WindowGroup.Controller.GetChildByType<XUiC_WorkstationInputGrid>();
    if (childByType != null)
    {
        flag |= childByType.HasItems(tempIngredientList, craftCountControl.Count);
    }
    return flag;
}
```

4. On craft, removes items:
```csharp
if (childByType != null)
{
    childByType.RemoveItems(recipe2.ingredients, craftCountControl.Count, tempIngredientList);
}
else
{
    xui.PlayerInventory.RemoveItems(recipe2.ingredients, craftCountControl.Count, tempIngredientList);
}
```

### 4.2 XUiC_RecipeList.BuildRecipeInfosList

**Called during recipe list refresh:**

```csharp
private void BuildRecipeInfosList(List<ItemStack> _items)
{
    recipeInfos.Clear();
    RecipeInfo item = default(RecipeInfo);
    for (int i = 0; i < recipes.Count; i++)
    {
        item.recipe = recipes[i];
        item.unlocked = XUiM_Recipes.GetRecipeIsUnlocked(base.xui, item.recipe);
        
        // THIS determines green/gray highlighting:
        bool flag = XUiM_Recipes.HasIngredientsForRecipe(_items, item.recipe, entityPlayer);
        item.hasIngredients = flag && (craftingWindow == null || craftingWindow.CraftingRequirementsValid(item.recipe));
        
        item.name = Localization.Get(item.recipe.GetName());
        recipeInfos.Add(item);
    }
}
```

**The `_items` list comes from:**
```csharp
list.AddRange(base.xui.PlayerInventory.GetBackpackItemStacks());
list.AddRange(base.xui.PlayerInventory.GetToolbeltItemStacks());
// Or from workstation input grid if present
```

### 4.3 XUiC_RecipeCraftCount.calcMaxCraftable

```csharp
private int calcMaxCraftable()
{
    if (recipe == null)
        return 1;
    
    XUiC_WorkstationInputGrid childByType = windowGroup.Controller.GetChildByType<XUiC_WorkstationInputGrid>();
    ItemStack[] array = (childByType == null) 
        ? base.xui.PlayerInventory.GetAllItemStacks().ToArray() 
        : childByType.GetSlots();
    
    // Check for quality items (max 1)
    for (int i = 0; i < recipe.ingredients.Count; i++)
    {
        ItemStack itemStack = recipe.ingredients[i];
        if (itemStack != null && itemStack.itemValue.HasQuality)
            return 1;
    }
    
    int num = int.MaxValue;
    // Calculate minimum craftable based on each ingredient...
    // Returns minimum across all ingredients
}
```

### 4.4 Recipe.CanCraftAny

```csharp
public bool CanCraftAny(IList<ItemStack> _itemStack, EntityAlive _ea = null)
{
    for (int tier = maxTier; tier >= 0; tier--)
    {
        bool flag = true;
        for (int i = 0; i < ingredients.Count; i++)
        {
            ItemStack itemStack = ingredients[i];
            int needed = CalculateNeededCount(itemStack, _ea, tier);
            
            int num3 = 0;
            while (needed > 0 && num3 < _itemStack.Count)
            {
                if (MatchesItem(_itemStack[num3], itemStack))
                {
                    needed -= _itemStack[num3].count;
                }
                num3++;
            }
            if (needed > 0)
            {
                flag = false;
                break;
            }
        }
        if (flag)
            return true;
    }
    return false;
}
```

**Key insight:** This iterates through the provided `_itemStack` list. To include container items, inject them into this list BEFORE calling.

---

## 5. Reload System

### 5.1 AnimatorRangedReloadState.GetAmmoCountToReload

```csharp
private int GetAmmoCountToReload(EntityAlive ea, ItemValue ammo, int modifiedMagazineSize)
{
    if (actionRanged.HasInfiniteAmmo(actionData))
    {
        if (actionRanged.AmmoIsPerMagazine)
            return modifiedMagazineSize;
        return modifiedMagazineSize - actionData.invData.itemValue.Meta;
    }
    
    // Check bag first
    if (ea.bag.GetItemCount(ammo) > 0)
    {
        if (actionRanged.AmmoIsPerMagazine)
            return modifiedMagazineSize * ea.bag.DecItem(ammo, 1);
        return ea.bag.DecItem(ammo, modifiedMagazineSize - actionData.invData.itemValue.Meta);
    }
    
    // Then check inventory (toolbelt)
    if (ea.inventory.GetItemCount(ammo) > 0)
    {
        if (actionRanged.AmmoIsPerMagazine)
            return modifiedMagazineSize * ea.inventory.DecItem(ammo, 1);
        return ea.inventory.DecItem(ammo, modifiedMagazineSize - actionData.invData.itemValue.Meta);
    }
    
    return 0;
}
```

**⚠️ CRITICAL:**
- Uses `ea.bag.GetItemCount()` and `ea.inventory.GetItemCount()` DIRECTLY
- Calls `DecItem` inline during reload - removal happens HERE
- **To support containers:** Must patch BOTH count check AND DecItem calls

---

## 6. Vehicle Refuel

### 6.1 EntityVehicle.hasGasCan

```csharp
protected bool hasGasCan(EntityAlive _ea)
{
    string fuelItem = GetVehicle().GetFuelItem();
    if (fuelItem == "")
        return false;
    
    ItemValue item = ItemClass.GetItem(fuelItem);
    int num = 0;
    
    // Check bag
    ItemStack[] slots = _ea.bag.GetSlots();
    for (int i = 0; i < slots.Length; i++)
    {
        if (slots[i].itemValue.type == item.type)
            num++;
    }
    
    // Check toolbelt
    for (int j = 0; j < _ea.inventory.PUBLIC_SLOTS; j++)
    {
        if (_ea.inventory.GetItem(j).itemValue.type == item.type)
            num++;
    }
    
    return num > 0;
}
```

**Note:** This counts STACKS not total items (incrementing by 1 per matching slot).

### 6.2 EntityVehicle.takeFuel

```csharp
private float takeFuel(EntityAlive _entityFocusing, int count)
{
    EntityPlayer entityPlayer = _entityFocusing as EntityPlayer;
    if (!entityPlayer)
        return 0f;
    
    string fuelItem = GetVehicle().GetFuelItem();
    if (fuelItem == "")
        return 0f;
    
    ItemValue item = ItemClass.GetItem(fuelItem);
    
    // Try inventory (toolbelt) first
    int num = entityPlayer.inventory.DecItem(item, count);
    if (num == 0)
    {
        // Then bag
        num = entityPlayer.bag.DecItem(item, count);
        if (num == 0)
            return 0f;
    }
    
    // Update UI...
    return num;
}
```

**Removal order:** Toolbelt first, then Bag (opposite of crafting!)

---

## 7. Trader System

### 7.1 ItemActionEntryPurchase.RefreshEnabled

```csharp
public override void RefreshEnabled()
{
    refreshBinding();
    XUiC_TraderItemEntry xUiC_TraderItemEntry = (XUiC_TraderItemEntry)base.ItemController;
    
    if (xUiC_TraderItemEntry?.Item != null)
    {
        int count = xUiC_TraderItemEntry.InfoWindow.BuySellCounter.Count;
        
        if (isOwner)
        {
            base.Enabled = count > 0;
            return;
        }
        
        XUi xui = xUiC_TraderItemEntry.xui;
        XUiM_PlayerInventory playerInventory = xui.PlayerInventory;
        
        // Calculate price
        int num = XUiM_Trader.GetBuyPrice(...);
        
        // Check if player has enough currency
        ItemValue item = ItemClass.GetItem(TraderInfo.CurrencyItem);
        base.Enabled = count > 0 && playerInventory.GetItemCount(item) >= num;
    }
}
```

### 7.2 TraderInfo.CurrencyItem

```csharp
public static string CurrencyItem { get; private set; }
```

**Default:** "casinoCoin" (Dukes Casino Tokens)

---

## 8. UI Color/State Summary

### Recipe Entry Colors

```csharp
case "hasingredientsstatecolor":
    if (HasIngredients)
        value = enabledColor (white or custom);  // CAN craft
    else
        value = disabledColor (148,148,148);     // Cannot craft
```

### Challenge Entry Colors

```csharp
case "iconcolor":
    if (!IsChallengeVisible)
        value = disabledColor;
    else if (entry.ChallengeState == Redeemed)
        value = disabledColor;
    else if (entry.ReadyToComplete)              // Complete!
        value = redeemableColor (0,255,0);       // GREEN
    else if (entry.IsTracked)
        value = trackedColor (255,180,0);        // Yellow/Orange
    else
        value = enabledColor;                    // Normal white
```

---

## 9. Double-Counting Prevention

### Where Double-Counting Can Occur

1. **Challenge `HandleUpdatingCurrent`** - calls `Backpack.GetItemCount` + `Toolbelt.GetItemCount` directly
   - If you ALSO patch `XUiM_PlayerInventory.GetItemCount`, items may be counted twice

2. **Crafting `hasItems`** - calls `GetAllItemStacks()` then `HasItems()`
   - If you add items to `GetAllItemStacks()` AND modify `HasItems()`, double-count

3. **Ingredient display** - calls `XUiM_PlayerInventory.GetItemCount()` 
   - If you patch both the method AND inject into binding value, double-count

### Safe Patching Strategy

1. **Choose ONE injection point per operation:**
   - For item counts: Patch `XUiM_PlayerInventory.GetItemCount()` OR the specific `Bag/Inventory.GetItemCount()`, not both
   
2. **For recipe availability:** Patch `BuildRecipeInfosList` to modify the `_items` list BEFORE processing
   
3. **For challenges:** 
   - Option A: Patch `Bag.GetItemCount` and `Inventory.GetItemCount` (affects EVERYTHING)
   - Option B: Patch `HandleUpdatingCurrent` and `CheckForNeededItem` in `ChallengeObjectiveGather` specifically
   - Option C: Patch `StatusText` getter and `CheckObjectiveComplete` (display only, no logic changes)

---

## 10. Caching Considerations

### Game's Caching

1. **Recipe list caching:** `XUiC_RecipeList` caches recipe info, refreshes on `IsDirty = true`
2. **Item change events:** `OnBackpackItemsChangedInternal` and `OnToolbeltItemsChangedInternal` trigger UI updates
3. **Challenge tracking:** Uses event-driven updates via `ItemsChangedInternal()`

### Mod Caching Recommendations

1. **Container cache invalidation:**
   - When player moves
   - When containers are opened/closed
   - When items are moved in/out of containers
   - After crafting operations

2. **Cache timing:**
   - Don't cache during active crafting operations
   - Refresh cache when container inventory changes
   - Consider using `GameManager.lockedTileEntities` to detect open containers

---

## 11. Critical Methods to Patch for Container Support

### High Priority (Core Functionality)

| Method | Purpose | Current pc status |
|--------|---------|-------------------|
| `XUiM_PlayerInventory.GetItemCount(ItemValue)` | Ingredient display | ✅ Patched |
| `XUiM_PlayerInventory.GetAllItemStacks()` | Recipe availability | ✅ Patched via Prefix |
| `XUiM_PlayerInventory.HasItems()` | Crafting material check | ✅ Patched |
| `XUiM_PlayerInventory.RemoveItems()` | Consume materials | ✅ Patched |

### Medium Priority (Extended Features)

| Method | Purpose | Current pc status |
|--------|---------|-------------------|
| `Bag.GetItemCount()` | Direct bag queries | ⚠️ Not needed (solved via events) |
| `Inventory.GetItemCount()` | Direct inventory queries | ⚠️ Not needed (solved via events) |
| `ChallengeObjectiveGather.HandleUpdatingCurrent()` | Challenge progress | ✅ Patched (Fix #8e) |
| `ItemStack.DragAndDropItemChanged` | Item transfer events | ✅ Patched (triggers challenge refresh) |

### Low Priority (Edge Cases)

| Method | Purpose | Current pc status |
|--------|---------|-------------------|
| `AnimatorRangedReloadState.GetAmmoCountToReload()` | Reload from containers | ✅ Patched |
| `EntityVehicle.hasGasCan()` | Fuel check | ✅ Patched |
| `EntityVehicle.takeFuel()` | Fuel consumption | ✅ Patched |
| `ItemActionEntryPurchase.RefreshEnabled()` | Trader currency | ✅ Patched |

---

## 12. Challenge Objectives - SOLVED (Fix #8e)

### Problem (Resolved)
Challenge objectives for gathering items (like "Gather 10 Wood") weren't counting container items.

### Root Cause (Identified)
`ChallengeObjectiveGather.HandleUpdatingCurrent()` directly calls `Backpack.GetItemCount()` and `Toolbelt.GetItemCount()`, bypassing our `XUiM_PlayerInventory` patches.

### Solution Implemented (v2.0)

The **Fix #8e** approach uses event-driven updates:

1. **`HandleUpdatingCurrent_Patch`** (Postfix)
   - After vanilla sets `Current` from inventory only
   - We ADD container item count to the `current` field
   - Cap at `MaxCount`

2. **`DragAndDropItemChanged` Event Trigger**
   - `LootContainer_SlotChanged_Patch` - Fires when items placed in container
   - `ItemStack_SlotChanged_Patch` - Fires for inventory/toolbelt changes when container open
   - These trigger `DragAndDropItemChanged` which causes challenge system to call `HandleUpdatingCurrent`

3. **Why This Works**
   - Uses safe event (`DragAndDropItemChanged`) that doesn't interfere with item transfers
   - Previous attempts using `OnBackpackItemsChangedInternal` caused infinite duplication
   - Only modifies the display `Current` field, not actual item movement

**All scenarios now work:** closed containers, shift+click transfers, drag operations, split stacks.

See [RESEARCH_NOTES.md](RESEARCH_NOTES.md) for the full debugging history and [INVENTORY_EVENTS_GUIDE.md](INVENTORY_EVENTS_GUIDE.md) for event system details.

---

*Document generated: December 26, 2025*
*Last updated: December 27, 2025 (Fix #8e documented)*
*Game Version: 7 Days to Die 1.0+ / 2.x*
