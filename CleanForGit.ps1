$baseDir = "c:\Users\Administrator\Documents\VBX"
Set-Location $baseDir

Write-Host "Cleaning up build artifacts before pushing to GitHub..." -ForegroundColor Cyan

$itemsToDelete = @(
    "DispensingControlSystem\bin",
    "DispensingControlSystem\obj",
    "DispensingControlSystem\Output",
    ".vs",
    "DispensingControlSystem\*.user",
    "DispensingControlSystem\*.userosscache",
    "DispensingControlSystem\*.suo",
    "DispensingControlSystem\*.log",
    "DispensingControlSystem\build_*.txt",
    "*.log",
    "DispensingControlSystem\*.msi",
    "DispensingControlSystem\*.wixpdb",
    "DispensingControlSystem\*.cab",
    "Output"
)

foreach ($item in $itemsToDelete) {
    if (Test-Path $item) {
        Write-Host "Removing: $item" -ForegroundColor Yellow
        Remove-Item -Path $item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Cleanup complete! Ready to push to GitHub." -ForegroundColor Green
