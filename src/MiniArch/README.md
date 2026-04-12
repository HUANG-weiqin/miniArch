# MiniArch

Minimal ECS learning project inspired by Arch.

## Scope

- versioned entities
- component registration
- signature-based archetypes
- chunk storage
- archetype-filtered queries
- cached archetype transitions
- query result caching keyed by archetype generation

## Current Behavior

- entities are created in the empty archetype
- component add/remove operations migrate entities between archetypes
- chunks use dense structure-of-arrays storage
- queries filter archetypes first, then iterate their chunks
- `MiniArch.Core.CommandBuffer` records structural and hierarchy changes without mutating the world immediately
- `CommandBuffer.Playback()` compiles one frame of commands and `World.Replay(in FrameCommands)` applies them in fixed order `create -> link/unlink -> add -> set -> remove -> destroy`
- `CommandBuffer.Play()` applies the same compiled semantics directly to the owning world without materializing `FrameCommands`, which is the lower-allocation hot path when cross-world replay is not needed
- command buffer `Create()` reserves a real entity handle during recording; same-frame `create + destroy` pairs are released during replay without ever materializing into an archetype
- preserved `FrameCommands` can be replayed into another world that starts from the same state and advances with the same frame order
- concurrent support is limited to command recording; world mutation still happens on one thread during replay

## API Layers

- `MiniArch.Ecs`
  - user-facing API for game logic
  - `World`, `Entity`, `Query<T>`, `Query<T1, T2>`
  - default queries support direct `foreach`
  - `World.IsAlive(entity)` is the common lifecycle check for a handle against the current world state
  - `World.Advanced` is the escape hatch back to `MiniArch.Core`
- `MiniArch.Core`
  - advanced API for storage-aware access and profiling
  - `Chunk`, `Archetype`, `Signature`, `QueryBuilder`, `ComponentRegistry`

## User Query Example

```csharp
using MiniArch.Ecs;

var world = new World();
var entity = world.Create(new Position(1, 2), new Velocity(3, 4));

if (world.TryGet(entity, out Position position))
{
    Console.WriteLine(position);
}

if (world.IsAlive(entity))
{
    Console.WriteLine("entity is still alive");
}

foreach (var item in world.Query<Position, Velocity>())
{
    Console.WriteLine($"{item.Entity}: {item.First} / {item.Second}");
}
```
