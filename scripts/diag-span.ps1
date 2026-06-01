param(
    [int]$EntityCount = 100000,
    [int]$DurationSeconds = 5,
    [int]$WarmupIterations = 3,
    [int]$RepeatCount = 3
)

$variants = @("Baseline", "NoInvalidation", "SpanApi", "UltraRaw")

Write-Host "=== Span Query Diagnostic ===" -ForegroundColor Cyan
Write-Host "EntityCount=$EntityCount Duration=${DurationSeconds}s Warmup=$WarmupIterations Repeat=$RepeatCount"
Write-Host ""

foreach ($variant in $variants) {
    Write-Host "--- $variant ---" -ForegroundColor Yellow
    $env:MINIARCH_SPAN_VARIANT = $variant
    $output = & dotnet run -c Release --project "benchmarks\MiniArch.Benchmarks" -- `
        throughput `
        --workload query-with-all-component-span `
        --engine miniarch `
        --entity-count $EntityCount `
        --duration $DurationSeconds `
        --warmup $WarmupIterations `
        --repeat $RepeatCount `
        2>&1
    $output | Select-String -Pattern "summary|repeat " | ForEach-Object { $_.Line }
    Write-Host ""
}

Remove-Item Env:\MINIARCH_SPAN_VARIANT -ErrorAction SilentlyContinue
Write-Host "=== Done ===" -ForegroundColor Cyan
