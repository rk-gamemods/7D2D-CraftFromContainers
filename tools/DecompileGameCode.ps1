# DecompileGameCode.ps1
# Decompiles 7 Days to Die game assemblies for code reference
# 
# This tool helps modders understand game internals by decompiling
# the game's .NET assemblies into readable C# source code.
#
# IMPORTANT: The decompiled code is for personal reference only.
# Do NOT commit decompiled game code to public repositories.

param(
    [string]$GamePath = "C:\Steam\steamapps\common\7 Days To Die",
    [string]$OutputPath = "..\GameCode",  # Relative to script location, gitignored
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Resolve relative output path
if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $scriptDir $OutputPath
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

# Key assemblies to decompile
$assemblies = @(
    "Assembly-CSharp.dll",           # Main game code
    "Assembly-CSharp-firstpass.dll", # Additional game code
    "0Harmony.dll"                   # Harmony library (for reference)
)

$managedPath = Join-Path $GamePath "7DaysToDie_Data\Managed"

Write-Host "=== 7 Days to Die Code Decompiler ===" -ForegroundColor Cyan
Write-Host "Game Path: $GamePath"
Write-Host "Output Path: $OutputPath"
Write-Host ""

# Check if game path exists
if (-not (Test-Path $managedPath)) {
    Write-Host "ERROR: Game path not found: $managedPath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please specify the correct game path:" -ForegroundColor Yellow
    Write-Host "  .\DecompileGameCode.ps1 -GamePath 'D:\Games\7 Days To Die'"
    exit 1
}

# Check if ILSpyCmd is installed
$ilspyCmd = Get-Command "ilspycmd" -ErrorAction SilentlyContinue
if (-not $ilspyCmd) {
    Write-Host "ILSpyCmd not found. Installing via dotnet tool..." -ForegroundColor Yellow
    dotnet tool install -g ilspycmd
    
    # Refresh PATH
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH", "User")
    
    $ilspyCmd = Get-Command "ilspycmd" -ErrorAction SilentlyContinue
    if (-not $ilspyCmd) {
        Write-Host "ERROR: Failed to install ilspycmd. Please install manually:" -ForegroundColor Red
        Write-Host "  dotnet tool install -g ilspycmd" -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "Using ILSpyCmd: $($ilspyCmd.Source)" -ForegroundColor Green

# Create output directory
if (Test-Path $OutputPath) {
    if ($Force) {
        Write-Host "Removing existing output directory..." -ForegroundColor Yellow
        Remove-Item $OutputPath -Recurse -Force
    } else {
        Write-Host "Output directory exists. Use -Force to overwrite, or delete manually." -ForegroundColor Yellow
        Write-Host "Existing: $OutputPath"
        exit 0
    }
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# Decompile each assembly
foreach ($assembly in $assemblies) {
    $dllPath = Join-Path $managedPath $assembly
    $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($assembly)
    $outputDir = Join-Path $OutputPath $assemblyName
    
    if (-not (Test-Path $dllPath)) {
        Write-Host "SKIP: $assembly not found" -ForegroundColor Yellow
        continue
    }
    
    Write-Host ""
    Write-Host "Decompiling $assembly..." -ForegroundColor Cyan
    Write-Host "  Source: $dllPath"
    Write-Host "  Output: $outputDir"
    
    $startTime = Get-Date
    
    # ILSpyCmd options:
    # -p / --project : Generate .csproj file
    # -o / --outputdir : Output directory
    # -lv / --languageversion : C# version (Latest)
    try {
        & ilspycmd $dllPath -p -o $outputDir -lv Latest 2>&1 | ForEach-Object {
            if ($_ -match "error|Error|ERROR") {
                Write-Host "  $_" -ForegroundColor Red
            }
        }
        
        $elapsed = (Get-Date) - $startTime
        $fileCount = (Get-ChildItem $outputDir -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue).Count
        Write-Host "  Done! $fileCount .cs files in $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
    }
    catch {
        Write-Host "  ERROR: $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== Decompilation Complete ===" -ForegroundColor Cyan
Write-Host "Output: $OutputPath" -ForegroundColor Green
Write-Host ""
Write-Host "You can now search the codebase in VS Code:" -ForegroundColor Yellow
Write-Host "  1. Open folder in VS Code"
Write-Host "  2. Use Ctrl+Shift+F to search all files"
Write-Host ""

# Show summary
$totalFiles = (Get-ChildItem $OutputPath -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue).Count
$totalSize = (Get-ChildItem $OutputPath -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "Total: $totalFiles .cs files, $($totalSize.ToString('F1')) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "REMINDER: Do not commit decompiled code to git!" -ForegroundColor Yellow
