param(
    [string]$Scenario = "",
    [ValidateRange(0, 2147483647)]
    [int]$Warmup = 3,
    [ValidateRange(1, 2147483647)]
    [int]$Measure = 10,
    [ValidateRange(0, 2147483647)]
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

$traceEnabled = (-not $NoTrace) -and ($TraceSeconds -gt 0)
if ($traceEnabled -and ($TraceSeconds + 2 -gt $Measure)) {
    throw "TraceSeconds must be at least 2 seconds shorter than Measure so tracing starts and finishes during measurement."
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

if ($traceEnabled) {
    $pidFile = Join-Path $profileOutputDir "profile.pid"
    $stopFile = Join-Path $profileOutputDir "profile.stop"
    if (Test-Path $pidFile) {
        Remove-Item $pidFile -Force
    }
    if (Test-Path $stopFile) {
        Remove-Item $stopFile -Force
    }
    $runnerArgs += "--profile-ready-file", $pidFile
    $runnerArgs += "--profile-stop-file", $stopFile
}

Write-Host "=== Scenario: $(if ($Scenario) { $Scenario } else { 'all' }) ===" -ForegroundColor Cyan
Write-Host "Warmup: ${Warmup}s, Measure: ${Measure}s" -ForegroundColor Cyan

$job = Start-Job -ScriptBlock {
    param($runnerArgsInJob, $repoRootInJob)
    Set-Location $repoRootInJob
    & dotnet @runnerArgsInJob
    if ($LASTEXITCODE -ne 0) {
        throw "CommandStream.Profile exited with code $LASTEXITCODE."
    }
} -ArgumentList $runnerArgs, $repoRoot

if ($traceEnabled) {
    Write-Host "Waiting for runner to finish warmup and start measurement (polling $pidFile)..." -ForegroundColor Yellow

    # The runner writes the PID only after warmup, immediately before measurement.
    $readyTimeoutSeconds = $Warmup + 10
    $readyWait = [Diagnostics.Stopwatch]::StartNew()
    $targetPid = $null
    while ($readyWait.Elapsed.TotalSeconds -lt $readyTimeoutSeconds) {
        if (Test-Path $pidFile) {
            $candidatePid = Get-Content $pidFile -Raw -ErrorAction SilentlyContinue
            if ($candidatePid) {
                $candidatePid = $candidatePid.Trim()
            }
            if ($candidatePid -and $candidatePid -match '^\d+$') {
                $targetPid = $candidatePid
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }

    if (-not $targetPid) {
        Stop-Job $job
        Remove-Job $job -Force
        throw "Timed out waiting for the measurement-ready marker."
    }

    Write-Host "Runner PID: $targetPid" -ForegroundColor Green
    Write-Host "Attaching dotnet-trace for ${TraceSeconds}s..." -ForegroundColor Yellow

    $traceFile = Join-Path $profileOutputDir "commandstream-$(if ($Scenario) { $Scenario } else { 'all' })-$(Get-Date -Format 'yyyyMMdd-HHmmss').nettrace"

    $traceJob = $null
    $traceAccepted = $false
    try {
        $traceJob = Start-Job -ScriptBlock {
            param($targetPid, $traceFile, $duration)
            dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler --process-id $targetPid --duration $("00:{0:mm}:{0:ss}" -f (New-TimeSpan -Seconds $duration)) -o $traceFile
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet-trace exited with code $LASTEXITCODE."
            }
        } -ArgumentList $targetPid, $traceFile, $TraceSeconds

        # The runner writes stopFile before taking its measurement-stop timestamp.
        # A trace is valid only when collection fully stops before that marker appears.
        $completedTraceJob = Wait-Job $traceJob -Timeout ($TraceSeconds + 30)
        if ($null -eq $completedTraceJob) {
            throw "Timed out waiting for dotnet-trace to finish."
        }

        Receive-Job $traceJob -ErrorAction Continue | Out-Host
        if ($traceJob.State -ne "Completed") {
            throw "dotnet-trace failed."
        }
        if (Test-Path $stopFile) {
            throw "Trace collection did not finish before measurement stopped; result discarded."
        }
        if (-not (Test-Path $traceFile)) {
            throw "dotnet-trace completed without producing a trace file."
        }

        $completedRunnerJob = Wait-Job $job -Timeout ($Measure + 30)
        if ($null -eq $completedRunnerJob) {
            throw "Timed out waiting for CommandStream.Profile to finish."
        }
        Receive-Job $job -ErrorAction Continue | Out-Host
        if ($job.State -ne "Completed") {
            throw "CommandStream.Profile failed."
        }

        $traceAccepted = $true
        Write-Host "=== Trace saved to: $traceFile ===" -ForegroundColor Green
        Write-Host ""
        Write-Host "Inclusive top-N: " -ForegroundColor Cyan
        Write-Host "  dotnet-trace report ""$traceFile"" topN -n 50 --inclusive" -ForegroundColor White
        Write-Host ""
        Write-Host "Exclusive top-N: " -ForegroundColor Cyan
        Write-Host "  dotnet-trace report ""$traceFile"" topN -n 50" -ForegroundColor White
    }
    finally {
        if ($null -ne $traceJob) {
            if ($traceJob.State -notin @("Completed", "Failed", "Stopped")) {
                Stop-Job $traceJob
            }
            Remove-Job $traceJob -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $job) {
            if ($job.State -notin @("Completed", "Failed", "Stopped")) {
                Stop-Job $job
            }
            Remove-Job $job -Force -ErrorAction SilentlyContinue
        }
        if (-not $traceAccepted -and (Test-Path $traceFile)) {
            Remove-Item $traceFile -Force
        }
    }
} else {
    # No tracing - just run
    Receive-Job $job -Wait -AutoRemoveJob | Out-Host
}
