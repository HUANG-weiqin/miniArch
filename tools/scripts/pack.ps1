param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\MiniArch\MiniArch.csproj"

& dotnet pack $project -c $Configuration -v minimal
