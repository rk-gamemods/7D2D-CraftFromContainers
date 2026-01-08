# ProxiCraft

A 7 Days to Die mod that allows crafting, reloading, refueling, and repairs using items from nearby storage containers.

**[Nexus Mods](https://www.nexusmods.com/7daystodie/mods/9269)** • **[GitHub](https://github.com/rk-gamemods/7D2D-ProxiCraft)**

## ⬇️ [Download ProxiCraft-1.2.7.zip](https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft-1.2.7.zip)

---

## Features

### Single-Player Features (Stable ✅)

Use items from nearby containers for:

| Feature | Description |
|---------|-------------|
| **Crafting** | Use materials from nearby storage for crafting recipes |
| **Block Repair/Upgrade** | Repair and upgrade blocks using container materials |
| **Weapon Reload** | Reload weapons with ammo from containers |
| **Vehicle Refuel** | Refuel vehicles using gas cans from containers |
| **Generator Refuel** | Refuel generators from nearby storage |
| **Item Repair** | Use repair kits from containers to fix weapons/tools |
| **Lockpicking** | Use lockpicks from containers to pick locks |
| **Painting** | Use paint from containers with paint brush |
| **Trader Purchases** | Pay with dukes stored in containers |
| **Challenge Tracker** | Container items count toward "Gather X" challenges |
| **HUD Ammo Counter** | Shows total container ammo in weapon stat bar |
| **Recipe Tracker** | Real-time ingredient counts from containers |
| **Locked Slot Respect** | Items in locked container slots are excluded |

**Storage Sources:**
- Standard containers (chests, boxes, storage crates)
- Vehicle storage (minibike, motorcycle, 4x4, gyrocopter)
- Drone cargo compartment
- Dew collector contents
- Workstation output slots (forge, campfire, chemistry station)

### Multiplayer Features (🧪 EXPERIMENTAL)

ProxiCraft includes experimental multiplayer support with automatic crash protection.

**Requirements:** ProxiCraft must be installed on BOTH server AND all clients (same version).

**How It Works - "Guilty Until Proven Innocent":**

```
┌─────────────────────────────────────────────────────────────┐
│ Multiplayer Safety Flow                                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Client connects to server                                  │
│  ├─ multiplayerImmediateLock = true? (default)              │
│  │   └─ YES → IMMEDIATE LOCK (storage disabled) ↓           │
│  │                                                          │
│  │   Server sends handshake request                         │
│  │   ├─ Client responds (has ProxiCraft)?                   │
│  │   │   └─ YES → UNLOCK → Normal operation ✓               │
│  │   └─ NO response after timeout?                          │
│  │       └─ STAY LOCKED + show culprit name ✗               │
│  │                                                          │
│  └─ NO → Trust mode (honor system) ⚠️                       │
│      └─ No lock, relies on all players having mod           │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

**Safety Features:**
- **Immediate Lock** - Storage access blocked instantly when any client connects (zero crash window)
- **Quick Unlock** - Resumes in ~100-300ms after verifying client has mod
- **Culprit Identification** - Shows exactly which player needs to install the mod
- **Auto Re-enable** - Mod unlocks when the player without ProxiCraft disconnects
- **Server Config Sync** - Clients automatically use server's settings

**Safety Settings (config.json):**

```json
{
  "multiplayerImmediateLock": true,
  "multiplayerHandshakeTimeoutSeconds": 10
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `multiplayerImmediateLock` | true | Lock mod when clients connect. Set to `false` for honor system (NOT RECOMMENDED). |
| `multiplayerHandshakeTimeoutSeconds` | 10 | How long to wait before declaring a player doesn't have the mod. |

⚠️ **WARNING:** Setting `multiplayerImmediateLock=false` removes crash protection. Only use on moderated servers where you enforce mod installation externally.

**Settings Sync Behavior:**
- **Server-synced:** `range`, all `pullFrom*`, all `enableFor*`, `storagePriority`, `respectLockedSlots`, `allowLockedContainers`
- **Local-only (never synced):** `isDebug`, `modEnabled`, `verboseHealthCheck`, `multiplayerImmediateLock`, `multiplayerHandshakeTimeoutSeconds`, all `enhancedSafety*` settings

Debug logging cannot be enabled remotely by a server - each player controls their own logging.

**Testing Status:**
- Single player ✅
- Basic dedicated server ✅
- Co-op hosting ⚠️ (needs more testing)
- High-latency connections ⚠️ (needs more testing)
- Large player counts (8+) ⚠️ (needs more testing)

**🐛 Please Report Bugs!** Multiplayer has many edge cases. Run `pc fullcheck` - it saves `fullcheck_report.txt` to the mod folder. Attach that file to your report on GitHub or Nexus.

---

## Installation

### Single-Player

1. Download the zip using the link above
2. Extract to `7 Days To Die/Mods/ProxiCraft/`
3. Launch game with EAC disabled

### Multiplayer

1. Install on the **server**
2. Install on **ALL clients** (same version)
3. If hosting co-op: the host IS the server, so all players need the mod

---

## Configuration

Edit `config.json` in the mod folder:

```json
{
  "modEnabled": true,
  "isDebug": false,
  "range": 15,

  "pullFromVehicles": true,
  "pullFromDrones": true,
  "pullFromDewCollectors": true,
  "pullFromWorkstationOutputs": true,
  "allowLockedContainers": true,

  "storagePriority": {
    "Drone": "1",
    "DewCollector": "2",
    "Workstation": "3",
    "Container": "4",
    "Vehicle": "5"
  },

  "enableForCrafting": true,
  "enableForQuests": true,
  "enableForRepairAndUpgrade": true,
  "enableForLockpicking": true,
  "enableForItemRepair": true,
  "enableForPainting": true,
  "enableForReload": true,
  "enableForRefuel": true,
  "enableForTrader": true,
  "enableForGeneratorRefuel": true,

  "enableHudAmmoCounter": true,
  "enableRecipeTrackerUpdates": true,
  "respectLockedSlots": true
}
```

### Key Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `range` | 15 | Search radius in blocks. Use 5 (same room), 15 (same floor), 30 (entire building), or -1 (unlimited). |
| `isDebug` | false | Enable verbose logging to `pc_debug.log` in the mod folder. Use for troubleshooting. |
| `storagePriority` | See above | Lower number = checked first. Items pulled from first available source. |
| `respectLockedSlots` | true | Skip items in user-locked container slots. |
| `pullFromWorkstationOutputs` | true | Only OUTPUT slots counted (not input/fuel/tool). |

---

## Console Commands

Open console with F1:

| Command | Description |
|---------|-------------|
| `pc status` | Show mod status, config, and multiplayer state |
| `pc health` | Show startup health check results |
| `pc test` | Test container scanning (shows what's found) |
| `pc fullcheck` | Full diagnostic (saves to `fullcheck_report.txt`) |
| `pc conflicts` | Check for mod conflicts |
| `pc toggle` | Enable/disable mod temporarily |
| `pc debug` | Toggle debug logging (writes to `pc_debug.log` in mod folder) |
| `pc reload` | Reload config from file |
| `pc perf on/off/report` | Performance profiling |

### Configuration Commands

```
pc config list              # List all settings
pc set range 30             # Change range to 30
pc config save              # Save changes to file
```

---

## Troubleshooting

### Single-Player Issues

1. **Check status:** `pc status` - Is mod enabled?
2. **Check health:** `pc health` - Are features working?
3. **Test scanning:** `pc test` - Can mod see containers?
4. **Full report:** `pc fullcheck` - Saves `fullcheck_report.txt` for bug reports
5. **Enable debug:** `pc debug` or set `isDebug: true` - Writes detailed logs to `pc_debug.log` in mod folder

### Multiplayer Issues

| Symptom | Solution |
|---------|----------|
| "Multiplayer: LOCKED" in `pc status` | A player doesn't have ProxiCraft installed. Check who. |
| Features don't work for some players | Ensure ALL players have same mod version |
| Settings different than expected | Server settings override client. Check server's config.json |
| CTD on player join | Run `pc fullcheck`, attach `fullcheck_report.txt` |

---

## Technical Details

<details>
<summary>Click to expand technical documentation</summary>

### Stability Philosophy

ProxiCraft is designed to survive game updates:

| Tier | Features | Risk |
|------|----------|------|
| **Stable** | Crafting, Quests, Block Repair, Lockpicking, Item Repair, Painting, HUD, Recipe Tracker | Low - Simple postfix patches |
| **Less Stable** | Reload, Vehicle Refuel, Generator Refuel, Trader | Medium - Transpiler patches |
| **Storage Sources** | Vehicles, Drones, Dew Collectors, Workstations | Low - Read-only, no patches |

**Startup Health Check:** Validates all 24 patches on game load. Silent when OK, warns on issues.

**Adaptive Recovery:** If game updates change method names, the mod attempts to find renamed methods automatically.

### Design Decisions

**Workstation Slots:** Only OUTPUT slots are counted. Input (being processed), fuel (being burned), and tool slots (in use) are ignored.

**Virtual Inventory Architecture:** All storage operations flow through `VirtualInventoryProvider` - centralizes multiplayer safety, ensures consistent behavior, enables global bug fixes.

### Project Structure

```
ProxiCraft/
├── ProxiCraft/
│   ├── ProxiCraft.cs              # Main mod, Harmony patches
│   ├── ContainerManager.cs        # Container scanning
│   ├── VirtualInventoryProvider.cs # Central inventory hub (MP-safe)
│   ├── MultiplayerModTracker.cs   # MP handshake and safety
│   ├── ModConfig.cs               # Configuration
│   └── ConsoleCmdProxiCraft.cs    # Console commands
└── Release/ProxiCraft/            # Distribution package
```

### Building

```powershell
dotnet build -c Release
```

</details>

---

## Inspiration & Prior Art

ProxiCraft builds on concepts from these mods:

| Mod | Author | Notes |
|-----|--------|-------|
| [CraftFromContainers](https://www.nexusmods.com/7daystodie/mods/2196) | aedenthorn | Original concept for A20 |
| [BeyondStorage2](https://www.nexusmods.com/7daystodie/mods/7809) | gazorper ([GitHub](https://github.com/superguru/7d2d_mod_BeyondStorage2)) | Expanded features, storage priority |

If you prefer their versions, check them out! ProxiCraft is a from-scratch implementation with different architecture.

**Use only ONE craft-from-containers mod.** Remove CraftFromContainers, BeyondStorage2, or similar mods before using ProxiCraft.

---

## Changelog

### v1.2.7 - Network Stability Improvements

**Fixed:**
- Fixed multiplayer handshake packet loss causing mod to lock up for entire server session
- Added retry mechanism for client handshake (1-second intervals until timeout)
- Added connection state validation before sending network packets
- Added network latency diagnostics for troubleshooting slow connections
- Improved error handling in packet deserialization

### v1.2.6 - Config File Bug Fix

**Fixed:**
- Fixed config settings being overwritten on game load
- Fixed race condition in config file watcher
- Fixed potential integer overflow in item count cache

### v1.2.5 - Hosting Panel Compatibility

**Fixed:**
- Fixed mod failing to load on some dedicated server hosting panels (CubeCoders AMP, etc.)

**For Server Hosts (if auto-detection still fails):**

You can manually tell ProxiCraft where it's installed by setting an environment variable called `PROXICRAFT_PATH` to the full folder path (e.g., `C:\GameServers\7DaysToDie\Mods\ProxiCraft`).

**Windows 10/11:**
1. Press `Win + R` to open Run dialog
2. Type `sysdm.cpl` and press Enter
3. Click the **Advanced** tab
4. Click **Environment Variables** button at the bottom
5. Under "System variables", click **New**
6. Variable name: `PROXICRAFT_PATH`
7. Variable value: Full path to your ProxiCraft folder
8. Click OK on all dialogs
9. Restart your server

**Other platforms:** Search your hosting panel or OS documentation for "environment variables".

### v1.2.4 - Enhanced Safety Fix

**Fixed:**
- Fixed enhanced safety mode breaking features (repair, reload, etc.) when switching between game sessions
- Multiplayer state now properly resets when starting a new game, preventing stale flags from blocking functionality

### v1.2.3 - Configuration Defaults Update

**Changed:**
- Enhanced Safety Mode now enabled by default (recommended for multiplayer stability)
- All `enhancedSafety*` settings default to `true` for new installations
- Configuration documentation expanded with all 30+ settings comprehensively documented

**Note:** Existing users with custom `config.json` files are unaffected. Only fresh installations will use the new defaults.

### v1.2.2 - Hotfix

**Fixed:**
- Health check report now shows all features (VehicleRepair, HudAmmoCounter, RecipeTracker, TraderSelling, LockedSlots were missing from grouped output)

### v1.2.1 - Virtual Inventory Architecture & Multiplayer Safety

**New:**
- 🧪 **EXPERIMENTAL Multiplayer Support** with Virtual Inventory architecture
- **Zero-Crash Protection** - "Guilty Until Proven Innocent" instant lock on client connect *(GreenGhost21, optimus0, GeeButtersnaps)*
- **Server Config Sync** - Clients use server's settings automatically
- **Configurable Storage Priority** - Control search order

**Fixed:**
- `pc help` command loop
- Vehicle repair kit loss with full inventory
- Duplicate profiler timer calls

### v1.2.0 - Features & Bug Fixes

**New:**
- HUD ammo counter for container ammo
- Locked slot respect

**Fixed:**
- Radial menu reload greyed out *(falkon311)*
- R-key reload blocked *(falkon311)*
- Block upgrade material consumption *(falkon311)*
- Workstation output crafting exploit *(Kaizlin)*
- "Take Like" button behavior *(Kaizlin)*

### v1.1.0 - Expanded Storage Sources

- Vehicle, drone, dew collector, workstation output support
- Recipe tracker real-time updates
- Performance profiler and optimizations

### v1.0.0 - Initial Release

---

## License

MIT License - See [LICENSE](LICENSE) for details.
