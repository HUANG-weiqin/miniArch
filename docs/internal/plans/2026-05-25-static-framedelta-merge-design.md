# Static FrameDelta.Merge Design

**Goal:** Simplify FrameDelta API to a single static `Merge(a, b)` pure function.

**Changes:**
- Remove `Append(FrameDelta other)`
- Remove `Squash()`
- Remove instance `Merge(FrameDelta other)`
- Add `static FrameDelta Merge(FrameDelta a, FrameDelta b)` — pure, returns new FrameDelta

**Semantics:** Processes a's commands first, then b's, preserving chronological order. Folds component commands per (Entity, ComponentTypeId) with state machine. Returns new FrameDelta, inputs untouched.

**Tests:** Convert all existing Append/Squash/Merge tests to use static Merge.
