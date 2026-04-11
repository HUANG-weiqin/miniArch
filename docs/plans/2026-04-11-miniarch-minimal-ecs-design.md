# MiniArch Minimal ECS Design

Date: 2026-04-11

## Goal

Build a small C# ECS project that reproduces the parts of Arch that are most useful for learning:

- entity lifetime management
- archetype construction
- chunk-based storage
- query acceleration by archetype filtering
- chunk iterator traversal
- structural changes through `create`, `add`, `set`, `remove`, `destroy`

The project should stay intentionally small. The goal is not feature parity with Arch. The goal is to understand why the memory layout and lookup strategy are fast.

## What We Keep From Arch

The implementation should borrow these ideas directly:

- `ComponentType` values are small integer ids instead of using `Type` as the runtime key.
- `Signature` represents the component set for an archetype.
- `World` stores a map from signature hash to archetype.
- Each archetype owns multiple chunks.
- Each chunk stores entities and components in structure-of-arrays layout.
- `Query` first filters archetypes, then iterates chunks.
- `Query` should cache matching archetypes when the archetype set has not changed.
- Structural changes should use cached transition edges between archetypes when possible.
- Entity removal should use swap-remove so compaction stays O(1).
- Entities should use versioned ids to detect stale references.

## What We Do Not Keep

The first version should exclude:

- events
- jobs / multithreading
- source generators
- non-generic APIs
- benchmark code
- Unity / Godot integration helpers
- any “rich” query system beyond the minimum required for chunk iteration

This keeps the code small enough that the storage and migration logic stays visible.

## Core Architecture

### 1. Component Registry

Responsibilities:

- register component types
- assign stable integer ids
- map `Type -> ComponentType`
- map `id -> Type`

Why it matters:

- archetype signatures become compact
- chunk lookups can use ids instead of reflection-heavy type comparisons
- query matching can use bitsets or compact signatures later if needed

### 2. Signature

Responsibilities:

- represent a component set
- act as the lookup key for archetypes
- act as the basis for query matching

Design notes:

- store component ids in a stable, comparable structure
- cache hash codes
- treat order consistently so signature equality is deterministic

Arch lesson to carry over:

- signature objects are cheap identifiers for archetypes
- the hash is used to short-circuit lookup, but equality should still be correct

### 3. Chunk

Responsibilities:

- store entities contiguously
- store each component type in its own contiguous array
- support insertion
- support set/update in place
- support swap-remove

Layout:

- `Entity[]`
- one array per component column

Arch lesson to carry over:

- SoA storage is the key to iteration speed
- chunk-local memory should be contiguous
- removal should copy the last row into the hole instead of shifting everything

### 4. Archetype

Responsibilities:

- own one signature
- own a list of chunks
- track total entity count
- track which chunk is currently receiving insertions
- provide add/remove transition helpers

Arch lesson to carry over:

- archetype is the unit of query filtering
- it is also the unit of migration
- it should be the place where chunk growth is decided

Chunk allocation policy:

- create at least one chunk when the archetype is created
- fill the current chunk until it is full
- move to the next preallocated chunk when available
- allocate a new chunk only when all existing ones are full

This matches the Arch pattern and makes allocation behavior predictable.

### 5. World

Responsibilities:

- create entities
- destroy entities
- add components
- set components
- remove components
- resolve the entity’s current location
- cache archetypes by signature
- cache queries by query description

Entity metadata:

- `archetype`
- `chunk index`
- `row index`
- `version`

Arch lesson to carry over:

- world state should be split between metadata and storage
- structural changes should update the metadata and storage together
- stale entity handles must fail through version mismatch

### 6. Query

Responsibilities:

- describe a component filter
- find matching archetypes
- return a chunk iterator

Iteration order:

1. resolve matching archetypes
2. iterate chunks inside each matching archetype
3. iterate rows inside each chunk

Arch lesson to carry over:

