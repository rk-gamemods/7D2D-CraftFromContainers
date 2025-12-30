# Trader Selling Feature - Postmortem & Future Reference

**Status:** REMOVED (v1.2.0)  
**Date:** December 28, 2025  
**Feature:** Sell items from nearby containers directly to traders  

---

## Executive Summary

The "sell from containers" feature was removed due to inherent complexity and duplication risks that could not be fully mitigated. The core problem: **we had to replicate ~60% of the game's selling logic to determine if a sale would succeed BEFORE modifying item counts**. Any mismatch between our checks and the game's checks resulted in item duplication.

The "buy from containers" feature (paying with dukes from storage) remains functional and stable because it uses a much simpler approach.

---

## What We Were Trying To Do

**Goal:** Allow players to sell items stored in nearby containers to traders without first moving items to their inventory.

**User Experience:**
1. Player opens trader, clicks on item in inventory
2. Sell quantity slider shows TOTAL count (inventory + containers)
3. Player selects quantity (even if > inventory count)
4. Sale completes, items removed from containers as needed, dukes received

---

## How Close We Got

**90% functional** - The feature worked in most normal cases. Failed edge cases caused item duplication.

### What Worked:
- ✅ Displaying combined count in sell UI (inventory + containers)
- ✅ Pulling items from containers before sale
- ✅ Normal NPC trader transactions
- ✅ Basic vending machine transactions
- ✅ Bundle size validation

### What Failed:
- ❌ Edge case: Full inventory (no room for dukes) → **DUPLICATION**
- ❌ Edge case: Trader at item limit → **DUPLICATION**  
- ❌ Edge case: Vending machine full → **DUPLICATION**
- ❌ Race conditions with other mods → **DUPLICATION**
- ❌ Game updates changing check order → **DUPLICATION**

---

## Technical Architecture

### The Problem: PREFIX Timing

```
┌─────────────────────────────────────────────────────────────┐
│                    OnActivated() Flow                        │
├─────────────────────────────────────────────────────────────┤
│  OUR PREFIX RUNS HERE                                        │
│    ↓                                                         │
│  Vanilla Check 1: Is slot empty?  ──→ RETURN (early exit)   │
│    ↓                                                         │
│  Vanilla Check 2: Bundle size?    ──→ RETURN (early exit)   │
│    ↓                                                         │
│  Vanilla Check 3: Trader at limit? ─→ RETURN (early exit)   │
│    ↓                                                         │
│  Vanilla Check 4: No room for coins? → RETURN (early exit)  │
│    ↓                                                         │
│  ... more checks ...                                         │
│    ↓                                                         │
│  ACTUAL SALE: slot.count -= sellAmount                       │
└─────────────────────────────────────────────────────────────┘
```

**The Fundamental Issue:**

If we add items to the slot in PREFIX, but vanilla exits early due to any check, those items stay in inventory = DUPLICATION.

We tried to solve this by **replicating all vanilla checks** in our prefix, but:
1. Game code is complex (~180 lines with many conditions)
2. Two completely different paths (NPC trader vs. vending machine)
3. Hidden dependencies on game state we can't fully observe
4. Any game update could add new checks we don't know about

---

## Implementation Details (Preserved for Future Reference)

### Patches Involved

1. **XUiC_ItemInfoWindow.SetItemStack** - Added container items to `BuySellCounter.MaxCount`
2. **ItemActionEntrySell.OnActivated** - Pulled items from containers before vanilla's subtraction

### The SetItemStack Patch (Display Only - Safe)

```csharp
[HarmonyPatch(typeof(XUiC_ItemInfoWindow), nameof(XUiC_ItemInfoWindow.SetItemStack))]
private static class XUiC_ItemInfoWindow_SetItemStack_Patch
{
    public static void Postfix(XUiC_ItemInfoWindow __instance, XUiC_ItemStack stack)
    {
        if (!__instance.isOpenAsTrader) return;
        
        // Add container items to the max sellable count
        int containerCount = ContainerManager.GetItemCount(Config, stack.ItemStack.itemValue);
        if (containerCount > 0 && __instance.BuySellCounter != null)
        {
            __instance.BuySellCounter.MaxCount += containerCount;
        }
    }
}
```

### The OnActivated Patch (Dangerous - Removed)

```csharp
[HarmonyPatch(typeof(ItemActionEntrySell), "OnActivated")]
private static class ItemActionEntrySell_OnActivated_Patch
{
    public static void Prefix(ItemActionEntrySell __instance)
    {
        // Replicated ALL of vanilla's checks:
        // - Bundle size validation
        // - Vending machine coin limits (6 * RentCost)
        // - Vending machine item capacity (50 slots)
        // - Vending machine stack space calculation
        // - NPC trader item limits (3 * stackSize per item type)
        // - CanSwapItems (room for coins in player inventory)
        
        // Only after ALL checks pass:
        int pulled = ContainerManager.RemoveItems(Config, itemValue, needed);
        xUiC_ItemStack.ItemStack.count += pulled;
    }
}
```

### Vanilla Checks We Had To Replicate

