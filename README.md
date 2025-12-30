# ProxiCraft

A 7 Days to Die mod that allows crafting, reloading, refueling, and repairs using items from nearby storage containers.

## ⬇️ [Download ProxiCraft.zip](https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft.zip)

## Features

### Core Features (Stable)
- ✅ **Craft from Containers** - Use materials from nearby chests for crafting
- ✅ **Block Repair/Upgrade** - Use materials from containers for repairs
- ✅ **Challenge Tracker Integration** - Container items count toward challenges
- ✅ **Lockpicking** - Use lockpicks from containers to pick locks
- ✅ **Item Repair** - Use repair kits from containers to repair weapons/tools
- ✅ **Painting** - Use paint from containers when using paint brush
- ✅ **HUD Ammo Counter** - Shows total ammo from containers in HUD stat bar
- ✅ **Recipe Tracker Updates** - Real-time ingredient count updates from containers
- ✅ **Trader Selling** - Sell items from nearby containers to traders
- ✅ **Locked Slot Respect** - Items in locked container slots are excluded from all operations

### Extended Features (Transpiler-based)
- ✅ **Weapon Reload** - Reload weapons using ammo from containers
- ✅ **Vehicle Refuel** - Refuel vehicles using gas cans from containers
- ✅ **Generator Refuel** - Refuel generators from containers
- ✅ **Trader Purchases** - Pay with dukes stored in containers

### Runtime Configuration
- ✅ **Live Config Reload** - Changes to config.json apply without restart
- ✅ **Console Config Commands** - View and modify settings in-game

### Storage Sources
- ✅ **Standard Containers** - Chests, boxes, storage crates
- ✅ **Vehicle Storage** - Minibike, motorcycle, 4x4, gyrocopter bags
- ✅ **Drone Storage** - Player's drone cargo
- ✅ **Dew Collectors** - Water from dew collectors
- ✅ **Workstation Outputs** - Items in forge/campfire/chemistry output slots

### Reliability Features
- ✅ **Startup Health Check** - Validates all features on load
- ✅ **Silent by Default** - No output unless there are issues
- ✅ **Auto-Adaptation** - Attempts to recover from game updates
- ✅ **Conflict Detection** - Warns about mod conflicts
- ✅ **Graceful Degradation** - Features disable individually if broken

## Stability Philosophy

ProxiCraft is designed to survive game updates through multiple layers of protection:

### 1. Patch Stability Tiers

| Tier | Features | Risk Level |
|------|----------|------------|
| **Stable** | Crafting, Quests, Block Repair, Lockpicking, Item Repair, Painting, HUD Ammo, Recipe Tracker, Trader Selling, Locked Slots | Low - Uses simple postfix patches |
| **Less Stable** | Reload, Vehicle Refuel, Generator Refuel, Trader Purchases | Medium - Uses transpiler patches that modify IL code |
| **Storage Sources** | Vehicles, Drones, Dew Collectors, Workstations | Low - Only reads game data, doesn't patch |

### 2. Startup Health Check

On every game start, ProxiCraft validates all 24 patches:
- **Silent when OK** - No console spam if everything works
- **Warns on issues** - Clear messages if features break
- **Detailed diagnostics** - Use `pc fullcheck` for bug reports

### 3. Adaptive Recovery

When game updates change method names or signatures:
1. **Primary lookup** - Try exact method match
2. **Signature matching** - Match by parameter types
3. **Name pattern search** - Look for renamed methods
4. **Graceful failure** - Disable feature, not crash game

## Installation

1. Download the zip using the link above
2. Extract to `7 Days To Die/Mods/ProxiCraft/`
3. Ensure EAC is disabled (required for all DLL mods)

## Console Commands

Open the console with F1 and use:

| Command | Description |
|---------|-------------|
| `pc status` | Show mod status and configuration |
| `pc health` | Show startup health check results |
| `pc recheck` | Re-run health check |
| `pc fullcheck` | Full diagnostic report (for bug reports) |
| `pc diag` | Show mod compatibility report |
| `pc test` | Test container scanning (shows nearby items) |
| `pc perf` | Performance profiler (see below) |
| `pc reload` | Reload configuration from config.json |
| `pc conflicts` | Show detected mod conflicts |
| `pc toggle` | Enable/disable mod temporarily |
| `pc debug` | Toggle debug logging |

