$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $projectRoot "web"
$publishRoot = Join-Path $projectRoot "artifacts\publish"
$portableRoot = Join-Path $projectRoot "artifacts\portable"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot "check-public-privacy.ps1")

Push-Location $webRoot
try {
    Invoke-Checked "npm" @("ci")
    Invoke-Checked "npm" @("test")
    Invoke-Checked "npm" @("run", "build")
    Invoke-Checked "npm" @("audit", "--audit-level=low")
}
finally {
    Pop-Location
}

Invoke-Checked "dotnet" @("clean", (Join-Path $projectRoot "PinboardApp.sln"), "-c", "Release", "--verbosity", "minimal")
Invoke-Checked "dotnet" @(
    "restore",
    (Join-Path $projectRoot "PinboardApp.sln"),
    "-p:NuGetAudit=true",
    "-p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904"
)
Invoke-Checked "dotnet" @("test", (Join-Path $projectRoot "PinboardApp.sln"), "-c", "Release", "--no-restore")
Invoke-Checked "dotnet" @("publish", (Join-Path $projectRoot "src\Pinboard.App\Pinboard.App.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", $publishRoot)

& (Join-Path $PSScriptRoot "stage-portable.ps1") -PublishPath $publishRoot -Destination $portableRoot

$zipPath = Join-Path $projectRoot "artifacts\Pinboard-windows-x64.zip"
$checksumPath = "$zipPath.sha256"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $portableRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii

Write-Host "Pinboard portable build: $portableRoot"
Write-Host "Release archive: $zipPath"
Write-Host "SHA-256: $checksumPath"
