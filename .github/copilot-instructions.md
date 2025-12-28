# ProxiCraft - Copilot Instructions

## Project Overview

ProxiCraft is a 7 Days to Die mod that allows players to use items from nearby storage containers for crafting, reloading, refueling, and more. It uses Harmony patching to modify game behavior at runtime.

**Current Version:** 1.1.0  
**Game Version:** 7 Days to Die V2.5 (tested)  
**Framework:** .NET 4.8, C# 12, Harmony 2.x

---

## Workspace Structure

```
7D2DMods/                         # WORKSPACE ROOT (parent of ProxiCraft repo)
├── DecompileGameCode.ps1         # Script to decompile game DLLs
├── 7D2DCodebase/                 # Decompiled game source (reference only)
│   ├── Assembly-CSharp/          # Main game code (~4000+ .cs files)
│   └── Assembly-CSharp-firstpass/
├── temp_analysis/                # Temporary folder for analyzing other mods
│   └── <SomeMod>/                # Downloaded mods and decompiled source (git-ignored)
└── ProxiCraft/                   # THIS REPOSITORY
    ├── ProxiCraft/               # Source code folder
    │   ├── ProxiCraft.cs         # Main mod class, Harmony patches, entry point
    │   ├── ContainerManager.cs   # Container scanning, caching, item counting
    │   ├── ModConfig.cs          # Configuration loading/saving
    │   ├── ConsoleCmdProxiCraft.cs  # Console commands (pc status, pc test, etc.)
    │   ├── StartupHealthCheck.cs # Validates patches work on game load
    │   ├── SafePatcher.cs        # Error-wrapped Harmony patching
    │   ├── AdaptiveMethodFinder.cs  # Finds methods even if renamed
    │   ├── RobustTranspiler.cs   # Safe IL manipulation utilities
    │   ├── PerformanceProfiler.cs   # Timing and cache statistics
    │   ├── ModCompatibility.cs   # Conflict detection with other mods
    │   ├── AdaptivePatching.cs   # Compatibility helpers
    │   └── NetPackagePCLock.cs   # Multiplayer lock sync
    ├── Properties/
    │   └── AssemblyInfo.cs
    ├── Release/                  # DISTRIBUTION FOLDER
    │   └── ProxiCraft/           # Ready-to-deploy mod package
    │       ├── ProxiCraft.dll    # Compiled mod
    │       ├── ModInfo.xml       # Mod metadata for game
    │       └── config.json       # Default configuration
    ├── .github/
    │   └── copilot-instructions.md  # THIS FILE
    ├── ProxiCraft.csproj
    ├── README.md
    ├── NEXUS_DESCRIPTION.txt     # Nexus Mods page description (BBCode)
    ├── TECHNICAL_REFERENCE.md    # Deep technical documentation
    ├── RESEARCH_NOTES.md         # Development notes and debugging history
    └── INTEGRATION_PLAN.md       # Feature planning documents
```

---

## Build & Deploy Workflow

### Building
```powershell
cd C:\Users\Admin\Documents\GIT\GameMods\7D2DMods\ProxiCraft
dotnet build -c Release
```
- Output goes directly to `Release\ProxiCraft\ProxiCraft.dll`
- The csproj is configured with `<OutputPath>Release\ProxiCraft\</OutputPath>`

