# ProxiCraft Release Process

## TL;DR - Quick Release Checklist

For AI assistants or quick reference. Replace `X.Y.Z` with actual version.

```
1. UPDATE VERSIONS (6 files):
   - ProxiCraft.csproj:17          → <ModVersion>X.Y.Z</ModVersion>
   - ProxiCraft/ProxiCraft.cs:59   → MOD_VERSION = "X.Y.Z"
   - Properties/AssemblyInfo.cs    → Three attributes with X.Y.Z.0
   - Release/ProxiCraft/ModInfo.xml → <Version value="X.Y.Z" />
   - README.md:7                   → Download link URL
   - README.md (changelog section) → Add new version entry

2. UPDATE CHANGELOGS:
   - README.md (add at top of Changelog section)
   - NEXUS_DESCRIPTION.txt (add before previous version)

3. CREATE RELEASE NOTES:
   - Create Release_vX.Y.Z.txt

4. BUILD:
   dotnet build ProxiCraft.csproj -c Release
   → Creates Release/ProxiCraft-X.Y.Z.zip

5. GIT:
   git add .
   git commit -m "Release vX.Y.Z: [description]"
   git tag -a vX.Y.Z -m "Release vX.Y.Z"
   git push origin master
   git push origin vX.Y.Z

6. NEXUS (manual):
   - Upload zip to nexusmods.com
   - Update description with NEXUS_DESCRIPTION.txt content
```

---

## Version Numbering

Follow semantic versioning (MAJOR.MINOR.PATCH):
- **PATCH** (1.2.3 → 1.2.4): Bug fixes, config changes, docs
- **MINOR** (1.2.x → 1.3.0): New features, backwards-compatible
- **MAJOR** (1.x.x → 2.0.0): Breaking changes

---

## Detailed Steps

### Step 1: Update Version Numbers

Update **6 files** with the new version:

| # | File | Location | Change |
|---|------|----------|--------|
| 1 | `ProxiCraft.csproj` | Line ~17 | `<ModVersion>X.Y.Z</ModVersion>` |
| 2 | `ProxiCraft/ProxiCraft.cs` | Line ~59 | `public const string MOD_VERSION = "X.Y.Z";` |
| 3 | `Properties/AssemblyInfo.cs` | Lines 7-9 | All three `[assembly: Assembly*Version]` attributes |
| 4 | `Release/ProxiCraft/ModInfo.xml` | Line ~7 | `<Version value="X.Y.Z" />` |
| 5 | `README.md` | Line ~7 | Download link: `ProxiCraft-X.Y.Z.zip` |
| 6 | `README.md` | Changelog section | Add new version entry at top |

**AssemblyInfo.cs format:**
```csharp
[assembly: AssemblyFileVersion("X.Y.Z.0")]
[assembly: AssemblyInformationalVersion("X.Y.Z")]
[assembly: AssemblyVersion("X.Y.Z.0")]
```

### Step 2: Update Changelogs

**README.md** (Markdown format, add at TOP of Changelog section):
```markdown
### vX.Y.Z - Title

**Fixed/Changed/Added:**
- Description of change
- Another change
```

**NEXUS_DESCRIPTION.txt** (BBCode format, add BEFORE previous version):
```
[b]vX.Y.Z[/b]
[list]
[*]Description of change
[*]Another change
[/list]
```

### Step 3: Create Release Notes

Create file `Release_vX.Y.Z.txt`:
```
## ProxiCraft vX.Y.Z

### Summary
One-sentence summary of what changed.

### Changes
- Detailed change 1
- Detailed change 2

### Installation
1. Download ProxiCraft-X.Y.Z.zip
2. Extract and copy ProxiCraft folder to 7 Days To Die/Mods/
3. Launch game with EAC disabled

**Multiplayer**: Install on server AND all clients (same version)

### Reporting Issues
Found a bug? https://github.com/rk-gamemods/7D2D-ProxiCraft/issues
```

### Step 4: Build

```powershell
cd ProxiCraft
dotnet build ProxiCraft.csproj -c Release
```

**Verify output:**
- Build succeeds with 0 errors
- `Release/ProxiCraft-X.Y.Z.zip` is created

### Step 5: Git Operations

```powershell
# Review changes
git status

# Commit
git add .
git commit -m "Release vX.Y.Z: Brief description"

# Tag
git tag -a vX.Y.Z -m "Release vX.Y.Z"

# Push
git push origin master
git push origin vX.Y.Z
```

### Step 6: Upload to Nexus Mods (Manual)

1. Go to https://www.nexusmods.com/7daystodie/mods/9269
2. Click "Manage files" → "Add file"
3. Upload `Release/ProxiCraft-X.Y.Z.zip`
4. Set name: `ProxiCraft vX.Y.Z`
5. Set version: `X.Y.Z`
6. Mark as main file
7. Go to "Description" tab
8. Replace entire description with contents of `NEXUS_DESCRIPTION.txt`
9. Save

### Step 7: Verify

- [ ] GitHub: Commit visible at https://github.com/rk-gamemods/7D2D-ProxiCraft/commits/master
- [ ] GitHub: Tag visible at https://github.com/rk-gamemods/7D2D-ProxiCraft/tags
- [ ] GitHub: Download link works (click README download button)
- [ ] Nexus: New version shows as main file
- [ ] Nexus: Download works

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Build fails | Check version strings match, run `dotnet clean` first |
| Zip not created | Verify `Release/ProxiCraft/` has `config.json` and `ModInfo.xml` |
| Git push fails | Check branch (`git branch`), verify remote (`git remote -v`) |
| Download 404 | Wait 1-2 min for GitHub CDN sync, verify zip filename matches |

---

**Last Updated**: January 4, 2026 (v1.2.4 release)
