$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $scriptDir "DispensingControlSystem.vbproj"
$outDir = Join-Path $scriptDir "Output\Standalone\win-x64"

Write-Host ""
Write-Host "==================================="
Write-Host "  Building win-x64 Standalone EXE"
Write-Host "==================================="
Write-Host "Project : $proj"
Write-Host "Output  : $outDir"
Write-Host ""

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build FAILED (exit code: $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "==================================="
Write-Host "  BUILD SUCCESS -> $outDir"
Write-Host "==================================="
