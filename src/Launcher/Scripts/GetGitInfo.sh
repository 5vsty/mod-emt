#!/bin/bash

# Parameter: file path for Git info
GitInfoFile="$1"

# Clear the file
> "$GitInfoFile"

# Write build date and time
echo "Build Date: $(date +%d.%m.%Y)" >> "$GitInfoFile"
echo "Build Time: $(date +%H:%M:%S)" >> "$GitInfoFile"

echo "" >> "$GitInfoFile"
echo "Main repository:" >> "$GitInfoFile"
git rev-parse HEAD >> "$GitInfoFile"
git rev-parse --abbrev-ref HEAD >> "$GitInfoFile"
git tag --points-at HEAD >> "$GitInfoFile"

echo "" >> "$GitInfoFile"
echo "Uncommitted Changes:" >> "$GitInfoFile"
git status --porcelain >> "$GitInfoFile"