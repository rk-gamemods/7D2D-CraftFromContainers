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
- `TileEntitySecureLootContainer` - Standard storage containers
- `TileEntityComposite` - Composite containers (may contain sub-containers)

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