| Check | Location | Complexity |
|-------|----------|------------|
| Empty slot | Line 106 | Simple |
| Bundle size (initial) | Line 110 | Simple |
| Vending: Max coins | Line 119 | Medium |
| Vending: 50 slot limit | Line 125-136 | High - iteration |
| Vending: Bundle size | Line 141 | Simple |
| Vending: Stack overflow calc | Line 128-135 | High - math |
| Trader: Item limit (3×stack) | Line 166-167 | Medium |
| Trader: Partial acceptance | Line 172 | Medium |
| Trader: Bundle size | Line 179 | Simple |
| Trader: CanSwapItems | Line 194 | **Black box** |

### The CanSwapItems Problem

`CanSwapItems(itemStack, coinStack, slotNumber)` is the final check for NPC trader sales. It verifies the player has room for the coins they'll receive.

**Problem:** This is a complex method that considers:
- Current slot contents
- Whether coins can stack with existing coins
- Backpack vs toolbelt slot locations
- Possibly other hidden conditions

We called it with our best approximation of the parameters, but any mismatch with how vanilla calls it = potential duplication.

---

## Alternative Approaches Considered

### 1. Transpiler Injection ❌
Inject code AFTER all checks pass but BEFORE the subtraction.

**Why rejected:** 
- Two completely different code paths (NPC vs vending)
- Multiple subtraction points in the code
- Extremely fragile to game updates

### 2. Postfix-Only Approach ❌
Only remove from containers AFTER confirming sale happened.

**Problem:** Vanilla gives coins based on `BuySellCounter.Count`. If we set that to include container items, vanilla gives coins for items we never took. If we don't set it, user can only sell what's in slot.

### 3. Complete Override ❌
Replace the entire `OnActivated` method.

**Why rejected:**
- Would break with every game update
- Would conflict with any other mod touching trader
- Maintenance nightmare

### 4. Duke-Only Mode ✅ (Implemented)
Only support container access for currency (dukes), not for selling goods.

**Why this works:**
- Buying (paying with dukes) has much simpler failure cases
- No bundle sizes, no trader limits, no vending machine logic
- Just check: can player receive the item? Remove dukes from containers.

---

## The Duplication Bug in Detail

### Reproduction Steps (Before Fix)
1. Have 10 grenades in inventory, 50 grenades in nearby container
2. Open trader, click grenades in inventory
3. UI shows "60" available to sell
4. Set quantity to 60
5. Click sell BUT: Inventory is full, no room for coins
6. Vanilla shows "No space for selling" and returns early
7. BUT: We already added 50 grenades to the slot!
8. Result: Player now has 60 grenades in inventory + 50 still in container

### Why Early Exit = Duplication

```csharp
// Our prefix runs FIRST
pulled = RemoveItems(container, 50);   // Removes 50 from container
slot.count += pulled;                   // Adds 50 to slot (now 60)

// Then vanilla runs
if (!CanSwapItems(...)) {
    ShowTooltip("No space!");
    return;  // EARLY EXIT - never subtracts from slot!
}
// This line never runs:
slot.count -= sellAmount;
```

---

## Lessons Learned

### 1. Prefix Patches That Modify State Are Dangerous
If the original method can exit early, any state changes in prefix persist.

### 2. Replicating Game Logic Is A Maintenance Trap
You're now responsible for updating your copy whenever the game updates.

### 3. Black Box Methods Are Red Flags
If you can't fully understand what a method checks, you can't safely call it.

### 4. Item Duplication Is The Worst Bug
Harder to detect, harder to reproduce, damages player saves permanently.

### 5. 80/20 Rule Applies
The buying feature (pay with dukes) gives 80% of the value with 20% of the complexity.

---

## Future Possibilities

### If TFP Adds Mod Hooks
If the game adds events like `OnBeforeSell` and `OnAfterSell`, this becomes trivial:
- `OnBeforeSell`: Validate and stage container items
- `OnAfterSell`: Commit the removal
- `OnSellCancelled`: Rollback staged items

### If We Build A State Machine
A complex but robust approach:
1. Prefix: Stage items (mark as "pending removal" but don't actually move)
2. Postfix: Check if sale succeeded, then commit or rollback
3. Requires tracking staged state across the method call

### Transpiler With Full IL Analysis
Identify ALL exit points in the method and inject rollback code at each one.
Extremely complex, extremely fragile, not recommended.

---

## Code Location (Removed)

The removed code was in `ProxiCraft.cs`:
- Lines 1416-1518: `XUiC_ItemInfoWindow_SetItemStack_Patch`
- Lines 1520-1752: `ItemActionEntrySell_OnActivated_Patch`

Config option removed: `enableTraderSelling`

---

## Conclusion

The feature was technically achievable but the risk/reward ratio was unacceptable:
- **Risk:** Item duplication bugs that corrupt player saves
- **Reward:** Convenience of selling without moving items first

The buying feature (paying with dukes from containers) provides the most valuable use case with minimal risk, so that remains enabled.

If you're reading this for a future attempt, consider:
1. Waiting for official mod hooks from TFP
2. Building a proper staging/commit/rollback system
3. Accepting that some vanilla code paths are too complex to safely intercept

---

*Document created: December 28, 2025*  
*ProxiCraft v1.2.0*
