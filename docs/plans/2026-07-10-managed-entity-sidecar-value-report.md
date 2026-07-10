# Managed Entity Sidecar Value Report

## Verdict

- **No-Go**：不要把 `Entity -> managed object` sidecar 作为 miniArch public API 纳入 v1。
- 一句话理由：候选库版能打败 dictionary，也能封装常见正确性坑，但没有明显优于 competent dense user；serialization 也不应成为 v1 价值主张。

## Why This Exists / Why Not

`Entity -> managed object` sidecar 本质是 host-local 旁路表：用 entity id 直索引保存不进入 ECS chunk 的托管对象引用。它解决的是渲染/引擎对象/外部资源绑定这类非确定性状态。

它不应该进入当前库的原因：

1. 它不参与 miniArch 的核心确定性路径：不进 lockstep、FrameDelta、Snapshot、Checksum、Replay。
2. 它的高性能形态就是 dense array + `world.IsAlive(entity)` + version；competent user 可以手写到接近同等性能。
3. 候选库版的主要价值是封装坑，而不是提供库独占能力。当前证据不足以支付新增 public API、命名、文档和长期兼容成本。
4. serialization 会把简单 runtime sidecar 扩张成 codec/remap/resource persistence 框架，v1 不应承担。

## Correctness Evidence

命令：

```bash
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --correctness-only
```

结论：

- `CompDict`、`CompDenseUser`、`ProtoMap`：全部 PASS。
- `NaiveDict`：在 zombie、slot reuse、align、invalid entity、null policy 场景 FAIL。
- `RawDenseUnsafe`：在 zombie、slot reuse、align、invalid entity、null policy 场景 FAIL。

关键场景：

| 场景 | 结论 |
|---|---|
| Destroy 后 Align 前读取 | naive/raw 会读到 zombie；competent/proto 通过 `world.IsAlive` 拦截 |
| Destroy + slot reuse | naive/raw 暴露旧 handle；competent/proto 正确区分 version |
| Align 清理 zombie | naive/raw 无法清理；competent/proto Count 归零 |
| Invalid entity / placeholder | naive/raw 会接受部分无效 handle；competent/proto 安全失败 |
| `Set(null)` | naive/raw 接受 null；competent/proto 禁止 |

正确性价值真实存在，但主要是相对 naive/raw；一旦用户写到 competent dense 版本，库版不再有系统性优势。

## Performance Evidence

原始结果：

- `tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-n10000-r5.md`
- `tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-n100000-r5.md`
- `tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-n1000000-r3.md`
- 汇总：`tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-summary.md`

### 100k live entities, mapping 100%, destroy 10%

| Map | Set | Get hit | TryGet hit | TryGet miss | Remove | Align |
|---|---:|---:|---:|---:|---:|---:|
| CompetentDictionaryMap | 66.7 ns | 57.6 ns | 59.0 ns | 33.4 ns | 106.0 ns | 23.2 ns |
| RawDenseUnsafeMap | 14.4 ns | 10.1 ns | 10.3 ns | 17.4 ns | 9.9 ns | 0.0 ns |
| CompetentDenseUserMap | 16.4 ns | 11.8 ns | 11.8 ns | 17.0 ns | 15.0 ns | 2.7 ns |
| ManagedEntityMapPrototype | 24.6 ns | 12.3 ns | 12.4 ns | 18.0 ns | 13.1 ns | 2.6 ns |

### 1M live entities, mapping 100%, destroy 10%

| Map | Set | Get hit | TryGet hit | TryGet miss | Remove | Align |
|---|---:|---:|---:|---:|---:|---:|
| CompetentDictionaryMap | 24.2 ns | 24.1 ns | 13.7 ns | 34.0 ns | 28.5 ns | 8.5 ns |
| RawDenseUnsafeMap | 13.0 ns | 9.4 ns | 9.7 ns | 23.0 ns | 8.5 ns | 0.0 ns |
| CompetentDenseUserMap | 15.2 ns | 10.3 ns | 10.8 ns | 28.3 ns | 9.1 ns | 2.7 ns |
| ManagedEntityMapPrototype | 18.9 ns | 10.5 ns | 11.4 ns | 31.0 ns | 9.2 ns | 2.6 ns |

Interpretation:

