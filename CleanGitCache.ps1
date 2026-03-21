Set-Location -Path "c:\Users\Administrator\Documents\VBX"
Write-Host "Cleaning Git Cache so .gitignore takes effect..." -ForegroundColor Cyan

# Remove everything from Git's cache
git rm -r --cached .

# Re-add everything (this will respect the new .gitignore)
git add .

Write-Host "Git cache cleared successfully!" -ForegroundColor Green
