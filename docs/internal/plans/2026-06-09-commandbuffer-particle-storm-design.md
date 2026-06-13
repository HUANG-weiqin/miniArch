# CommandBuffer Particle Storm Benchmark Design

## Goal

Add a new benchmark scenario to `perf/CommandBufferGame.Perf` that stresses **high-intensity structural churn**: massive entity creation/destruction with diversified archetype signatures, minimal Set operations.

## Scenario

`ParticleStormWorld` simulates a particle system with short-lived entities:

- **4,000 entities spawned per tick**, distributed across 4 archetype variants.
- **2-tick lifespan** via ring buffer—entities created on tick N are destroyed on tick N+2.
- Steady pool of ~8,000 active particles.
- **100 emitter entities** (persistent, receive lightweight Set updates each tick for checksum).
- **Benchmark focus**: Create/Add/Destroy throughput under archetype fragmentation.

### Archetype Distribution (4,000/tick)

| Type | Ratio | Components | Count/tick |
|---|---|---|---|
| A (simple) | 30% | Position + Velocity + Alpha | 1200 |
| B (colored) | 30% | Position + Velocity + Color + Scale | 1200 |
| C (fading) | 20% | Position + Velocity + Alpha + Lifetime | 800 |
| D (complex) | 20% | Position + Velocity + Color + Scale + Lifetime | 800 |

4 distinct archetypes → 4 chunk chains, creating archetype split/merge pressure on every tick.

### Per-tick Flow (all engines identical)

```
1. Query 100 emitter entities, accumulate checksum (minimal, <0.1%)
2. Record phase:
   a. Destroy batch from (tick-2) — 4000 entities
   b. Set 100 emitter component values
   c. Create 4000 entities across 4 archetypes
   d. Add components for each entity
3. Submit/Playback
```

### Ring Buffer Strategy

- `EntityBuffer[][]` = `[STORM_TICK_BUFFER][batchSize]`, where `STORM_TICK_BUFFER = 2`.
- `buffer[tick % 2]` holds current tick's created handles.
- `buffer[(tick + 1) % 2]` holds handles to destroy (created 2 ticks ago when lifespan = 2).
- No query-based lifetime scan needed; pure index math.

### New Component Structs

- `Alpha` (int)
- `Color` (int R, G, B, A — packed or separate ints)
- `Scale` (int)
- `EmitterTag` (int — marker for the 100 persistent entities)

All implement `Friflo.Engine.ECS.IComponent`.

### Implementation

Add 4 new scenario classes following `ICommandBufferGameScenario`:

| Engine | Class Name |
|---|---|
| MiniArch CommandBuffer | `MiniArchParticleStormWorld` |
| MiniArch CommandStream | `MiniArchCommandStreamParticleStormWorld` |
| Friflo | `FrifloParticleStormWorld` |
| Arch | `ArchParticleStormWorld` |

Wire into `Main()` after the existing SteadyCombat scenarios.

### Completion Criteria

- New scenarios build and run in Release.
- Output table includes all 4 engines for ParticleStorm.
- Live count is 100 + 8000 = 8100 for all engines (steady pool).
- Knowledge base updated.
