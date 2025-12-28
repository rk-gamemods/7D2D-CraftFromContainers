# ProxiCraft

A 7 Days to Die mod that allows crafting, reloading, refueling, and repairs using items from nearby storage containers.

## ⬇️ [Download ProxiCraft.zip](https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft.zip)

## Features

- ✅ **Challenge Tracker Integration** - Container items count toward challenges like "Gather 4 Wood"
- ✅ **Balanced Default Range** - 15-block radius by default (configurable, -1 for unlimited)
- ✅ **Real-time Updates** - Challenge counts update immediately when moving items
- ✅ **Conflict-Aware** - Uses low-priority patches to minimize issues with other mods
- ✅ **Robust Error Handling** - SafePatcher system prevents crashes

## Inspiration & Prior Art

ProxiCraft was inspired by the "Craft From Containers" concept implemented by various modders:

| Mod | Author | Notes |
|-----|--------|-------|
| [CraftFromContainers](https://www.nexusmods.com/7daystodie/mods/2196) | aedenthorn | Long-term maintainer |
| [CraftFromContainers v1.0](https://www.nexusmods.com/7daystodie/mods/4970) | SYN0N1M | Game v1.0 update |
| CraftFromContainersPlus | llmonmonll | Popular fork |

We studied these open-source projects for ideas and approaches, then made our own 
implementation with different design choices.

### What's Different in ProxiCraft

| Feature | ProxiCraft | Original CFC |
|---------|------------|--------------|
| **Default Range** | 15 blocks (balanced) | Unlimited |
| **Challenge Tracker** | ✅ Integrated | ❌ Not available |
| **SafePatcher System** | ✅ Graceful failures | ❌ Basic patching |
| **Conflict Detection** | ✅ Automatic warnings | ❌ Manual |

**Note:** You can only use ONE craft-from-containers mod at a time. ProxiCraft will 
warn you if it detects conflicting mods.

## Installation

1. Download the zip using the link above
2. Extract to `7 Days To Die/Mods/ProxiCraft/`
3. Ensure EAC is disabled (required for all DLL mods)

## Multiplayer Status

**Untested in multiplayer.** The mod includes lock synchronization code but has not been validated in multiplayer environments.

**Expected behavior:**
- Should work when players use **separate containers** at different locations
- May have issues with **shared storage** that multiple players access
- Race conditions possible if players craft simultaneously from the same container

**Technical limitation:** All container locking is client-side. There is no server-side validation to prevent concurrent access, so item duplication or loss could occur with shared containers.

## Project Structure

```
ProxiCraft/
├── ProxiCraft/           # Source code
│   ├── ProxiCraft.cs     # Main mod with Harmony patches
│   ├── ContainerManager.cs        # Container scanning and item management
│   ├── ModConfig.cs               # Configuration settings
│   ├── SafePatcher.cs             # Safe patching utilities
│   ├── AdaptivePatching.cs        # Compatibility helpers
│   ├── ModCompatibility.cs        # Conflict detection
│   ├── ConsoleCmdProxiCraft.cs    # Console commands (pc)
│   └── NetPackagePCLock.cs        # Multiplayer lock sync
├── Release/ProxiCraft/   # Ready-to-deploy mod package
│   ├── ProxiCraft.dll    # Compiled mod
│   ├── ModInfo.xml                # Mod metadata
│   └── config.json                # User configuration
├── tools/                         # Development utilities
│   ├── DecompileGameCode.ps1      # Game code decompiler script
│   └── README.md                  # Tool documentation
├── RESEARCH_NOTES.md              # Development history and debugging notes
├── INVENTORY_EVENTS_GUIDE.md      # Technical reference for modders
└── LICENSE                        # MIT License
```

## Features

### Code Organization
- Separated container scanning/management into dedicated `ContainerManager` class
- Mod compatibility checking in `ModCompatibility` class
- Safe patching utilities in `SafePatcher` class
- Clear separation of concerns between patches and business logic
- Added comprehensive XML documentation

### Error Handling
- All patches wrapped in try-catch blocks
- Graceful degradation - mod disables feature if it causes errors
- Detailed logging with configurable debug mode
- Safe patching that records success/failure for each patch

### Null Safety
- Extensive null checks throughout
- Safe pattern matching for type checks
- Defensive coding against game state changes

### Mod Compatibility
- **HarmonyPriority(Priority.Low)** on all patches - other mods run first
- Automatic detection of known conflicting mods
- Detection of Harmony patch conflicts with other mods
- Postfix patches preferred over Transpilers where possible
- Non-destructive - never breaks original game functionality

### Performance
- Caching of container scans (100ms cooldown)
- Position-based cache invalidation (only rescan when player moves)
- Lazy initialization of storage lists

### Troubleshooting Tools
- In-game console command `pc` for diagnostics
- Detailed diagnostic reports
- Container scan testing
- Real-time conflict detection

## Console Commands

Open the console with F1 and use:

| Command | Description |
|---------|-------------|
| `pc status` | Show mod status and configuration |
| `pc diag` | Full diagnostic report with patch status |
| `pc test` | Test container scanning (shows nearby items) |
| `pc conflicts` | Show detected mod conflicts |
| `pc toggle` | Enable/disable mod temporarily |
| `pc debug` | Toggle debug logging |
| `pc refresh` | Refresh container cache |

## Configuration

Create/edit `config.json` in the mod folder:

```json
{
  "modEnabled": true,
  "isDebug": false,
  "enableForRepairAndUpgrade": true,
  "enableForTrader": true,
  "enableForRefuel": true,
  "enableForReload": true,
  "enableFromVehicles": true,
  "allowLockedContainers": true,
  "range": 15
}
```

- `modEnabled` - Master toggle
- `isDebug` - Enable verbose logging (disable for performance)
- `range` - Max distance in **blocks** to scan for containers (default: 15, use -1 for unlimited)

### Range Guide

Range is the **radius** from player position (diameter = range × 2):

| Range | Diameter | Coverage |
|-------|----------|----------|
| 5 | 10 blocks | Same room only |
| 10 | 20 blocks | Adjacent rooms |
| 15 | 30 blocks | Same floor/area (default) |
| 30 | 60 blocks | Entire small building |
| 50 | 100 blocks | Large POI floor |
| -1 | Unlimited | All loaded chunks (can cause lag) |

## Building

```powershell
dotnet build -c Release
```

This will output:
- `Release/ProxiCraft/` - The mod folder (DLL, ModInfo.xml, config.json)
- `Release/ProxiCraft.zip` - Ready-to-distribute release package

## Troubleshooting

### Crafting doesn't use container items
1. Run `pc status` to check if mod is enabled
2. Run `pc test` to verify container scanning works
3. Run `pc conflicts` to check for mod conflicts
4. Check `config.json` that `modEnabled` is true

### Game crashes or errors
1. Enable debug logging: `pc debug`
2. Check game log for `[ProxiCraft]` messages
3. Run `pc diag` for full diagnostic report
4. Try disabling other mods to isolate the issue

### Challenge tracker not updating
- This is the Fix #8e feature. It patches `DragAndDropItemChanged` to safely trigger challenge refresh
- Run `pc diag` to verify the challenge patch is active
- See [RESEARCH_NOTES.md](RESEARCH_NOTES.md) for technical details

### Performance issues
1. Set `isDebug` to `false` in config.json
2. Reduce `range` if using unlimited (-1)
3. Check for other mods that scan containers

## Potential Mod Conflicts

ProxiCraft uses `Priority.Low` Harmony patches, meaning it runs *after* other mods to avoid breaking them. However, conflicts can still occur if another mod changes method signatures or return values that ProxiCraft depends on.

### High Risk (Will Conflict)
- **CraftFromChests / PullFromContainers / CraftFromContainersPlus** - Same functionality, patches same methods
- **AutoCraft** - Modifies crafting methods

### Medium Risk (May Work)
These mods patch some of the same classes. Conflicts are possible but not guaranteed:
- **SMXui / SMXhud / SMXmenu** - UI overhauls that may change window structures
- **BiggerBackpack** - Changes inventory methods
- **ExpandedStorage** - Changes TileEntity behavior

### Low Risk
- **BetterVehicles** - May change vehicle fuel systems
- Most mods that don't modify crafting or inventory

### Tested Compatible
These mods have been confirmed to work alongside ProxiCraft:
- **JaWoodleUI** - UI overhaul (v21.0)
- **TechFreqsIncreasedBackpack** - Increases backpack to 144 slots

## Known Compatibility

This mod uses Harmony to patch:
- `XUiM_PlayerInventory` - Item counting and removal
- `XUiC_RecipeList` / `XUiC_RecipeCraftCount` - Recipe UI
- `XUiC_IngredientEntry` - Ingredient display
- `ItemActionEntryCraft` - Craft button
- `ItemActionEntryPurchase` - Trader purchase
- `AnimatorRangedReloadState` - Weapon reload
- `EntityVehicle` - Vehicle refueling
- `GameManager` - Container lock/unlock events
- `ItemStack.DragAndDropItemChanged` - Challenge tracker updates (v2.0+)

Mods that heavily modify these classes may conflict. The `Priority.Low` attribute
helps ensure this mod's patches run after others.

## License

MIT License - See [LICENSE](LICENSE) for details.

This mod is released under MIT license to allow community continuation when game updates break functionality.
