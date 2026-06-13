# CommandBuffer Game Steady-State Benchmark Design

## Goal

Create an independent Release-only perf project that compares MiniArch, Friflo, and Arch under a realistic long-running game tick where structural changes are recorded through each engine's command buffer and then applied to a reused world/store.

## Scenario

`SteadyCombatWorld` keeps a stable live population and runs repeated game ticks:

1. Query pass reads active combat entities and accumulates a checksum.
2. Record pass writes commands into a command buffer:
   - spawn a fixed number of entities,
   - despawn the same number through FIFO lifetime tracking,
   - set/update hot components on selected existing entities,
   - add/remove several status components to force archetype churn.
3. Playback/submit applies the commands to the same world/store.
4. The benchmark reports total tick throughput, checksum, GC counts, heap delta, live count, and phase timing for query, record, and playback.

## Fairness rules

- Use NuGet packages for Friflo and Arch; use the local MiniArch project reference.
- Reuse the world/store and command buffer across ticks where the engine API supports it.
- Use the same deterministic index schedule and FIFO queue shape for all engines.
- Do not recreate worlds inside the measurement loop.
- Avoid benchmark container artifacts such as `List.RemoveAt(0)`; use `Queue<T>` for FIFO lifecycle.
- Measure only the steady tick loop after setup and warmup.

## Project shape

- New project: `perf/CommandBufferGame.Perf/CommandBufferGame.Perf.csproj`.
- Single executable source to keep the scenario independent from the older commandbuffer microbenchmark.
- CLI defaults: 3s warmup, 10s measurement, one `SteadyCombatWorld` scenario.
- Command: `dotnet run -c Release --project perf/CommandBufferGame.Perf`.

## Completion criteria

- The new project builds and runs in Release.
- Output includes all three engines and their steady-state metrics.
- No world recreation happens inside timed iterations.
- Knowledge index/page is updated for the new perf module.