### Configuration Commands

| Command | Description |
|---------|-------------|
| `pc config list` | List all settings with current values |
| `pc config get <setting>` | Get a specific setting value |
| `pc config set <setting> <value>` | Set a setting value (temporary) |
| `pc config save` | Save current settings to config.json |
| `pc set <setting> <value>` | Shortcut for config set |
| `pc get <setting>` | Shortcut for config get |

**Example:**
```
pc set range 30       # Change range to 30 blocks
pc config save        # Save changes to config.json
```

### Troubleshooting Workflow

1. **Check status**: `pc status` - Is the mod enabled?
2. **Check health**: `pc health` - Are all features working?
3. **Test scanning**: `pc test` - Can mod see your containers?
4. **Full report**: `pc fullcheck` - Copy this for bug reports

### Performance Profiling

If you experience lag, use the built-in profiler:

| Command | Description |
|---------|-------------|
| `pc perf` | Show brief performance status |
| `pc perf on` | Enable profiling (collects timing data) |
| `pc perf off` | Disable profiling |
| `pc perf reset` | Clear collected data |
| `pc perf report` | Show detailed performance report |

The profiler tracks timing for container scans, item counting, and cache operations. Share the output when reporting performance issues.

## Configuration

Edit `config.json` in the mod folder. The file is organized into sections:

```json
{
  "modEnabled": true,
  "isDebug": false,
  "verboseHealthCheck": false,
  "range": 15,

  // Storage Sources (STABLE)
  "pullFromVehicles": true,
  "pullFromDrones": true,
  "pullFromDewCollectors": true,
  "pullFromWorkstationOutputs": true,
  "allowLockedContainers": true,

  // Core Features (STABLE - simple patches)
  "enableForCrafting": true,
  "enableForQuests": true,
  "enableForRepairAndUpgrade": true,
  "enableForLockpicking": true,
  "enableForItemRepair": true,
  "enableForPainting": true,

  // Extended Features (LESS STABLE - transpiler patches)
  "enableForReload": true,
  "enableForRefuel": true,
  "enableForTrader": true,
  "enableForGeneratorRefuel": true,

  // New Features
  "enableHudAmmoCounter": true,
  "enableRecipeTrackerUpdates": true,
  "enableTraderSelling": true,
  "respectLockedSlots": true
}
```

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `modEnabled` | true | Master toggle for entire mod |
| `isDebug` | false | Enable verbose logging (performance impact) |
| `verboseHealthCheck` | false | Always show full health check output |
| `range` | 15 | Search radius in blocks (-1 for unlimited) |
| `enableHudAmmoCounter` | true | Show container ammo in HUD stat bar |
| `enableRecipeTrackerUpdates` | true | Live recipe tracker ingredient updates |
| `enableTraderSelling` | true | Allow selling from containers to traders |
| `respectLockedSlots` | true | Skip items in user-locked container slots |

### Range Guide

Range is the **radius** from player position:

| Range | Coverage | Notes |
|-------|----------|-------|
| 5 | Same room only | Minimal performance impact |
| 15 | Same floor/area | **Default** - good balance |
| 30 | Entire building | Slight performance impact |
| -1 | All loaded chunks | May cause lag with many containers |

**Important:** Range does NOT affect Trader purchases - those work regardless of distance when talking to a trader.

### Feature Notes

| Feature | Range Affected? | Notes |
|---------|-----------------|-------|
| Crafting | Yes | Only containers within range |
| Block Repair | Yes | Only containers within range |
| Trader | **No** | Works anywhere when at trader |
| Vehicle Refuel | Yes | Containers near the vehicle |
| Generator Refuel | Yes | Containers near the generator UI |
| Lockpicking | Yes | Containers within range |

## Project Structure

