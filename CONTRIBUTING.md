# Contributing

## Getting Started

```powershell
# Build
dotnet build miniArch.sln

# Run tests
dotnet test miniArch.sln

# Run benchmarks
dotnet run -c Release --project tests/MiniArch.Benchmarks

# Check for dead code (requires ripgrep: winget install BurntSushi.ripgrep.MSVC)
.\tools\scripts\deadcode.ps1
```

## Performance Gate

Any change to `src/MiniArch/` requires a performance regression check:

```powershell
dotnet run -c Release --project tools/perf/HeroComing.Perf
```

Check that throughput is above thresholds (Movement ≥866 rounds/s, Attack ≥200 rounds/s) and memory is stable.

## Project Structure

```
src/MiniArch/             # Library source
tests/                    # Tests & benchmarks
  MiniArch.Tests/         # Unit tests
  MiniArch.Benchmarks/    # Public benchmarks
  HeroPipeline.Tests/     # Pipeline integration tests
  SharedInfrastructure/   # Shared test infrastructure
tools/
  perf/                   # Performance regression & comparison tests
  scripts/                # Development scripts
docs/
  comparison.md           # Comparison with other ECS libraries
  internal/               # Internal development documentation
.knowledge/               # Agent knowledge base (for AI-assisted development)
AGENTS.md                 # Agent instructions (for AI-assisted development)
```

## Agent-Assisted Development

This project supports AI-agent-assisted development. If you use AI coding tools, please read:

- `AGENTS.md` — Agent workflow instructions
- `.knowledge/` — Project knowledge base

## Code Conventions

- Source code identifiers and comments: English
- Docs, plans, and collaboration: Chinese
- Follow the existing code style in the project

## Pull Request Process

1. Ensure all tests pass
2. Run the performance gate if touching core ECS code
3. Update `.knowledge/` if new architectural knowledge was gained
4. Update CHANGELOG.md with notable changes
