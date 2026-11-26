param(
    [string]$GitInfoFile
)

# Clear the file
"" > $GitInfoFile

# Write build date and time
Add-Content -Path $GitInfoFile -Value ("Build Time: " + (Get-Date -Format 'dd.MM.yyyy HH:mm:ss'))

Add-Content -Path $GitInfoFile -Value ""
Add-Content -Path $GitInfoFile -Value "Main repository:"
git rev-parse HEAD | Out-File -Append $GitInfoFile
git rev-parse --abbrev-ref HEAD | Out-File -Append $GitInfoFile
git tag --points-at HEAD | Out-File -Append $GitInfoFile

Add-Content -Path $GitInfoFile -Value ""
Add-Content -Path $GitInfoFile -Value "Uncommitted Changes:"
git status --porcelain | Out-File -Append $GitInfoFile