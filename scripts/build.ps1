param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

. $PSScriptRoot\env.ps1
Initialize-MiniArchScriptEnvironment

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\MiniArch\MiniArch.csproj"

& dotnet build $project -c $Configuration -v minimal
