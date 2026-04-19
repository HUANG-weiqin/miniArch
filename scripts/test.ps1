param(
    [string]$Configuration = "Debug",
    [string]$Filter = ""
)

$ErrorActionPreference = "Stop"

. $PSScriptRoot\env.ps1
Initialize-MiniArchScriptEnvironment

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "tests\MiniArch.Tests\MiniArch.Tests.csproj"

$args = @(
    "test",
    $project,
    "-c",
    $Configuration,
    "-v",
    "minimal"
)

if ($Filter) {
    $args += @("--filter", $Filter)
}

& dotnet @args
