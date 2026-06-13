param(
    [string]$Configuration = "Release",
    [string]$Workload = "entity",
    [string]$Scenario = "with-all",
    [string]$Temperature = "cold",
    [int]$EntityCount = 100000,
    [int]$DurationSeconds = 15,
    [int]$WarmupIterations = 3,
    [int]$StartupDelaySeconds = 3,
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
    "--",
    "profile-query",
    "--workload",
    $Workload,
    "--scenario",
    $Scenario,
    "--temperature",
    $Temperature,
    "--entity-count",
    $EntityCount,
    "--duration",
    $DurationSeconds,
    "--warmup",
    $WarmupIterations,
    "--startup-delay",
    $StartupDelaySeconds
)

if ($ExtraArgs -and $ExtraArgs.Length -gt 0) {
    $args += $ExtraArgs
}

& dotnet @args
