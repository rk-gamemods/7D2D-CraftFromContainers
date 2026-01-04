# ProxiCraft Release Process

This document provides a comprehensive, step-by-step checklist for releasing new versions of ProxiCraft to GitHub and Nexus Mods.

## Version Numbering

Follow semantic versioning (MAJOR.MINOR.PATCH):
- **MAJOR**: Breaking changes, incompatible API changes
- **MINOR**: New features, backwards-compatible functionality
- **PATCH**: Bug fixes, config changes, documentation updates

Examples:
- New feature (multiplayer support): 1.2.0 → 1.3.0
- Bug fix: 1.2.2 → 1.2.3
- Config default change only: 1.2.2 → 1.2.3
- Breaking change: 1.2.3 → 2.0.0

## Pre-Release Checklist

Before starting the release process, verify:

- [ ] All code changes are committed and tested
- [ ] No outstanding bugs that should block release
- [ ] NEXUS_DESCRIPTION.txt Configuration section is up-to-date with all settings
- [ ] README.md documentation reflects current features
- [ ] TECHNICAL_REFERENCE.md is accurate (if code changes were made)
- [ ] All tests pass (if applicable)
- [ ] Version number determined (MAJOR.MINOR.PATCH)

## Release Checklist

### Step 1: Update Version Numbers

Update version in **6 files** (replace `X.Y.Z` with actual new version):

#### 1.1 ProxiCraft.csproj
**File**: `ProxiCraft/ProxiCraft.csproj`  
**Line**: ~18  
**Change**: `<ModVersion>OLD_VERSION</ModVersion>` → `<ModVersion>X.Y.Z</ModVersion>`

#### 1.2 ProxiCraft.cs
**File**: `ProxiCraft/ProxiCraft/ProxiCraft.cs`  
**Line**: ~59  
**Change**: `public const string MOD_VERSION = "OLD_VERSION";` → `public const string MOD_VERSION = "X.Y.Z";`

#### 1.3 AssemblyInfo.cs
**File**: `ProxiCraft/Properties/AssemblyInfo.cs`  
**Lines**: 7-9 (three attributes)  
**Changes**:
```csharp
[assembly: AssemblyVersion("X.Y.Z.0")]
[assembly: AssemblyFileVersion("X.Y.Z.0")]
[assembly: AssemblyInformationalVersion("X.Y.Z")]
```

#### 1.4 ModInfo.xml
**File**: `ProxiCraft/Release/ProxiCraft/ModInfo.xml`  
**Line**: ~6  
**Change**: `<Version value="OLD_VERSION" />` → `<Version value="X.Y.Z" />`

#### 1.5 README.md (version badge + download link)
**File**: `ProxiCraft/README.md`  
**Line 7**: Download link  
**Change**: 
```markdown
[**Download v1.2.2**](https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft-1.2.2.zip)
```
to:
```markdown
[**Download vX.Y.Z**](https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft-X.Y.Z.zip)
```

**Note**: If there's a version badge/shield near the top, update that too.

#### 1.6 README.md (verify no other version references)
Search the entire file for OLD_VERSION and update any remaining references.

### Step 2: Update Changelogs

#### 2.1 README.md Changelog
**File**: `ProxiCraft/README.md`  
**Location**: ~line 239 (Changelog section, add at top)  
**Format**:
```markdown
### v<X.Y.Z>
- [Brief description of changes]
- [List each change as bullet point]
```

**Example**:
```markdown
### v1.2.3
- Changed enhanced safety mode defaults to enabled (recommended for multiplayer stability)
- Configuration documentation expanded with all 30+ settings
```

#### 2.2 NEXUS_DESCRIPTION.txt Changelog
**File**: `ProxiCraft/NEXUS_DESCRIPTION.txt`  
**Location**: ~line 356 (Changelog section, add BEFORE previous version)  
**Format** (BBCode):
```
[b]vX.Y.Z[/b]
[list]
[*]Brief description of changes
[*]Each change as bullet point
[/list]
```

**Example**:
```
[b]v1.2.3[/b]
[list]
[*]Changed enhanced safety mode defaults to enabled (recommended for multiplayer stability)
[*]Configuration documentation expanded with all 30+ settings
[/list]
```

### Step 3: Create Release Notes File

**File**: `ProxiCraft/Release_vX.Y.Z.txt`  
**Purpose**: Used for GitHub release description

