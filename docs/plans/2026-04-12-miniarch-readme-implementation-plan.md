# MiniArch README 实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deliver a production-usable README set for MiniArch, with a short repository entry README and a detailed API handbook in `src/MiniArch/README.md`.

**Architecture:** Keep the repository README focused on navigation and move all API detail into the library README. Document `MiniArch.Ecs` and `MiniArch.Core` separately, mark concurrency boundaries explicitly, and validate the result with tests plus three persona reviews.

**Tech Stack:** Markdown, .NET 8, xUnit

---

### Task 1: Lock the documentation scope

**Files:**
- Modify: `README.md`
- Modify: `src/MiniArch/README.md`
- Reference: `.knowledge/kb-user-api-layering.md`
- Reference: `.knowledge/kb-core-ecs.md`
- Reference: `.knowledge/kb-command-buffer-feasibility.md`

**Step 1: Confirm target split**

- Root README stays short and links to the full API manual.
- Library README becomes the single source of truth for public API reference.

**Step 2: Confirm public API inventory**

- Use the public members from `MiniArch.Ecs` and `MiniArch.Core` only.
- Exclude internal helpers from the API reference.

### Task 2: Rewrite the repository README as an entry page

**Files:**
- Modify: `README.md`

**Step 1: Replace the current minimal content**

- Keep project purpose, repository layout, quick commands, and collaboration entry points.
- Add a prominent link to `src/MiniArch/README.md`.

**Step 2: Self-check**

- Verify the root README is short and not duplicating the full API reference.

### Task 3: Write the detailed library README

**Files:**
- Modify: `src/MiniArch/README.md`

**Step 1: Add reader guidance**

- Explain when to choose `MiniArch.Ecs` vs `MiniArch.Core`.
- Add a concurrency legend and an explicit non-goal note for concurrent world writes.

**Step 2: Add minimal examples**

- User-facing `Create + TryGet + Query`
- Advanced `QueryDescription`
- Multi-threaded `CommandBuffer` recording
- `WorldSnapshot.Save/Load`

**Step 3: Add full public API reference**

- Document each public type and its public members.
- Mark thread-related APIs with the agreed tags.
- Call out important behavioral edges and limitations.

### Task 4: Verify correctness against code and tests

**Files:**
- Reference only: `src/MiniArch/**/*.cs`
- Reference only: `tests/MiniArch.Tests/**/*.cs`

**Step 1: Run the test suite**

Run: `powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1`

**Step 2: Fix documentation mismatches if any are discovered**

- Update README wording, not runtime code, unless verification reveals a real API inconsistency.

### Task 5: Review with three developer personas

**Files:**
- Modify: `src/MiniArch/README.md` if review findings require it

**Step 1: Request three focused reviews**

- Persona A: ordinary gameplay developer
- Persona B: advanced ECS/performance developer
- Persona C: concurrency/command-buffer oriented developer

**Step 2: Iterate until all three pass**

- For each failed review, address the clarity or correctness gap.
- Re-run review until all three report the README is usable.
