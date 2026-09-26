param(
    [string]$PublishPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\publish"),
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\portable")
)
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PublishPath "Pinboard.exe") -Destination (Join-Path $Destination "Pinboard.exe") -Force
foreach ($name in @("README.md", "README.zh-CN.md", "LICENSE", "THIRD-PARTY-NOTICES.txt", "CONTRIBUTING.md", "SECURITY.md")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $Destination $name) -Force
}
# Preserve relative links and screenshot paths in both README languages.
$docsSource = Join-Path $projectRoot "docs"
$docsTarget = Join-Path $Destination "docs"
New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
Get-ChildItem -LiteralPath $docsSource -Recurse -File | Where-Object { $_.Extension -in @(".md", ".png") } | ForEach-Object {
    $relative = $_.FullName.Substring($docsSource.Length).TrimStart('\', '/')
    $target = Join-Path $docsTarget $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}
