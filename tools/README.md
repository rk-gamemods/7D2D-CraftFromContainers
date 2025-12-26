# Development Tools

This folder contains tools to help with mod development.

## DecompileGameCode.ps1

A PowerShell script to decompile 7 Days to Die game assemblies into readable C# source code. This is essential for understanding game internals when developing mods.

### Prerequisites

1. **.NET SDK** - Required for the decompiler tool
   - Download from: https://dotnet.microsoft.com/download
   - Or via winget: `winget install Microsoft.DotNet.SDK.8`

2. **7 Days to Die** - The game must be installed
   - Default path: `C:\Steam\steamapps\common\7 Days To Die`

3. **ILSpyCmd** - Automatically installed by the script if missing
   - Manual install: `dotnet tool install -g ilspycmd`

### Usage

```powershell
# Basic usage (uses default Steam path)
.\tools\DecompileGameCode.ps1

# Custom game path
.\tools\DecompileGameCode.ps1 -GamePath "D:\Games\7 Days To Die"

# Force regenerate (overwrites existing)
.\tools\DecompileGameCode.ps1 -Force

# Custom output location
.\tools\DecompileGameCode.ps1 -OutputPath "C:\Temp\7D2DCode"
```

### Output

By default, decompiled code is placed in `../GameCode/` (relative to the script), which is gitignored. The output includes:

- `Assembly-CSharp/` - Main game code (~15,000+ files)
  - UI systems (XUi*, LocalPlayerUI)
  - Entity systems (EntityPlayer, TileEntity)
  - Inventory and containers
  - Challenges and quests
  
- `Assembly-CSharp-firstpass/` - Additional systems

- `0Harmony/` - Harmony patching library reference

### Key Classes for ProxiCraft

When developing this mod, these are the important classes to search for:

| Class | Purpose |
|-------|---------|
| `TileEntityLootContainer` | Basic loot containers |
| `TileEntitySecureLootContainer` | Lockable containers |
| `TileEntityComposite` | Modern composite containers (V1.0+) |
| `TEFeatureStorage` | Storage feature for composites |
| `ITileEntityLootable` | Interface for lootable containers |
| `XUiM_PlayerInventory` | Player inventory manager |
| `XUiC_RecipeList` | Recipe list UI |
| `ChallengeObjectiveGather` | Gather challenge objectives |
| `ItemStack` | Item stack with drag/drop events |

### Searching the Codebase

Once decompiled, use VS Code's search (Ctrl+Shift+F) to find:

```
# Find all classes implementing an interface
: ITileEntityLootable

# Find method definitions
public void RemoveItem

# Find Harmony patch targets
GetItemCount
```

### ⚠️ Important Notes

1. **Do NOT commit decompiled code** - It's copyrighted game content
2. **For personal reference only** - Use to understand game mechanics
3. **Regenerate after game updates** - Game code changes between versions
4. **Takes 2-5 minutes** - Be patient during decompilation

### Troubleshooting

**"Game path not found"**
- Verify your 7D2D installation path
- Use `-GamePath` parameter with the correct path

**"ILSpyCmd failed to install"**
- Ensure .NET SDK is installed
- Try manual install: `dotnet tool install -g ilspycmd`
- Restart your terminal after installation

**"Access denied"**
- Run PowerShell as Administrator
- Or choose a different output path you have write access to