**Template**:
```
## ProxiCraft v<X.Y.Z>

### Summary
[One-sentence summary of what changed]

### Changes
- [Detailed list of changes]
- [Include breaking changes prominently]
- [Reference issue numbers if applicable: #123]

### Installation
1. Download ProxiCraft-X.Y.Z.zip
2. Extract and copy ProxiCraft folder to 7 Days To Die/Mods/
3. Launch game with EAC disabled

**Multiplayer**: Install on server AND all clients (same version)

### Full Documentation
- [GitHub README](https://github.com/rk-gamemods/7D2D-ProxiCraft)
- [Configuration Guide](https://github.com/rk-gamemods/7D2D-ProxiCraft#configuration)
- [Technical Reference](https://github.com/rk-gamemods/7D2D-ProxiCraft/blob/main/TECHNICAL_REFERENCE.md)

### Reporting Issues
Found a bug? [Open an issue on GitHub](https://github.com/rk-gamemods/7D2D-ProxiCraft/issues)
```

**Example for config-only release**:
```
## ProxiCraft v1.2.3

### Summary
Configuration change: Enhanced safety mode now enabled by default for better multiplayer stability.

### Changes
- **Enhanced Safety Defaults Changed**: All `enhancedSafety*` settings now default to `true` (recommended for multiplayer)
- **Documentation Expanded**: NEXUS_DESCRIPTION.txt Configuration section now documents all 30+ settings comprehensively
- **No Functional Changes**: This is a default configuration change only - no code changes

**Note for Existing Users**: If you have a custom `config.json`, your settings are unchanged. Only new installations will use the new defaults.

### Installation
1. Download ProxiCraft-1.2.3.zip
2. Extract and copy ProxiCraft folder to 7 Days To Die/Mods/
3. Launch game with EAC disabled

**Multiplayer**: Install on server AND all clients (same version)

### Full Documentation
- [GitHub README](https://github.com/rk-gamemods/7D2D-ProxiCraft)
- [Configuration Guide](https://github.com/rk-gamemods/7D2D-ProxiCraft#configuration)
- [Technical Reference](https://github.com/rk-gamemods/7D2D-ProxiCraft/blob/main/TECHNICAL_REFERENCE.md)

### Reporting Issues
Found a bug? [Open an issue on GitHub](https://github.com/rk-gamemods/7D2D-ProxiCraft/issues)
```

### Step 4: Build Release Package

Run the build command:
```powershell
dotnet build ProxiCraft/ProxiCraft.csproj -c Release
```

**Expected Output**:
- Build succeeds (0 warnings ideally)
- `ProxiCraft/Release/ProxiCraft-X.Y.Z.zip` is created
- Zip contains:
  - `ProxiCraft/ProxiCraft.dll`
  - `ProxiCraft/ModInfo.xml`
  - `ProxiCraft/config.json`

**Verification**:
```powershell
# Check zip exists
Test-Path "ProxiCraft/Release/ProxiCraft-X.Y.Z.zip"

# Inspect contents
Expand-Archive -Path "ProxiCraft/Release/ProxiCraft-X.Y.Z.zip" -DestinationPath "ProxiCraft/Release/temp_verify" -Force
Get-ChildItem -Recurse "ProxiCraft/Release/temp_verify"
Remove-Item -Recurse -Force "ProxiCraft/Release/temp_verify"
```

### Step 5: Git Operations

#### 5.1 Review Changes
```powershell
git status
git diff
```

Verify:
- [ ] All 6 version files updated
- [ ] README.md download link updated
- [ ] Both changelogs updated
- [ ] Release notes file created
- [ ] No unintended changes

#### 5.2 Commit Changes
```powershell
git add .
git commit -m "Release vX.Y.Z: [Brief description]"
```

**Example commit messages**:
- `Release v1.2.3: Enable enhanced safety by default`
- `Release v1.3.0: Add lockpicking feature`
- `Release v1.2.4: Fix vehicle refuel crash`

#### 5.3 Create Git Tag
```powershell
git tag -a vX.Y.Z -m "Release vX.Y.Z"
```

#### 5.4 Push to GitHub
```powershell
# Push commits
git push origin main

# Push tags
git push origin vX.Y.Z
```

**Verification**:
- Visit `https://github.com/rk-gamemods/7D2D-ProxiCraft/commits/main`
- Verify commit appears
- Visit `https://github.com/rk-gamemods/7D2D-ProxiCraft/tags`
- Verify tag appears

### Step 6: Verify GitHub Changes

**After push completes, verify everything is accessible:**

1. **Check Commit Appears**:
   - Visit `https://github.com/rk-gamemods/7D2D-ProxiCraft/commits/master`
   - Verify your release commit is at the top

2. **Check Tag Appears**:
   - Visit `https://github.com/rk-gamemods/7D2D-ProxiCraft/tags`
   - Verify `vX.Y.Z` tag exists

