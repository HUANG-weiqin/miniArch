param(
    [string]$Configuration = "Debug",
    [string]$Filter = ""
)

$ErrorActionPreference = "Stop"

& $PSScriptRoot\build.ps1 -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $PSScriptRoot\test.ps1 -Configuration $Configuration -Filter $Filter
