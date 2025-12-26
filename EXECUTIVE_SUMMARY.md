# ProxiCraft - Executive Summary

## Overview

This document summarizes the comprehensive rewrite that resulted in the ProxiCraft mod for 7 Days to Die, transforming a functional but fragile single-file implementation into a robust, maintainable, and highly compatible modular codebase with **new features** not present in any prior version.

**Inspired By:** CraftFromContainers community lineage (llmonmonll → aedenthorn → SYN0N1M → others)
**This Version:** 1.0.0
**Date:** December 2024
**Repository:** [github.com/rk-gamemods/7D2D-ProxiCraft](https://github.com/rk-gamemods/7D2D-ProxiCraft)

---

## What's New in v2.0 (Not in Prior Versions)

✅ **Challenge Tracker Integration** - Container items count toward challenges like "Gather 4 Wood"
✅ **Real-time Updates** - Challenge counts update immediately when moving items between inventory and storage
✅ **SafePatcher System** - Robust error handling prevents mod errors from crashing the game
✅ **ModCompatibility Detection** - Automatic conflict detection with other mods
✅ **Comprehensive Documentation** - Full research notes, technical guides, and event documentation

---

## Why This Refactoring Was Needed

### Original Code Issues

1. **Single Monolithic File** - All 1,161 lines in one file made maintenance difficult
2. **Limited Error Handling** - Exceptions could crash the game or cause silent failures
3. **No Conflict Detection** - Users had no way to diagnose mod conflicts
4. **Fragile Transpilers** - IL-level patches broke when other mods touched the same methods
5. **No Debugging Tools** - Troubleshooting required log file analysis
6. **Backpack Mod Incompatibility** - Invasive patching conflicted with inventory mods

---

## Architectural Improvements

### Modular Code Structure

| File | Purpose | Lines |
|------|---------|-------|
| `ProxiCraft.cs` | Main mod entry, Harmony patches | ~900 |
| `ContainerManager.cs` | Container scanning, item management | ~300 |
| `ModConfig.cs` | Configuration with XML documentation | ~80 |
| `ModCompatibility.cs` | Conflict detection, diagnostics | ~380 |
| `AdaptivePatching.cs` | Dynamic patch strategy selection | ~200 |
| `SafePatcher.cs` | Error-wrapped Harmony operations | ~200 |
| `ConsoleCmdProxiCraft.cs` | In-game console commands | ~270 |
| `NetPackagePCLock.cs` | Multiplayer synchronization | ~50 |

**Total: 8 focused files vs 1 monolithic file**

### Separation of Concerns

```
┌─────────────────────────────────────────────────────────────┐
│                        ProxiCraft                            │
│                  (Entry Point & Patches)                     │
└─────────────────────┬───────────────────────────────────────┘
                      │
        ┌─────────────┼─────────────┐
        ▼             ▼             ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────┐
│ ContainerMgr  │ │ ModCompat     │ │ SafePatcher   │
│ (Business)    │ │ (Diagnostics) │ │ (Reliability) │
└───────────────┘ └───────────────┘ └───────────────┘
```

---

## Technical Improvements

### 1. Comprehensive Error Handling

**Before:**
```csharp
// No error handling - exceptions propagate and crash
var items = GetStorageItems();
items.AddRange(containerItems);
```

**After:**
```csharp
try
{
    var items = ContainerManager.GetStorageItems(Config);
    if (items.Count > 0)
    {
        combined.AddRange(items);
        LogDebug($"Added {items.Count} item stacks from containers");
    }
}
catch (Exception ex)
{
    LogWarning($"Error adding container items: {ex.Message}");
    // Graceful degradation - return original items unchanged
}
```

**Benefits:**
- Game never crashes due to mod errors
- Errors are logged with context for troubleshooting
- Features degrade gracefully (disable broken feature, keep others working)

### 2. Structured Logging System

Four log levels with configurable debug mode:

| Level | Method | When Used |
|-------|--------|-----------|
| Info | `Log()` | Normal operations, startup messages |
| Debug | `LogDebug()` | Detailed tracing (only when `isDebug=true`) |
| Warning | `LogWarning()` | Recoverable issues, compatibility notices |
| Error | `LogError()` | Critical failures requiring attention |

**Example Output:**
```
[ProxiCraft] v2.0.0 initialized with 12 patches
[ProxiCraft] Found 1 backpack mod(s) - using additive patching
[ProxiCraft] [DEBUG] Added 47 item stacks from containers
[ProxiCraft] [Warning] XUiC_RecipeList has Transpiler conflict - using Postfix fallback
```

### 3. Adaptive Patching System

The mod now dynamically selects patching strategies based on detected conflicts:

```csharp
public enum PatchStrategy
{
    Full,        // Use Transpiler (fastest, but fragile)
    PostfixOnly, // Use Postfix only (safer with conflicts)
    Careful,     // Extra null checks enabled
    Skip         // Don't patch (incompatible)
}
```

**Decision Flow:**
1. Check if method exists with expected signature
2. Check if other mods have Transpilers on same method
3. Select safest compatible strategy
4. Log decision for diagnostics

### 4. Backpack Mod Compatibility

**The Core Problem:**
- Backpack mods modify inventory structure (add slots, change bag size)
- Original mods modified inventory methods directly
- Both mods fighting over same methods = crashes

**The Solution: Additive-Only Patching**

| Operation | What We Do | Why It's Safe |
|-----------|------------|---------------|
| Get items | Append to result | We don't modify source |
| Count items | Add to count | Original count preserved |
| Has items | Only supplement | Only act if inventory says "no" |
| Remove items | Remove remainder | Inventory removes first |

**Recognized Compatible Mods:**
- BiggerBackpack
- 60SlotBackpack / 96SlotBackpack
- BackpackExpansion
- ExtendedBackpack
- LargerBackpack

### 5. Conflict Detection & Reporting

Automatic detection at startup:

```
=== POTENTIAL MOD CONFLICTS DETECTED (2) ===
  [High Risk Conflict] CraftFromChests
    Issue: Similar mod - will conflict with same patches
    Tip: RECOMMENDED: Disable 'CraftFromChests' as it provides similar functionality.
  [Harmony Patch Conflict] SomeOtherMod
    Issue: Method 'XUiM_PlayerInventory.GetAllItemStacks' is also patched
    Tip: This may cause unexpected behavior. Check if features work correctly.
=== END CONFLICT REPORT ===
```

### 6. In-Game Console Commands

New `pc` command for runtime diagnostics:

| Command | Description |
|---------|-------------|
| `pc status` | Show mod status and configuration |
| `pc diag` | Full diagnostic report |
| `pc test` | Test container scanning |
| `pc conflicts` | Show detected conflicts |
| `pc toggle` | Enable/disable mod temporarily |
| `pc debug` | Toggle debug logging |

**Example `pc diag` Output:**
```
=== ProxiCraft Diagnostic Report ===
Mod Version: 2.0.0
Game Version: Alpha 21.2 (b37)
Config Enabled: True

--- Backpack Mod Compatibility ---
  Detected 1 backpack mod(s):
    ✓ BiggerBackpack (compatible - using additive patching)

--- Patch Status ---
Successful: 11, Failed: 1
  ✓ GameManager_StartGame_Patch -> GameManager.StartGame
  ✓ ItemActionEntryCraft_OnActivated_Patch -> ItemActionEntryCraft.OnActivated
  ✗ XUiC_RecipeList_Update_Patch -> XUiC_RecipeList.Update
      Error: Method signature changed by UI mod

--- Feature Status ---
  Crafting from containers: Enabled
  Repair/Upgrade: Enabled
  Trader purchases: Enabled
  Vehicle refuel: Enabled
  Weapon reload: Enabled
  Vehicle storage: Enabled
  Range: -1 (unlimited)
=== End Report ===
```

---

## Performance Improvements

### Container Scanning Cache

**Before:** Scanned all containers on every crafting check (expensive)

**After:** 
- 100ms cache cooldown between scans
- Position-based invalidation (only rescan when player moves)
- Lazy initialization of storage lists

```csharp
private static float _lastScanTime;
private static Vector3 _lastScanPosition;
private const float CACHE_DURATION = 0.1f; // 100ms

public static List<ItemStack> GetStorageItems(ModConfig config)
{
    float currentTime = Time.time;
    Vector3 playerPos = GetPlayerPosition();
    
    // Use cache if valid
    if (currentTime - _lastScanTime < CACHE_DURATION && 
        Vector3.Distance(playerPos, _lastScanPosition) < 1f)
    {
        return _cachedItems;
    }
    
    // Perform new scan...
}
```

---

## Quality of Life Improvements

### 1. Self-Documenting Configuration

```json
{
  "modEnabled": true,          // Master toggle for the entire mod
  "isDebug": false,            // Enable verbose logging (disable for performance)
  "enableForRepairAndUpgrade": true,  // Use container items for repairs
  "enableForTrader": true,     // Use container currency at traders
  "enableForRefuel": true,     // Use container fuel for vehicles
  "enableForReload": true,     // Use container ammo for weapons
  "enableFromVehicles": true,  // Include vehicle storage in searches
  "allowLockedContainers": true,  // Access containers others have open
  "range": -1                  // Max distance (-1 = unlimited)
}
```

### 2. Multiplayer Synchronization

Container lock state is now properly broadcast to all clients:
- Prevents race conditions when multiple players craft
- Uses custom `NetPackagePCLock` for efficient sync

### 3. Developer-Friendly Structure

- XML documentation on all public methods
- Clear separation for future enhancements
- Analysis folder with decompiled game code for reference

---

## Migration Path

For users upgrading from v1.x:

1. **Backup** existing `config.json` (settings are compatible)
2. **Replace** `ProxiCraft.dll` with new version
3. **Delete** old source files if present
4. **Test** with `pc status` command in-game

---

## Summary of Benefits

| Aspect | Before | After |
|--------|--------|-------|
| **Stability** | Crashes on errors | Graceful degradation |
| **Compatibility** | Conflicts common | Adaptive, backpack-friendly |
| **Debugging** | Log file only | In-game console commands |
| **Maintainability** | 1 file, 1161 lines | 8 focused modules |
| **Performance** | Scan every check | Cached with invalidation |
| **Transparency** | Silent failures | Detailed diagnostics |

---

## Files Changed

```
ProxiCraft/
├── ProxiCraft/           # Source code
│   ├── ProxiCraft.cs     # Main mod with Harmony patches
│   ├── ContainerManager.cs        # Container scanning and item management
│   ├── ModConfig.cs               # Configuration settings
│   ├── SafePatcher.cs             # Safe patching utilities
│   ├── AdaptivePatching.cs        # Compatibility helpers
│   ├── ModCompatibility.cs        # Conflict detection
│   ├── ConsoleCmdProxiCraft.cs  # Console commands (pc)
│   └── NetPackagePCLock.cs       # Multiplayer lock sync
├── Release/ProxiCraft/   # Ready-to-deploy mod package
│   ├── ProxiCraft.dll    # Compiled mod
│   ├── ModInfo.xml                # Mod metadata
│   └── config.json                # User configuration
├── tools/                         # Development utilities
│   ├── DecompileGameCode.ps1      # Game code decompiler script
│   └── README.md                  # Tool documentation
├── RESEARCH_NOTES.md              # Development history and debugging notes
├── INVENTORY_EVENTS_GUIDE.md      # Technical reference for modders
└── LICENSE                        # MIT License with attribution
```

---

*Developed with assistance from GitHub Copilot - December 2024*
*Last updated: December 27, 2025*
