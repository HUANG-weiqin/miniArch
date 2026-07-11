# CommandStream.CreateMany Design

## 结论

`CommandStream.CreateMany` 必须是 CommandStream 自身 IR 的批量写入路径，而不是 `Action<World>` 延迟回调。第一版只实现 struct-writer API，用现有 pending-batch / `FrozenState` / `FrameDelta` 管线承载数据，先证明完整语义下是否仍有性能价值。

## 目标

- 一次录制 N 个同签名实体，减少 per-entity `Create()+Add<T>...` 的录制开销。
- 完整支持现有 CommandStream 消费路径：
  - `Submit()`
  - `Snapshot()` / `SnapshotInto()`
  - `SubmitAndSnapshotAsync()` / `SubmitAndSnapshotIntoAsync()`
  - `DeferredEntities=true` placeholder 模式
  - `EntitySlot.Track()`
  - `FrameDelta` replay
- 以 benchmark 判断保留价值：如果完整语义下没有明显收益，删除该 API；如果有收益，再考虑 bulk materialize fast path。

## 非目标

- 第一版不做 delegate/lambda 便利 API，避免闭包分配污染性能结论。
- 第一版不做独立 `World.CreateMany`。
- 第一版不改变 `FrameDelta` wire format。
- 第一版不承诺 `ParallelCommandStream.CreateMany` 高扩展性；它可以先与其他 pending-batch mutator 一样串行锁保护。

## API

第一版公开 struct-writer API，组件数支持 1..8：

```csharp
public interface ICreateManyWriter<T1> where T1 : unmanaged
{
    void Write(int index, Entity entity, out T1 component1);
}

public void CreateMany<T1, TWriter>(Span<Entity> entities, TWriter writer)
    where T1 : unmanaged
    where TWriter : struct, ICreateManyWriter<T1>;
```

`T2..T8` 版本同形状：`Write(int index, Entity entity, out T1 c1, ..., out Tn cn)`。

选择 `Span<Entity>` 而不是返回数组：

- 调用者决定存储位置，可用 stackalloc、ArrayPool 或已有数组。
- `entities.Length` 就是 count，避免重复参数。
- record 阶段立即写出 entity handle，调用者可继续 `Track`、`AddChild`、`Destroy` 或建立组件内 Entity 引用。

## 核心运行机制

```text
Record CreateMany:
  for each index:
    entity = CreateCore()                 // real-reserved 或 placeholder
    entities[index] = entity
    writer.Write(index, entity, out components...)
    WritePendingComponent(batchIdx, component1)
    ...

Submit:
  ResolveDeferredCreates()
  PreValidatePendingSlots()
  MaterializeAllPending()
  ApplyHierarchy()
  ApplyComponentStores()
  ApplyDestroys()

Snapshot / Async:
  BuildDelta / BuildFromFrozen read the same FrozenState pending batches
```

关键点：CreateMany 写入的组件就是普通 pending-batch component data。这样 placeholder 解析、FrameDelta emit、async frozen state、Replay 都不需要新语义。

## 数据结构

第一版不新增独立 CreateMany command list。数据直接进入现有字段：

- `FrozenState.BatchEntities[]`
- `FrozenState.BatchHeads[]`
- `FrozenState.BatchComps[]`
- `FrozenState.BatchBuf`
- real-id 模式下的 `PendingBatch[]`
- deferred 模式下的 `_pendingBatchDeferredArr[]`

也就是说，CreateMany 是“批量调用 CreateCore + WritePendingComponent 的 inline 快路径”。这保守但完整。

## 为什么第一版不做 bulk materialize

真正的 bulk materialize 可以进一步减少 Submit 开销：一次 archetype resolve、一次 `AllocateRows(N)`、列连续写入。但它会引入第二套 pending-create 表示，并且必须同时覆盖 cancelled batch、placeholder resolve、FrameDelta emit、async frozen swap、chunked fallback。

第一版先复用现有 pending-batch 管线，原因：

- 正确性风险最低。
- TDD 最直接。
- 若 record 阶段才是瓶颈，已经能测到收益。
- 若收益不够，再基于真实 benchmark 决定是否值得引入 bulk materialize。

## 正确性要求

- `CreateMany` 后、`Submit` 前，world 不应看到实体 alive。
- `Submit` 后所有未取消实体 alive，组件值正确。
- `Snapshot` 不修改 source world，Replay 后 replica 收敛。
- `DeferredEntities=true` 时返回 placeholder，`Submit` / `Replay` 后组件内 Entity 字段被解析。
- `SubmitAndSnapshotAsync` 与 `SubmitAndSnapshotIntoAsync` 产出的 delta 包含 CreateMany 创建的实体。
- `Destroy` CreateMany 中的某个 pending entity 必须按现有 pending cancel 语义释放或跳过。
- Public API baseline 必须显式更新。

## 性能判断

新增 benchmark 比较：

1. per-entity `Create()+Add<T>...+Submit`
2. `CreateMany<T1..Tn,TWriter>+Submit`
3. `CreateMany<T1..Tn,TWriter>+SubmitAndSnapshotAsync`

测量必须使用 `-c Release`。判断标准：

- 若完整语义版本对 create-heavy record+submit 没有稳定提升，删除 API。
- 若有稳定提升但 Submit 仍明显占比高，再设计第二阶段 bulk materialize fast path。

## 风险

- `Span<Entity>` public API 会改变 API sentinel，需要人工接受 baseline 更新。
- `TWriter` 按值传入；如果 writer 内部有计数状态，外部不会看到 writeback。第一版约定 writer 是纯输入生成器。
- `ParallelCommandStream` 第一版串行锁实现，主要保证正确性，不作为并行扩展性结论。
