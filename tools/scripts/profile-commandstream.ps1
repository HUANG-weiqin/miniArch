param(
    [string]$Scenario = "",
    [int]$Warmup = 3,
    [int]$Measure = 10,
    [int]$TraceSeconds = 0,
    [string]$OutputDir = "",
    [switch]$NoTrace,
    [switch]$ListOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repoRoot "tools\perf\CommandStream.Profile\CommandStream.Profile.csproj"
$profileOutputDir = if ($OutputDir) { $OutputDir } else { Join-Path $repoRoot "profiles" }

# Ensure profile output dir exists
if (-not (Test-Path $profileOutputDir)) {
    New-Item -ItemType Directory -Path $profileOutputDir -Force | Out-Null
}

if ($ListOnly) {
    & dotnet run --project $project -c Release -- --list
    exit
}

# Build the runner first (Release)
Write-Host "=== Building CommandStream.Profile (Release) ===" -ForegroundColor Cyan
& dotnet build $project -c Release 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

$runnerArgs = @(
    "run", "--project", $project, "-c", "Release", "--no-build", "--"
)

if ($Scenario) {
    $runnerArgs += "--scenario", $Scenario
}
$runnerArgs += "--warmup", $Warmup
$runnerArgs += "--measure", $Measure

$traceEnabled = (-not $NoTrace) -and ($TraceSeconds -gt 0)

if ($traceEnabled) {
    $pidFile = Join-Path $profileOutputDir "profile.pid"
    if (Test-Path $pidFile) {
        Remove-Item $pidFile -Force
    }
    $runnerArgs += "--profile-ready-file", $pidFile
    if ($TraceSeconds -gt 0) {
        $runnerArgs += "--attach-delay", 2
    }
}

Write-Host "=== Scenario: $(if ($Scenario) { $Scenario } else { 'all' }) ===" -ForegroundColor Cyan
Write-Host "Warmup: ${Warmup}s, Measure: ${Measure}s" -ForegroundColor Cyan

$job = Start-Job -ScriptBlock {
    param($runnerArgsInJob, $repoRootInJob)
    Set-Location $repoRootInJob
    & dotnet @runnerArgsInJob
} -ArgumentList $runnerArgs, $repoRoot

if ($traceEnabled) {
    Write-Host "Waiting for runner to signal it's ready (polling $pidFile)..." -ForegroundColor Yellow
    Start-Sleep -Seconds 1

    # Wait for PID file
    $timeout = 10
    $targetPid = $null
    do {
        if (Test-Path $pidFile) {
            $targetPid = Get-Content $pidFile -Raw -ErrorAction SilentlyContinue
            if ($targetPid) {
                $targetPid = $targetPid.Trim()
            }
            if ($targetPid -and $targetPid -match '^\d+$') {
                break
            }
        }
        Start-Sleep -Seconds 1
        $timeout--
    } while ($timeout -gt 0)

    if (-not $targetPid) {
        Write-Error "Timed out waiting for runner."
        Stop-Job $job
        Remove-Job $job -Force
        exit 1
    }

    Write-Host "Runner PID: $targetPid" -ForegroundColor Green
    Write-Host "Attaching dotnet-trace for ${TraceSeconds}s..." -ForegroundColor Yellow

    $traceFile = Join-Path $profileOutputDir "commandstream-$(if ($Scenario) { $Scenario } else { 'all' })-$(Get-Date -Format 'yyyyMMdd-HHmmss').nettrace"

    # Start trace collection
    $traceJob = Start-Job -ScriptBlock {
        param($targetPid, $traceFile, $duration)
        dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler --process-id $targetPid --duration $("00:{0:mm}:{0:ss}" -f (New-TimeSpan -Seconds $duration)) -o $traceFile
    } -ArgumentList $targetPid, $traceFile, $TraceSeconds

    # Wait for runner to finish
    Receive-Job $job -Wait -AutoRemoveJob | Out-Host

    # Wait for trace to finish
    Wait-Job $traceJob -Timeout 30 | Out-Null
    Receive-Job $traceJob -Wait -AutoRemoveJob | Out-Host

    if (Test-Path $traceFile) {
        Write-Host "=== Trace saved to: $traceFile ===" -ForegroundColor Green
        Write-Host ""
        Write-Host "Inclusive top-N: " -ForegroundColor Cyan
        Write-Host "  dotnet-trace report ""$traceFile"" topN -n 50 --inclusive" -ForegroundColor White
        Write-Host ""
        Write-Host "Exclusive top-N: " -ForegroundColor Cyan
        Write-Host "  dotnet-trace report ""$traceFile"" topN -n 50" -ForegroundColor White
    }
} else {
    # No tracing - just run
    Receive-Job $job -Wait -AutoRemoveJob | Out-Host
}