- query should not scan entities one by one across the entire world
- filtering at the archetype level is the main win
- returning an iterator over chunks preserves locality and keeps the API lightweight

## Data Flow

### Create

1. create or recycle an entity id
2. resolve the target signature
3. find or create the archetype
4. insert the entity into a chunk
5. write entity metadata back to the world

### Add / Remove

1. read the entity’s current archetype
2. compute the destination signature
3. try to reuse an archetype transition edge
4. if the edge does not exist, create the new archetype and cache the edge
5. move the entity to the destination archetype
6. copy or remove component data as needed
7. update entity metadata

### Set

Two cases:

- if the component already exists, write in place
- if the component does not exist, treat it as an add and move to a new archetype

### Destroy

1. read the entity metadata
2. swap-remove from the current chunk
3. update metadata for the entity that was moved into the hole
4. recycle the destroyed entity id and bump its version

## Archetype Transition Cache

Arch uses transition edges so repeated structural changes do not recompute the same destination archetype over and over.

We should keep this idea.

For each archetype:

- cache destination archetype for “add component X”
- cache destination archetype for “remove component X”

This is useful because:

- adding/removing the same component pattern happens repeatedly in games
- archetype construction is deterministic
- the cache turns repeated graph traversal into a direct lookup

Implementation approach:

- key edges by component id
- store them per archetype
- invalidate them only when the world destroys archetypes or resets

## Query Acceleration

Arch’s query speed comes mostly from two layers:

- archetype filtering
- cached query matching

We should keep both:

- cache query descriptions in the world
- cache the matching archetype list inside the query object
- refresh the cache only when the archetype set changes

For the first version, query matching can be based on:

- `all` components must be present
- later, optional `any`, `none`, `exclusive` can be added if needed

To keep the first version focused, the primary requirement is:

- `world.query(...)` returns a chunk iterator over matching archetypes

## Memory Model

Important rules:

- entity data should be compact and contiguous where possible
- chunks should hold arrays directly rather than indirect per-entity objects
- archetype chunk lists should grow in large steps instead of one element at a time
- swap-remove should preserve dense packing
- metadata lookups should be O(1)

The point is to make the memory access pattern obvious:

- metadata lookup by entity id
- archetype lookup by signature
- chunk iteration over contiguous rows

## Suggested File Layout

- `src/MiniArch/Core/ComponentRegistry.cs`
- `src/MiniArch/Core/Entity.cs`
- `src/MiniArch/Core/Signature.cs`
- `src/MiniArch/Core/Chunk.cs`
- `src/MiniArch/Core/Archetype.cs`
- `src/MiniArch/Core/World.cs`
- `src/MiniArch/Core/Query.cs`
- `src/MiniArch/Core/Iterators.cs`

## Implementation Order For The Agent

1. Build `ComponentRegistry`, `Entity`, and `Signature`.
2. Build `Chunk` with SoA arrays and swap-remove.
3. Build `Archetype` with chunk allocation and entity insertion/removal.
4. Build `World` with entity metadata and archetype lookup.
5. Add structural changes: `add`, `set`, `remove`, `destroy`.
6. Build `Query` with archetype filtering and chunk iteration.
7. Add edge cache for archetype transitions.
8. Add a small test suite covering:
   - entity version reuse
   - chunk insertion and removal
   - archetype migration
   - query chunk iteration

## Acceptance Criteria

The first version is good enough if:

- entities can be created and destroyed safely
- component additions/removals move entities between archetypes correctly
- chunk storage remains dense
- query returns chunk iterators over matching archetypes
- repeated add/remove transitions reuse cached destination archetypes
- no event/job/template system is present

## Notes

The most important thing to preserve from Arch is not API surface. It is the data-oriented flow:

- compact ids
- signature-based archetypes
- chunk-based SoA storage
- cached query matching
- cached archetype transitions
- O(1) structural removal

That is the part worth studying and reproducing.