- Dense beats dictionary clearly: at 100k, ProtoMap TryGet hit is 12.4 ns vs CompetentDictionary 59.0 ns; Remove is 13.1 ns vs 106.0 ns.
- `world.IsAlive(entity)` cost is small: at 100k, raw dense TryGet hit is 10.3 ns, competent dense 11.8 ns, proto 12.4 ns.
- ProtoMap does **not** beat competent dense user in hot path: Get/TryGet/Remove are within noise; Set is slower due to Count/null/maxTouched bookkeeping.
- `_maxTouchedExclusive` is useful for cold paths: at 1M, ProtoMap TrimExcess is 3.80 us vs CompetentDenseUser 572.87 us; Clear is 748.77 us vs 1.528 ms. This is an implementation recipe, not enough for a public API by itself.
- 100k Align is acceptable as a cold path: ProtoMap ~2.6 ns per mapped slot, roughly 0.26 ms for 100k slots.
- Hot operations report 0 Gen0/1/2. Dense arrays retain capacity by design; Clear removes references, TrimExcess releases arrays.

## API Complexity Evidence

Competent dense user needs to know:

1. `world.IsAlive(entity)` is the liveness oracle.
2. sidecar `_versions[id]` is only binding state, not liveness.
3. `Remove(entity)` must match version and must not destroy World entity.
4. `Align()` clears zombies and managed references.
5. `Set(null)` must be prohibited or null/absence semantics become ambiguous.
6. placeholder / negative id / out-of-range must fail safely.

This is non-trivial, but the working recipe is still a small dense-array helper. The library would mostly turn this recipe into API surface. Given no hot-path advantage over competent dense user, documentation/recipe is the better tradeoff.

## Serialization Decision

- **v1: exclude serialization.**
- If ever revisited, serialization requires user codec:

```csharp
public interface IManagedEntityMapCodec<T> where T : class
{
    void Write(BinaryWriter writer, T value);
    T Read(BinaryReader reader);
}
```

Minimal snapshot item would be `(entity.Id, entity.Version, payload)`. `WorldSnapshot.Save/Load` preserves slot version/free-list semantics, so direct bind is only safe when sidecar snapshot is saved/loaded with the same World snapshot. Any remap scenario needs an explicit remap table. Godot/Unity values should serialize asset id/path/GUID, not object references.

Serialization does not rescue v1: it adds codec/remap/resource-system questions without proving runtime map value first.

## Recommended Implementation

No production implementation now.

If future evidence reopens this, the least-bad implementation is the prototype shape in `tools/perf/ManagedEntityMap.ValueLab/Program.cs`:

- `T?[] _values`, `int[] _versions`, `int _count`, `int _maxTouchedExclusive`.
- Constructor takes `World` and optional initial capacity.
- `Set` checks non-null and `world.IsAlive`.
- `TryGet/Get` check `world.IsAlive` + version.
- `Remove` returns bool and only removes matching version.
- `Align` scans up to `_maxTouchedExclusive` and clears zombie references.
- `Clear` clears up to `_maxTouchedExclusive`; `TrimExcess` trims to `_maxTouchedExclusive`.

Future minimum API, if ever accepted:

| Member | Keep? | Reason |
|---|---:|---|
| `Set` | yes | primary bind operation |
| `TryGet` | yes | non-throwing hot path |
| `Get` | optional | convenience only; can be omitted for smaller API |
| `Remove` | yes | sidecar-only deletion |
| `Align` | yes | zombie cleanup / GC release |
| `Count` | yes | correctness/debug visibility |
| `Clear` | optional | useful for scene/world teardown |
| `TrimExcess` | optional | cold memory release |

Name if future Go: `ManagedEntityMap<T>` is honest enough; it says managed sidecar, not deterministic component. It must not live in `MiniArch.Core`; if added, keep it as user-layer `MiniArch` API or separate optional package.

## Risks and Non-Goals

- No lockstep determinism guarantee.
- No thread safety.
- No Snapshot/FrameDelta/Checksum/Replay integration.
- No managed ECS component support.
- No value->entity reverse index.
- No built-in reflection serializer.
- No Godot/Unity resource integration.

## Commands Run

Baseline repair and gate:

```bash
dotnet test -c Release --nologo -v q
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

ValueLab:

```bash
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --correctness-only
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 10000 --repetitions 5
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 100000 --repetitions 5
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 1000000 --repetitions 3
```
