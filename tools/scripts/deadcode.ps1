param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$Strict
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$solution = Join-Path $repoRoot "miniArch.sln"
$srcDir = Join-Path $repoRoot "src"
$testDir1 = Join-Path $repoRoot "tests"
$testDir2 = Join-Path $repoRoot "tools\perf"
$searchDirs = @($srcDir, $testDir1, $testDir2)
$global:hasIssues = $false
$header = "=" * 60

# --- Phase 1: Build to trigger IDE0051/IDE0052 (unused private members) ---
if (-not $SkipBuild) {
    Write-Host $header -ForegroundColor Cyan
    Write-Host "  Phase 1: Build (IDE0051/IDE0052 check for unused private members)" -ForegroundColor Cyan
    Write-Host $header -ForegroundColor Cyan
    Write-Host ""

    $buildArgs = @(
        "build", $solution,
        "-c", $Configuration,
        "-v", "minimal",
        "/warnaserror:IDE0051;IDE0052"
    )
    if ($Strict) {
        $buildArgs += "/p:AnalysisLevel=latest-Recommended"
        $buildArgs += "/p:EnforceCodeStyleInBuild=true"
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        if ($Strict) {
            Write-Host "Build FAILED (strict mode). Some errors may be CA quality rules, not dead code." -ForegroundColor Red
        } else {
            Write-Host "Build FAILED - IDE0051/IDE0052 detected unused private members." -ForegroundColor Red
        }
        $hasIssues = $true
    } else {
        Write-Host "Build passed - no unused private members detected." -ForegroundColor Green
        if ($Strict) { Write-Host "(Strict mode: CA quality rules also passed.)" -ForegroundColor Green }
    }
    Write-Host ""
} else {
    Write-Host "Skipping build (--SkipBuild)" -ForegroundColor Yellow
    Write-Host ""
}

# --- Phase 2: rg scan for unreferenced public/internal symbols ---
Write-Host $header -ForegroundColor Cyan
Write-Host "  Phase 2: rg + sg scan for unused public/internal API" -ForegroundColor Cyan
Write-Host $header -ForegroundColor Cyan
Write-Host ""

# Prefer sg (ast-grep) for C#-aware matching, fallback to rg
$tool = $null
if (Get-Command "sg" -ErrorAction SilentlyContinue) {
    $tool = "sg"
} elseif (Get-Command "rg" -ErrorAction SilentlyContinue) {
    $tool = "rg"
}

if (-not $tool) {
    Write-Host "Neither sg (ast-grep) nor rg (ripgrep) found." -ForegroundColor Yellow
    Write-Host "Install one of them to enable public/internal dead code scanning:" -ForegroundColor Yellow
    Write-Host "  sg:  cargo install ast-grep --locked" -ForegroundColor Yellow
    Write-Host "  rg:  winget install BurntSushi.ripgrep.MSVC" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Only private member dead code was checked (via build, Phase 1)." -ForegroundColor Yellow
    Write-Host ""
    if ($hasIssues) { exit 1 }
    exit 0
}

# Helper: count references to a symbol name across src + test paths
function Get-ReferenceCount {
    param([string]$Name, [string]$DefFile)
    $count = 0
    if ($tool -eq "sg") {
        # When available, use sg for reference search
        $refs = & sg -l -e "$Name" $srcDir $testDir1 $testDir2 2>$null
    } else {
        $output = & rg -c -F "$Name" $srcDir $testDir1 $testDir2 2>$null
        if (-not $output) { return 0 }
        foreach ($line in $output) {
            $parts = $line -split ':'
            $c = [int]$parts[-1]
            $f = $parts[0..($parts.Length-2)] -join ':'
            if ($f -eq $DefFile) { $c-- }
            if ($c -gt 0) { $count += $c }
        }
    }
    return $count
}

