# ProxiCraft - Copilot Instructions

## Project Overview
This is a 7 Days to Die mod that allows players to craft using items from nearby storage containers. It uses Harmony patching to modify game behavior at runtime.

## Directory Structure

```
ProxiCraft/
├── ProxiCraft/          # Source code folder
│   ├── ProxiCraft.cs    # Main mod class with Harmony patches
│   ├── ContainerManager.cs       # Container scanning and item management
│   ├── ModConfig.cs              # Configuration settings
│   ├── ConsoleCmdProxiCraft.cs  # Console commands (pc)
│   ├── ModCompatibility.cs       # Conflict detection
│   ├── SafePatcher.cs            # Safe patching utilities
│   ├── AdaptivePatching.cs       # Compatibility helpers
│   └── NetPackagePCLock.cs      # Multiplayer lock sync
├── Properties/
│   └── AssemblyInfo.cs           # Assembly metadata
├── Release/                      # DISTRIBUTION FOLDER
│   └── ProxiCraft/      # Ready-to-deploy mod package
│       ├── ProxiCraft.dll  # Compiled mod DLL (copy here after build)
│       ├── ModInfo.xml           # Mod metadata for game
│       └── config.json           # User configuration
├── obj/                          # Build intermediates (git-ignored)
├── ProxiCraft.csproj    # Project file
└── README.md                     # Documentation
```

## Build & Deploy Workflow

1. **Build**: `dotnet build -c Release`
   - Output: `Release\ProxiCraft\ProxiCraft.dll` (ready for distribution)

2. **Deploy to Game** (optional): If needed, copy the entire `Release\ProxiCraft\` folder to the game's Mods directory:
   - Game mods path: `C:\Steam\steamapps\common\7 Days To Die\Mods\`

## Important Notes

- **DO NOT** put the DLL directly in the game's Mods folder - it goes in `Release\ProxiCraft\` first
- The `Release\ProxiCraft\` folder is the complete mod package ready for distribution
- The game's Mods folder (`...\7 Days To Die\Mods\`) contains multiple different mods as subfolders
- Each mod needs its own subfolder with ModInfo.xml and the DLL

## Console Commands (in-game)

- `pc status` - Show mod status and configuration
- `pc test` - Test container scanning
- `pc diag` - Show diagnostic info (patch status)
- `pc debug` - Toggle debug logging
- `pc refresh` - Refresh container cache
- `pc conflicts` - Show mod conflicts

## Key Technical Details

- Target Framework: .NET 4.8
- Uses Harmony 2.x for runtime patching
- Patches `XUiM_PlayerInventory` methods for item counting
- Patches `XUiC_RecipeList.BuildRecipeInfosList` for UI highlighting
- Container scanning uses chunk-based TileEntity queries
