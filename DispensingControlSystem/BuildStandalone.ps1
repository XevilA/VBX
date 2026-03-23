$ErrorActionPreference = "Stop"

$rids = @(
    "win-x86",
    "win-x64",
    "win-arm64"
)

Remove-Item -Path "Output\Standalone" -Recurse -ErrorAction Ignore
New-Item -ItemType Directory -Force -Path "Output\Standalone" | Out-Null

foreach ($rid in $rids) {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "Building Standalone Executable for $rid"
    Write-Host "========================================"
    Write-Host ""
    
    $outDir = "Output\Standalone\$rid"

    # 1. Publish the .NET application as a single-file, self-contained executable
    dotnet publish DispensingControlSystem.vbproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outDir
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build standalone .NET project for $rid"
        exit $LASTEXITCODE
    }

    Write-Host "Successfully built standalone application for $rid in $outDir"
}

Write-Host "========================================"
Write-Host "All standalone builds completed successfully!"
Write-Host "Check the Output\Standalone folder."