# Find defs with a pattern, then check if each has >0 references outside its file
function Find-Unused {
    param(
        [string]$Pattern,
        [string]$SearchDir,
        [string]$Label,
        [string]$NameExtract
    )

    $defs = if ($tool -eq "sg") {
        & sg -n $Pattern $SearchDir 2>$null
    } else {
        & rg --no-heading -n $Pattern $SearchDir 2>$null
    }
    if (-not $defs) { return }

    foreach ($def in $defs) {
        $parts = $def -split ':', 3
        $file = $parts[0]
        $line = $parts[1]
        $text = $parts[2]

        # Extract symbol name
        $name = $null
        if ($NameExtract -eq "type") {
            if ($text -match '(class|struct|interface|enum|record)\s+(\w+)') { $name = $matches[2] }
        } elseif ($NameExtract -eq "method") {
            if ($text -match '(?:void|bool|int|string|float|double|long|byte|char|object|Task|ValueTask)\s+(\w+)\s*\(') { $name = $matches[1] }
        } elseif ($NameExtract -eq "field") {
            if ($text -match '(\w+)\s*;') { $name = $matches[1] }
        } else {
            if ($text -match '\s+(\w+)\s*(?:\(|;|{)') { $name = $matches[1] }
        }
        if (-not $name -or $name.Length -le 1) { continue }

        # Skip common inherited overrides and infrastructure
        if ($name -match '^(Equals|GetHashCode|ToString|GetEnumerator|Dispose|Clone|Deconstruct|Finalize|MemberwiseClone|GetType)$') { continue }

        $refs = Get-ReferenceCount -Name $name -DefFile $file
        if ($refs -eq 0) {
            $rel = [System.IO.Path]::GetRelativePath($repoRoot, $file)
            $global:hasIssues = $true
            Write-Host "  UNUSED  $Label $name" -ForegroundColor Red
            Write-Host "          at $rel : line $line" -ForegroundColor DarkRed
            Write-Host ""
        }
    }
}

# --- Scan public types ---
Write-Host "> Checking public types..." -ForegroundColor Gray
Find-Unused -Pattern '(?m)^\s*public\s+(partial\s+)?(class|struct|interface|enum|record)\s+' -SearchDir $srcDir -Label "public type" -NameExtract "type"

# --- Scan internal types ---
Write-Host "> Checking internal types..." -ForegroundColor Gray
Find-Unused -Pattern '(?m)^\s*internal\s+(partial\s+)?(class|struct|interface|enum|record)\s+' -SearchDir $srcDir -Label "internal type" -NameExtract "type"

# --- Scan public methods ---
Write-Host "> Checking public methods..." -ForegroundColor Gray
Find-Unused -Pattern '(?m)^\s*public\s+(?:(?:static|virtual|override|abstract|sealed|readonly|unsafe|partial|async)\s+)*\w+\s+\w+\s*\(' -SearchDir $srcDir -Label "public method" -NameExtract "method"

# --- Scan internal methods ---
Write-Host "> Checking internal methods..." -ForegroundColor Gray
Find-Unused -Pattern '(?m)^\s*internal\s+(?:(?:static|virtual|override|abstract|sealed|readonly|unsafe|partial|async)\s+)*\w+\s+\w+\s*\(' -SearchDir $srcDir -Label "internal method" -NameExtract "method"

# --- Summary ---
Write-Host ""
Write-Host $header -ForegroundColor Cyan
if ($global:hasIssues) {
    Write-Host "  RESULT: Dead code detected - review flagged symbols above." -ForegroundColor Red
    Write-Host $header -ForegroundColor Cyan
    Write-Host ""
    Write-Host "False-positive tips:" -ForegroundColor Yellow
    Write-Host "  - A symbol referenced only via reflection will show as unused." -ForegroundColor Yellow
    Write-Host "  - Extension methods defined in src/ but used in tests are not dead." -ForegroundColor Yellow
    Write-Host "  - Run with 'git clean -fd' first to remove stale artifacts." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "  RESULT: No dead code detected." -ForegroundColor Green
    Write-Host $header -ForegroundColor Cyan
    Write-Host ""
    exit 0
}
