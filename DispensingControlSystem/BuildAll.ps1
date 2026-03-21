$ErrorActionPreference = "Stop"

$rids = @(
    "win-x86",
    "win-x64",
    "win-arm64"
)

Remove-Item -Path "Output\MultiArch" -Recurse -ErrorAction Ignore
New-Item -ItemType Directory -Force -Path "Output\MultiArch" | Out-Null

foreach ($rid in $rids) {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "Building DispensingControlSystem for $rid"
    Write-Host "========================================"
    Write-Host ""
    
    # 1. Publish the .NET application
    # We do a framework-dependent publish, but specify the RID to build a native AppHost for that architecture
    dotnet publish DispensingControlSystem.vbproj -c Release -r $rid --self-contained false -p:PublishSingleFile=false
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build .NET project for $rid"
        exit $LASTEXITCODE
    }

    # 2. Determine WiX Architecture from RID
    $wixArch = "x64"
    if ($rid -eq "win-x86") {
        $wixArch = "x86"
    } elseif ($rid -eq "win-arm64") {
        $wixArch = "arm64"
    } elseif ($rid -eq "win-arm") {
        # Win10 on ARM 32-bit. WiX supports x86, x64, arm64 as primary architectures. 
        # Best effort is to build as x86 to ride on emulation since 32-bit ARM is a rare edge case.
        # Alternatively, WiX v4 supports 'arm64' natively.
        # Let's try compiling as x86 since 32-bit ARM can run x86 via emulation if standard ARM is not supported.
        $wixArch = "x86" 
    }

    # 3. Build WiX Installer
    $msiName = "SetupVBX_$rid.msi"
    $msiPath = "Output\MultiArch\$msiName"
    
    Write-Host "Building MSI for $rid ($wixArch architecture in WiX)"
    
    # Clean up previous wix obj
    Remove-Item -Path "obj\Release\$rid" -Recurse -ErrorAction Ignore
    
    wix build Product.wxs -arch $wixArch -d "Rid=$rid" -o $msiPath
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build MSI for $rid"
        exit $LASTEXITCODE
    }
    
    Write-Host "Successfully built $msiPath"
}

Write-Host "========================================"
Write-Host "All multi-architecture builds completed successfully!"
Write-Host "Check the Output\MultiArch folder for the MSI installers."
