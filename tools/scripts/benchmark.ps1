param(
    [string]$Configuration = "Release",
    [string]$Filter = "*",
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj"

$args = @(
    "run",
    "--project",
    $project,
    "-c",
    $Configuration,
    "--"
)

if ($Filter) {
    $args += @("--filter", $Filter)
}

if ($ExtraArgs -and $ExtraArgs.Length -gt 0) {
    $args += $ExtraArgs
}

& dotnet @args
