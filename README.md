# MiniArch

MiniArch is a small C# ECS learning project inspired by Arch.

## What Lives Here

- `src/MiniArch`: ECS runtime code
- `tests/MiniArch.Tests`: xUnit coverage for the runtime
- `docs/plans`: design and implementation notes
- `.knowledge`: reusable project knowledge for future agents
- `scripts`: one-command wrappers for build and test

## Quick Start

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\verify.ps1
```

## Agent Workflow

1. Read `AGENTS.md`.
2. Read `.knowledge/INDEX.md`.
3. Open the most relevant `kb-*.md` page.
4. Use `scripts\verify.ps1` before claiming a change is done.

## Current ECS Shape

- `World` owns entity lifecycle, archetype lookup, and query caching.
- `Archetype` owns chunk lists and transition edges.
- `Chunk` stores entities and component columns in a dense layout.
- `Signature` identifies archetypes by component set.
- `Query` filters archetypes first, then iterates chunks.
