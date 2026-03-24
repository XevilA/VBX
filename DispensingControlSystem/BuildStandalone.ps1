$ErrorActionPreference = "Stop"

# ══════════════════════════════════════════════════════════
#  VBX DISPENSING CONTROL SYSTEM — Standalone Build Script
#  Produces a single self-contained EXE for Windows x64
# ══════════════════════════════════════════════════════════

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj      = Join-Path $scriptDir "DispensingControlSystem.vbproj"
$outDir    = Join-Path $scriptDir "Output\Standalone\win-x64"
$rid       = "win-x64"

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  VBX DISPENSING CONTROL — Standalone Build               ║" -ForegroundColor Cyan
Write-Host "║  Target: Windows x64 Single-File Self-Contained EXE     ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Step 1: Clean
Write-Host "[1/3] Cleaning previous output..." -ForegroundColor Yellow
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

# Step 2: Publish
Write-Host "[2/3] Publishing self-contained single-file EXE..." -ForegroundColor Yellow
Write-Host "       Project : $proj"
Write-Host "       Runtime : $rid"
Write-Host "       Config  : Release"
Write-Host ""

dotnet publish $proj `
    -c Release `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "║  ✗  BUILD FAILED  (exit code: $LASTEXITCODE)                        ║" -ForegroundColor Red
    Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Step 3: Verify
Write-Host ""
Write-Host "[3/3] Verifying output..." -ForegroundColor Yellow
$exe = Get-ChildItem (Join-Path $outDir "DispensingControlSystem.exe") -ErrorAction SilentlyContinue
if ($exe) {
    $sizeMB = [math]::Round($exe.Length / 1MB, 2)
    Write-Host "       EXE  : $($exe.Name)"
    Write-Host "       Size : $sizeMB MB"
    Write-Host "       Path : $($exe.FullName)"
}

# List all files in output
Write-Host ""
Write-Host "  Output contents:" -ForegroundColor DarkGray
Get-ChildItem $outDir | ForEach-Object {
    $s = if ($_.Length) { "$([math]::Round($_.Length / 1KB, 1)) KB" } else { "DIR" }
    Write-Host "    $($_.Name.PadRight(40)) $s" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✓  BUILD SUCCESS                                       ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  Run: .\Output\Standalone\win-x64\DispensingControlSystem.exe" -ForegroundColor White
Write-Host ""
