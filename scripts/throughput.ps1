param(
    [string]$Configuration = "Release",
    [string]$Workload = "query-with-all-entity",
    [string]$Engine = "both",
    [int]$EntityCount = 100000,
    [int]$DurationSeconds = 10,
    [int]$WarmupIterations = 3,
    [int]$RepeatCount = 5,
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
    "throughput",
    "--workload",
    $Workload,
    "--engine",
    $Engine,
    "--entity-count",
    $EntityCount,
    "--duration",
    $DurationSeconds,
    "--warmup",
    $WarmupIterations,
    "--repeat",
    $RepeatCount
)

if ($ExtraArgs -and $ExtraArgs.Length -gt 0) {
    $args += $ExtraArgs
}

& dotnet @args