```
ProxiCraft/
├── ProxiCraft/                    # Source code
│   ├── ProxiCraft.cs              # Main mod with Harmony patches
│   ├── ContainerManager.cs        # Container scanning and item management
│   ├── ModConfig.cs               # Configuration settings
│   ├── SafePatcher.cs             # Safe patching utilities
│   ├── AdaptivePatching.cs        # Mod compatibility helpers
│   ├── AdaptiveMethodFinder.cs    # Game update recovery
│   ├── RobustTranspiler.cs        # Safe transpiler utilities
│   ├── StartupHealthCheck.cs      # Feature validation system
│   ├── ModCompatibility.cs        # Conflict detection
│   ├── ConsoleCmdProxiCraft.cs    # Console commands (pc)
│   ├── PerformanceProfiler.cs     # Performance profiling system
│   └── NetPackagePCLock.cs        # Multiplayer lock sync
├── Release/ProxiCraft/            # Ready-to-deploy mod package
│   ├── ProxiCraft.dll             # Compiled mod
│   ├── ModInfo.xml                # Mod metadata
│   └── config.json                # User configuration
├── tools/                         # Development utilities
├── RESEARCH_NOTES.md              # Development history
├── INVENTORY_EVENTS_GUIDE.md      # Technical reference
└── LICENSE                        # MIT License
```

## Technical Details

### Patching Methodology

ProxiCraft uses three types of Harmony patches:

1. **Postfix Patches** (Most Stable)
   - Run after original method
   - Can modify return values
   - Used for: Item counting, crafting validation

2. **Prefix Patches** (Stable)
   - Run before original method
   - Can skip original if needed
   - Used for: Painting, lockpicking

3. **Transpiler Patches** (Less Stable)
   - Modify IL bytecode directly
   - Most powerful but most fragile
   - Used for: Reload, refuel, trader

### Health Check System

The startup health check validates:
- **Method existence** - Target methods still exist
- **Patch application** - Harmony hooks are active
- **Transpiler success** - IL modifications applied correctly
- **Type availability** - Required game classes exist

Results are cached and available via `pc health` command.

### Adaptive Recovery

When a game update changes code:

```
1. Try exact method match
   ↓ (fail)
2. Try signature-based matching
   ↓ (fail)
3. Try name pattern search (e.g., "GetAmmo" → "GetAmmoCount")
   ↓ (fail)
4. Mark feature as FAILED, continue with other features
```

This prevents the mod from crashing due to minor game updates.

## Design Decisions

### Workstation Slots: Output Only

When counting items in workstations (forge, campfire, workbench, chemistry station), ProxiCraft **only counts OUTPUT slots**. Input, fuel, and tool slots are intentionally ignored.

**Why?**

| Slot Type | Counted? | Reasoning |
|-----------|----------|-----------|
| **Output** | ✅ Yes | Finished products ready to use |
| **Input** | ❌ No | Items actively being processed/consumed |
| **Fuel** | ❌ No | Fuel is being burned, not available |
| **Tool** | ❌ No | Tools are in use (anvil, beaker, etc.) |

**Example:** You put 100 iron ore in the forge. You should NOT be able to craft iron bars from that ore - it's actively being smelted. But once the iron bars appear in the output slot, those ARE available for crafting.

This prevents confusion where:
- Iron in the smelting queue counts as "available" for crafting
- Wood in the fuel slot counts as building material
- The beaker in the chemistry station tool slot counts as an item

### Vehicle/Drone Storage: Full Access

Unlike workstations, vehicle and drone storage containers are fully counted. All slots are available because these are true storage containers - there's no "processing" happening.

### Open Container Handling

When you have a container/vehicle/workstation UI open, items are counted directly from that open source rather than from the world scan. This ensures:
- Real-time updates as you move items
- No double-counting
- Accurate challenge tracker updates

## Multiplayer Status

**Untested in multiplayer.** The mod includes lock synchronization code but has not been validated in multiplayer environments.

**Expected behavior:**
- Should work when players use **separate containers** at different locations
- May have issues with **shared storage** that multiple players access
- Race conditions possible if players craft simultaneously from the same container

## Troubleshooting