3. **Test Direct Download Link**:
   - The README.md download link uses direct repository access: `https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft-X.Y.Z.zip`
   - Open this URL in browser - should download the zip immediately
   - If 404, wait 1-2 minutes for GitHub CDN to sync, then retry

4. **Verify README Displays Correctly**:
   - Visit `https://github.com/rk-gamemods/7D2D-ProxiCraft`
   - Check that download button shows new version
   - Verify changelog section shows new entry

**Note**: ProxiCraft uses direct repository file access instead of GitHub Releases for simplicity. Users download the zip file directly from the `Release/` folder.

### Step 7: Upload to Nexus Mods

**Manual Process** (Nexus doesn't have API for uploads):

1. **Login to Nexus Mods**:
   - Go to: `https://www.nexusmods.com/7daystodie/mods/XXXX` (ProxiCraft mod page)
   - Click "Manage files"

2. **Upload New File**:
   - Click "Add file"
   - **File**: Upload `ProxiCraft/Release/ProxiCraft-X.Y.Z.zip`
   - **Name**: `ProxiCraft vX.Y.Z`
   - **Version**: `X.Y.Z`
   - **Brief overview**: Copy summary from `Release_vX.Y.Z.txt`
   - **Mark as main file**: Check if this is the primary version

3. **Update Description**:
   - Click "Description" tab
   - Copy **entire contents** of `NEXUS_DESCRIPTION.txt` into the description editor
   - Preview to verify BBCode formatting
   - Save

4. **Verification**:
   - View mod page as non-logged-in user
   - Verify download button shows correct version
   - Verify description displays correctly
   - Test download link

### Step 8: Post-Release Verification

Final checks after release is live:

- [ ] GitHub commit and tag visible
- [ ] GitHub direct download link works: `https://github.com/rk-gamemods/7D2D-ProxiCraft/raw/master/Release/ProxiCraft-X.Y.Z.zip`
- [ ] README.md download link on GitHub main page works (critical - this is what users see first)
- [ ] Changelog visible on GitHub
- [ ] Nexus Mods page shows new version
- [ ] Nexus download works
- [ ] Nexus description displays correctly

**Optional**: Test installation in clean 7D2D instance to verify zip structure is correct.

### Step 9: Announce Release (Optional)

Consider announcing on:
- [ ] GitHub Discussions (if enabled)
- [ ] Nexus Mods sticky post/comment
- [ ] Discord servers (7D2D modding communities)
- [ ] Reddit r/7daystodie (if significant release)

---

## Troubleshooting

### Build Fails
- Check all version strings match (no typos)
- Verify `dotnet --version` shows compatible SDK (6.0+)
- Clean build: `dotnet clean ProxiCraft/ProxiCraft.csproj` then rebuild

### Git Push Fails
- Check you're on correct branch: `git branch` (should show `* main`)
- Verify remote: `git remote -v` (should show GitHub URL)
- Authentication issues: Regenerate GitHub PAT if needed

### Zip Not Created
- Check `ProxiCraft.csproj` has `CreateReleaseZip` target
- Verify `Release/ProxiCraft/` folder exists with `config.json` and `ModInfo.xml`
- Check build output for zip creation errors

### Download Link 404
- Verify tag pushed: `git ls-remote --tags origin`
- Check release is published (not draft)
- Ensure zip filename matches URL exactly (case-sensitive)

---

## Quick Reference: Files to Update

Every release requires updating these files:

| File | Line(s) | What to Change |
|------|---------|----------------|
| `ProxiCraft.csproj` | ~18 | `<ModVersion>` |
| `ProxiCraft/ProxiCraft.cs` | ~59 | `MOD_VERSION` constant |
| `Properties/AssemblyInfo.cs` | 7-9 | 3 assembly attributes |
| `Release/ProxiCraft/ModInfo.xml` | ~6 | `<Version value="">` |
| `README.md` | ~7 | Download link URL |
| `README.md` | ~239 | Add changelog entry |
| `NEXUS_DESCRIPTION.txt` | ~356 | Add changelog entry |
| *New file* | n/a | Create `Release_vX.Y.Z.txt` |

---

## Notes

- Always test build locally before pushing
- Never skip version updates in any of the 6 files - inconsistency causes confusion
- README.md download link is critical - it's the first thing users see
- NEXUS_DESCRIPTION.txt Configuration section should always be comprehensive (all settings documented)
- For multiplayer changes, emphasize in release notes that server + clients all need update
- Tag version format: `vX.Y.Z` (with 'v' prefix)
- Zip filename format: `ProxiCraft-X.Y.Z.zip` (no 'v' prefix)

---

**Last Updated**: January 4, 2026 (v1.2.3 release)