### Testing In-Game
1. Copy `Release\ProxiCraft\` folder to `C:\Steam\steamapps\common\7 Days To Die\Mods\`
2. Launch game with EAC disabled
3. Use console commands (`pc status`, `pc test`, `pc health`) to verify

### Creating Release Package
The `Release\ProxiCraft\` folder IS the release package. Zip it for distribution:
```powershell
Compress-Archive -Path .\Release\ProxiCraft -DestinationPath .\Release\ProxiCraft.zip -Force
```

---

## Development Workflows

### Analyzing Other Mods (for feature inspiration or compatibility)

1. **Download** the mod to `temp_analysis/<ModName>/`
2. **Decompile** using ILSpyCmd:
   ```powershell
   ilspycmd "temp_analysis\ModName\ModName.dll" -p -o "temp_analysis\ModName_src" -lv Latest
   ```
3. **Analyze** the source to understand:
   - What methods they patch (for conflict detection)
   - How they implement features (for inspiration)
   - What config options they expose
4. **Clean up** after analysis (temp_analysis is git-ignored)

### Decompiling Game Code

The `7D2DCodebase` folder is its own git repository - each decompile becomes a commit, allowing you to diff between game versions.

**First time or after game update:**
```powershell
cd C:\Users\Admin\Documents\GIT\GameMods\7D2DMods
.\DecompileGameCode.ps1
```

The script will:
1. Detect game version automatically
2. Decompile assemblies
3. Commit with game version as message
4. Show diff summary of what changed (if previous version exists)

**Comparing game versions:**
```powershell
cd 7D2DCodebase
git log --oneline                           # See version history
git diff HEAD~1 --stat                      # Summary of changed files
git diff HEAD~1 -- XUiM_PlayerInventory.cs  # Specific file diff
```

Output: `7D2DCodebase/Assembly-CSharp/` with ~4000+ .cs files

**When to regenerate:**
- After game updates (method signatures may change)
- When implementing new features that touch unfamiliar game systems
- When debugging unexpected behavior

### After Game Updates

1. **Regenerate decompiled code:**
   ```powershell
   .\DecompileGameCode.ps1 -Force
   ```

2. **Run startup health check in-game:**
   ```
   pc health
   pc fullcheck
   ```

3. **Check for broken patches:**
   - Look for "FAILED" status in health check output
   - Check if method signatures changed in decompiled code
   - Use `AdaptiveMethodFinder` to locate renamed methods

4. **Common breaking changes:**
   - Method parameter types changed
   - Methods moved to different classes
   - Field names changed (use reflection patterns that search by type)

---

## Console Commands Reference

In-game console (F1):

| Command | Description |
|---------|-------------|
| `pc status` | Show mod status and configuration |
| `pc health` | Show startup health check results |
| `pc test` | Test container scanning (shows what's found) |
| `pc diag` | Full diagnostic report |
| `pc fullcheck` | Complete diagnostic for bug reports |
| `pc conflicts` | Check for mod conflicts |
| `pc toggle` | Enable/disable the mod |
| `pc refresh` | Clear container cache |
| `pc perf` | Show performance profiler status |
| `pc perf on` | Enable performance profiling |
| `pc perf off` | Disable performance profiling |
| `pc perf reset` | Reset profiler statistics |
| `pc perf report` | Show detailed timing report (also saves to file) |
| `pc perf save` | Save report to perf_report.txt |

---

## Key Technical Patterns

### Container Types and How to Access Them

| Container Type | Class | Access Pattern |
|---------------|-------|----------------|
| Storage crates | `TileEntityComposite` | `GetFeature<TEFeatureStorage>().items` |
| Secure loot | `TileEntitySecureLootContainer` | Direct `items` array |
| Vehicles | `EntityVehicle` | `vehicle.GetAttachedBag().GetSlots()` |
| Drones | `EntityDrone` | `drone.lootContainer.items` (same as `bag.GetSlots()`) |
| Dew collectors | `TileEntityCollector` | `collector.items` via `ITileEntityLootable` |
| Workstations | `TileEntityWorkstation` | `workstation.Output` (slots 0-5 typically) |

### Harmony Patching Strategy

- **Priority.Low** on all patches - let other mods run first
- **Postfix over Prefix** - add to results, don't replace
- **Postfix over Transpiler** - less brittle to game updates
- **SafePatcher wrapper** - catches exceptions per-patch

### Cache Management

- `ContainerManager` caches container references with 100ms TTL
- `GetItemCount` uses item count cache with 100ms TTL
- `RefreshStorages()` rebuilds cache on demand
- Cache invalidated when player moves significantly

### Challenge Tracker Integration (Complex!)

The challenge tracker was the hardest feature to implement. Key lessons:

1. **NEVER fire `OnBackpackItemsChangedInternal` during item transfers** - causes item duplication!
2. **Use `DragAndDropItemChanged` instead** - challenges already subscribe to it
3. **Cache open container reference** when UI opens
4. **SET the `Current` field** in `HandleUpdatingCurrent`, don't just calculate
5. **Workstation slots**: Only count OUTPUT slots (0-5), not Input/Fuel/Tool

---

## Performance Optimization Lessons

### What Works
- **Squared distance checks** - avoid `Math.Sqrt()` in hot paths
- **Direct dictionary iteration** - avoid `dict.Keys.ToArray()` allocations
- **For loops over foreach** - avoid enumerator allocations
- **Cache hit rates** - aim for >95% cache hit rate

### Real-World Performance (AMD Ryzen 9800X3D reference)
- Cold cache: ~2.4ms for full scan
- Warm cache: ~0.16ms average
- Cache hit rate: ~98.7%

### When Performance Matters
- `GetItemCount` is called frequently during crafting UI
- `RefreshStorages` runs when player opens crafting
- Recipe list building iterates all recipes

---

## Documentation Files

| File | Purpose |
|------|---------|
| `README.md` | User-facing documentation, feature list |
| `NEXUS_DESCRIPTION.txt` | Nexus Mods page (BBCode format) |
| `TECHNICAL_REFERENCE.md` | Deep technical documentation |
| `RESEARCH_NOTES.md` | Development history, debugging notes |
| `INTEGRATION_PLAN.md` | Feature planning and design |

When updating features:
1. Update `README.md` with user-facing changes
2. Update `NEXUS_DESCRIPTION.txt` changelog section
3. Update `TECHNICAL_REFERENCE.md` for technical details
4. Bump version in `ModInfo.xml` and `AssemblyInfo.cs`

---

## Git Workflow

### Branch Strategy
- `master` - stable releases
- `feature/*` - feature development branches

### Typical Flow
```powershell
git checkout -b feature/new-feature
# ... develop and test ...
git add -A; git commit -m "Add new feature"; git push -u origin feature/new-feature
# ... when ready ...
git checkout master; git merge feature/new-feature; git push
```

---

## Lessons Learned

### Things That Break Easily
1. **Transpilers** - IL injection is fragile, use postfix when possible
2. **Field access by name** - fields get renamed, search by type instead
3. **Specific method signatures** - use `AdaptiveMethodFinder` for resilience
4. **UI window lookups** - `xui.WindowGroups` only has MENU windows, not in-game UI

### Things That Cause Bugs
1. **Firing backpack events during transfers** → item duplication
2. **Reading `TileEntity.items` while UI open** → stale data
3. **Not null-checking everything** → random crashes
4. **Counting workstation input slots** → double-counting materials

### Debugging Tips
1. Use `pc fullcheck` for comprehensive diagnostics
2. Check `output_log.txt` in game folder for errors
3. Enable `isDebug: true` in config.json for verbose logging
4. Use `pc perf on` to profile performance issues

### Game Code Quirks
- `xui.lootContainer` is only set when looting window is open
- `lockedTileEntities` dictionary tracks who has containers open
- `EntityDrone.lootContainer.items` and `drone.bag.GetSlots()` are the SAME array
- Workstation slots: Input=varies, Output=0-5 typically, Fuel=last slots