### Mod doesn't work at all
1. Run `pc status` - Check if mod is enabled
2. Run `pc health` - Check for failed features
3. Ensure EAC is disabled
4. Check game log for `[ProxiCraft]` errors

### Specific feature not working
1. Run `pc fullcheck` for detailed diagnostics
2. Check if feature is enabled in config.json
3. Check if health check shows FAIL for that feature
4. If FAIL: Feature may need mod update for this game version

### Performance issues
1. Run `pc perf on`, play for a bit, then `pc perf report` to identify bottlenecks
2. Reduce `range` (default 15 is recommended)
3. Set `isDebug` to `false`
4. Disable features you don't use

### After game update
1. Run `pc health` - Check which features broke
2. Features showing [ADAPT] are auto-recovered
3. Features showing [FAIL] need mod update
4. Report issues with `pc fullcheck` output

## Potential Mod Conflicts

### High Risk (Will Conflict)
- **CraftFromChests / CraftFromContainersPlus** - Same functionality
- **AutoCraft** - Modifies crafting methods
- Other "craft from containers" mods

### Medium Risk (May Work)
- **SMXui / SMXhud** - UI overhauls
- **BiggerBackpack** - Changes inventory
- **ExpandedStorage** - Changes containers

### Low Risk
- **BetterVehicles** - May affect refuel
- Most mods not touching crafting/inventory

### Tested Compatible
- **JaWoodleUI** - UI overhaul
- **TechFreqsIncreasedBackpack** - Larger backpack

## Inspiration & Prior Art

ProxiCraft builds on concepts from:

| Mod | Author | Notes |
|-----|--------|-------|
| [CraftFromContainers](https://www.nexusmods.com/7daystodie/mods/2196) | aedenthorn | Original concept |
| [CraftFromContainers v1.0](https://www.nexusmods.com/7daystodie/mods/4970) | SYN0N1M | Game v1.0 port |
| BeyondStorage2 | superguru | Feature ideas |

### What's Different in ProxiCraft

| Feature | ProxiCraft | Others |
|---------|------------|--------|
| Startup Health Check | ✅ Validates all features | ❌ |
| Adaptive Recovery | ✅ Survives game updates | ❌ |
| Silent by Default | ✅ No spam | ❌ |
| Challenge Tracker | ✅ Full integration | ❌ |
| Stability Tiers | ✅ Documented risk levels | ❌ |

## Building

```powershell
dotnet build -c Release
```

Outputs:
- `Release/ProxiCraft/` - The mod folder
- `Release/ProxiCraft.zip` - Distribution package

## Changelog

### v1.2.0 - Features & Bug Fixes
**New Features:**
- **New**: HUD ammo counter - shows container ammo in weapon stat bar
- **New**: Locked slot respect - items in user-locked container slots excluded from all operations

**Bug Fixes:**
- **Fixed**: Radial menu reload - ammo greyed out when only in nearby containers *(reported by falkon311)*
- **Fixed**: R-key reload blocked when ammo only in containers *(reported by falkon311)*
- **Fixed**: Block upgrades not consuming materials from nearby containers *(reported by falkon311)*
- **Fixed**: Workstation output items counted but not consumed - crafting exploit *(reported by Kaizlin)*
- **Fixed**: "Take Like" button taking all container contents instead of matching items *(reported by Kaizlin)*

### v1.1.0 - Expanded Storage Sources
- Vehicle storage support - minibike, motorcycle, 4x4, gyrocopter
- Drone storage support - use items from drone cargo
- Dew collector support - water available for crafting
- Workstation output slots - forge, workbench, campfire, chemistry station
- Recipe tracker real-time updates from containers
- Performance profiler - `pc perf` commands for diagnostics
- Container cache performance optimizations

### v1.0.0 - Initial Public Release
- Core crafting from containers
- Weapon reload from containers
- Vehicle/generator refuel from containers
- Block repair from containers
- Lockpicking and item repair from containers
- Challenge tracker integration
- Startup health check system
- Console diagnostics (`pc` commands)

## License

MIT License - See [LICENSE](LICENSE) for details.

Released under MIT to allow community maintenance when game updates break functionality.
