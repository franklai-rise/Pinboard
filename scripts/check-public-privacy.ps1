$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$self = "scripts/check-public-privacy.ps1"
$extensions = @(
    ".cs", ".csproj", ".json", ".md", ".ps1", ".ts", ".tsx",
    ".xaml", ".xml", ".yml", ".yaml", ".html", ".css", ".js", ".mjs"
)
$findings = [System.Collections.Generic.List[string]]::new()

Push-Location $projectRoot
try {
    # Keep Unicode filenames literal; Git's quoted form is not a filesystem path.
    $trackedFiles = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed with exit code $LASTEXITCODE."
    }

    foreach ($relativePath in $trackedFiles) {
        if ($relativePath -eq $self -or $extensions -notcontains [IO.Path]::GetExtension($relativePath)) {
            continue
        }

        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines((Join-Path $projectRoot $relativePath))) {
            $lineNumber++
            if ($line -match '(?i)\b[A-Z]:[\\/]' -or
                $line -match '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b' -or
                $line -match '<Authors>(?!Pinboard contributors</Authors>)' -or
                $line -match 'BEGIN [A-Z ]*PRIVATE KEY') {
                $findings.Add("${relativePath}:${lineNumber}: $($line.Trim())")
            }
        }
    }
}
finally {
    Pop-Location
}

if ($findings.Count -gt 0) {
    Write-Error ("Potential personal path, email, author identity, or private key found:`n" + ($findings -join "`n"))
}

Write-Host "Public-tree privacy check passed."
