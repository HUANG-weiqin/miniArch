# MiniArch Snapshot Persistence Design

Date: 2026-04-11

## Goal

Build a first-version snapshot persistence layer for `MiniArch` that supports fast save/load of a full `World` by reusing the ECS chunk/column layout.

## Scope

This first version explicitly supports:

- full-world save/load
- same-version read/write only
- `unmanaged` component payloads only
- compact binary snapshot format
- fast sequential write and fast batch-oriented load

This first version explicitly excludes:

- cross-version migration
- partial save/load
- incremental or diff snapshots
- reference-type component persistence
- external protocol stability across future major refactors

## Problem Statement

The current runtime `Chunk` is not a single stable raw memory block. It is a managed object graph with:

- an `Entity[]`
- one managed array per component column
- runtime-only lookup tables
- runtime-only archetype/query caches outside the chunk itself

That means the runtime chunk cannot be persisted by blindly copying CLR object memory. The persistence layer must instead export the logical chunk content into a stable binary format.

## Design Summary

The persistence layer will treat the world as:

- file header
- component schema table
- archetype records
- chunk records
- column payload blocks

The runtime still uses `ComponentType` as the internal hot-path id. The snapshot file will not store runtime `ComponentType.Value` directly. Instead it will store a stable schema key derived from the runtime component `Type`.

## Key Decisions

### 1. Runtime ids stay internal

`ComponentType` remains the runtime lookup key for:

- signatures
- archetype transitions
- chunk column lookup
- queries

It is not used as the persisted schema id because it depends on runtime registration order.

### 2. Persist stable schema names

The snapshot file stores a stable schema identity per persisted component, based on the component type's assembly-qualified name.

This is acceptable for the first version because:

- the user accepted same-version compatibility only
- it avoids inventing a separate schema registry before it is needed
- it keeps the persistence layer small

### 3. Persist only effective rows

Each chunk record stores only `Count` rows, not `Capacity`. This avoids persisting slack space and keeps the file smaller.

### 4. Persist only reconstructible world state

The snapshot stores:

- entity ids
- entity slot versions
- archetype signatures
- per-column payloads

The snapshot does not store:

- query caches
- archetype edge caches
- component-id-to-column lookup arrays
- chunk slack capacity

These are rebuilt during load.

### 5. Require `unmanaged` payload columns

The first version only supports components whose payloads can be copied as raw bytes. Any component that is not registered as snapshot-safe must cause save/load to fail fast with a clear error.

## Binary Format

### File Header

The file begins with:

- magic bytes
- format version
- chunk capacity
- entity slot count
- component schema count
- archetype count

### Component Schema Table

Each persisted component entry stores:

- schema name string

The schema table lets the file refer to components by compact schema index inside archetype/chunk records.

### Archetype Record

Each archetype stores:

- component count
- ordered schema indices for the archetype signature
- chunk count

### Chunk Record

Each chunk stores:

- row count
- entity id block
- one raw payload block per column, ordered by the archetype signature

For each payload block, the writer stores exactly `rowCount * sizeof(component)` bytes.

### Entity Slot Version Table

The snapshot also stores one version per entity slot outside chunk records. This lets load rebuild free entity ids without losing version history.

## Save Flow

1. Enumerate all world archetypes.
2. Build the set of persisted component schemas used by those archetypes.
3. Write the file header and schema table.
4. For each archetype, write the ordered signature.
5. For each chunk with `Count > 0`:
   - write row count
   - write entity ids
   - write each component column payload as raw bytes

## Load Flow

1. Validate header magic and format version.
2. Read the schema table and map each schema name back to a runtime `Type`.
3. Register those runtime types into the world's `ComponentRegistry`.
4. Recreate or resolve the runtime archetype for each persisted signature.
5. Materialize entities/chunk rows in batch while preserving snapshot chunk boundaries.
6. Fill entity arrays and component columns from the stored raw blocks.
7. Rebuild world metadata:
   - `_versions`
   - `_locations`
   - archetypes/chunks

## Required Runtime Extensions

To keep load/save fast and avoid abusing the high-level structural-change APIs, the runtime needs small internal extension points:

- `World` internal APIs for persistence-oriented archetype resolution and metadata reconstruction
- `Chunk` internal APIs to expose typed raw column arrays for `unmanaged` copy
- `Archetype` batch reservation reuse where possible

The persistence layer should not call `World.Add/Set/Remove` during load. That would turn loading into repeated structural changes and defeat the design goal.

## Testing Strategy

The first version should prove:

- a world containing only unmanaged components can round-trip through snapshot
- entity versions survive round-trip
- free entity slot versions survive round-trip
- multiple archetypes survive round-trip
- multiple chunks inside one archetype survive round-trip
- unsupported component payloads fail with a clear exception

## Acceptance Criteria

The feature is complete when:

- full-world save/load works for unmanaged components
- snapshot round-trip preserves entity ids, versions, archetype membership, and component values
- tests prove multi-archetype and multi-chunk round-trip behavior
- unsupported component types fail deterministically
- the implementation runs in a dedicated worktree without contaminating the original working directory
