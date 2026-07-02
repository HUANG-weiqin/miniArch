# CommandStream Profile Implementation Plan

> **For future agents:** implement task-by-task; keep the runner independent from `src/MiniArch/` runtime code unless sampling proves deeper counters are needed.

**Goal:** Add a CommandStream-only profiling runner and helper script that can drive repeatable workloads and attach CPU sampling.

**Architecture:** Create a new console project under `tools/perf/CommandStream.Profile` referencing `src/MiniArch`. The runner owns workload setup, warmup, measurement, phase timing, and output. A PowerShell script starts the runner and optionally attaches `dotnet-trace`.

**Tech Stack:** .NET 8 console app, MiniArch public API, `Stopwatch`, `GC.GetTotalMemory`, optional `dotnet-trace` CLI.

---

### Task 1: Add console project shell

**Files:**
- Create: `tools/perf/CommandStream.Profile/CommandStream.Profile.csproj`
- Create: `tools/perf/CommandStream.Profile/Program.cs`
- Modify: `miniArch.sln`

**Steps:**
1. Create a net8.0 executable project referencing `src/MiniArch/MiniArch.csproj`.
2. Add a minimal `Program.Main` that parses `--scenario`, `--warmup`, `--measure`, `--attach-delay`, `--profile-ready-file`, and `--list`.
3. Add the project to `miniArch.sln` with `dotnet sln miniArch.sln add tools/perf/CommandStream.Profile/CommandStream.Profile.csproj`.
4. Verify with `dotnet build -c Release tools/perf/CommandStream.Profile/CommandStream.Profile.csproj`.

### Task 2: Implement workload interface and common runner

**Files:**
- Modify: `tools/perf/CommandStream.Profile/Program.cs`

**Steps:**
1. Add `ICommandStreamScenario` with `Name`, `LiveCount`, `RunTick()` and phase counters.
2. Add `BenchmarkRunner` for warmup + measurement loops.
3. Output PID, scenario, duration, ticks/s, ms/tick, phase percentages, live count, heap delta, and GC count.
4. Keep timing external to `src/MiniArch/` runtime.

### Task 3: Implement first workload set

**Files:**
- Modify: `tools/perf/CommandStream.Profile/Program.cs`

**Steps:**
1. Add reusable profile components: `Position`, `Velocity`, `Health`, `Damage`, `TagA`, `TagB`, `TagC`, `TagD`.
2. Implement scenarios:
   - `existing-set`
   - `existing-add-remove`
   - `create-small4`
   - `create-duplicates`
   - `create-destroy`
   - `snapshot-only`
3. Keep each workload deterministic and self-contained.
4. Ensure all scenarios run without unbounded entity growth except intentional steady-state create/destroy cases.

### Task 4: Add profiling helper script

**Files:**
- Create: `tools/scripts/profile-commandstream.ps1`

**Steps:**
1. Accept `-Scenario`, `-Warmup`, `-Measure`, `-TraceSeconds`, `-OutputDir`, `-NoTrace`.
2. Build the profile project in Release.
3. Start runner as child process with a ready file containing PID.
4. If tracing is enabled, run `dotnet-trace collect --profile cpu-sampling --process-id <pid> --duration ...`.
5. Print `dotnet-trace report <file> topN -n 50 --inclusive` and `--exclusive` commands.

### Task 5: Verify and document

**Files:**
- Modify: `.knowledge/kb-command-stream.md`
- Modify: `.knowledge/INDEX.md` only if adding a new knowledge page (not expected)

**Steps:**
1. Run:
   - `dotnet build -c Release miniArch.sln`
   - `dotnet run -c Release --project tools/perf/CommandStream.Profile -- --list`
   - `dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4 --warmup 1 --measure 2`
   - `dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario snapshot-only --warmup 1 --measure 2`
2. Update `kb-command-stream.md` with runner purpose and commands.
3. Since `src/MiniArch/` is not modified, HeroComing perf gate is not required by AGENTS §5a; run it only if runtime code changes.
