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
