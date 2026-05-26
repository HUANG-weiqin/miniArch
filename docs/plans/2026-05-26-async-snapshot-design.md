# SubmitAndSnapshotAsync 设计方案

## 目标

提供 `CommandBuffer.SubmitAndSnapshotAsync()` 方法，在换出 buffer 状态后，**主线程 Submit 与后台线程 BuildDelta 并行执行**，使 Submit 的 CPU 开销与 Snapshot 构建完全重叠。

## 当前问题

帧同步场景下每帧需要同时：
1. `Submit()` — 将录制操作 apply 到本地 world（必须主线程，有副作用）
2. `Snapshot()` — 生成 FrameDelta 给网络同步

两步串行执行时，帧末尾开销 = Submit 耗时 + Snapshot 耗时。

## 核心洞察

**Submit 和 BuildDelta 对 buffer 内部状态都是只读的。** Submit 只读 `_createdStatePool`/`_opsPool`/`_hierarchyByChild`/`_existingDestroys` 然后调用 `_world` 的方法；BuildDelta 同样只读这些状态。

因此：换出后，同一份 frozen state 可以被两个线程同时读——主线程 Submit，后台线程 BuildDelta + DeepCopy。

## 方案

### 执行模型

```
cb.SubmitAndSnapshotAsync()
  ↓ 瞬间换出状态（<100ns）
  ↓ buffer 重置为空，立刻可录制下一帧
  ↓
  ┌─ 主线程（同步）：读 frozen state → Submit 到 world
  └─ 后台线程（Task.Run）：读 frozen state → BuildDelta + DeepCopy → return slabs
  ↓
  返回 Task<FrameDelta>，主线程 await 拿到 delta
```

**关键**：方法返回时 Submit 已经完成（world 已更新），只有 delta 构建在后台跑。用户 await 只是等 delta，不等 Submit。

### 换出的字段

| 字段 | 换出后 buffer 拿到 |
|------|-------------------|
| `_createdStatePool` | `Array.Empty<CreatedState>()` |
| `_createdStatePoolCount` | `0` |
| `_createdEntityByPoolIndex` | `Array.Empty<Entity>()` |
| `_createdStateLookup` | `Array.Empty<int>()` |
| `_maxCreatedEntityId` | `0` |
| `_opsPool` | `Array.Empty<ExistingEntityOps>()` |
| `_opsPoolCount` | `0` |
| `_opsEntityByPoolIndex` | `Array.Empty<Entity>()` |
| `_opsLookup` | `Array.Empty<int>()` |
| `_maxOpsEntityId` | `0` |
| `_existingDestroys` | `new HashSet<Entity>()` |
| `_hierarchyByChild` | `new Dictionary<Entity, HierarchyIntent>()` |
| `_slabs` | `new List<byte[]>()` |
| `_hasCreatedEntities` | `false` |
| `_typeInfoCache` | `new Dictionary<int, ...>()` |
| `_currentSlabIndex` | `-1` |
| `_currentSlabOffset` | `0` |

换出成本：~20 次引用赋值 + 4 个空容器 new。预估 < 100ns。

### 不换出的字段

- `_world` — buffer 始终持有，SubmitFromFrozen 需要
- `_allocator` — buffer 始终持有
- `_tempComponents` — 后台线程自建 local scratch list

### 结果所有权

返回的 `FrameDelta` 与同步 `Snapshot()` 完全一致：`DeepCopyOwnedData()` 保证所有组件数据独立 owned。后台线程完成后 slab 归还 ArrayPool，delta 不引用任何 slab。用户可以无限期持有。

## 设计边界

- **不改** `Submit()`、`Snapshot()`、`Clear()` 的现有行为
- **不改** `FrameDelta` 及其任何结构
- **不引入** 锁、通道、或其他同步机制
- **不池化** 换出后的空容器（每帧 4 个 Gen0 小对象，不值得池化）
- `SubmitAndSnapshotAsync()` 是 `Submit()` + `Snapshot()` 的**合并可选路径**

## 新增类型

### FrozenBufferState（internal）

```csharp
internal sealed class FrozenBufferState
{
    public CreatedState[] CreatedStatePool;
    public int CreatedStatePoolCount;
    public Entity[] CreatedEntityByPoolIndex;
    public int[] CreatedStateLookup;
    public int MaxCreatedEntityId;

    public ExistingEntityOps[] OpsPool;
    public int OpsPoolCount;
    public Entity[] OpsEntityByPoolIndex;
    public int[] OpsLookup;
    public int MaxOpsEntityId;

    public HashSet<Entity> ExistingDestroys;
    public Dictionary<Entity, HierarchyIntent> HierarchyByChild;
    public List<byte[]> Slabs;
    public bool HasCreatedEntities;
    public Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> TypeInfoCache;
}
```

纯数据容器，无行为。

## 新增方法

### CommandBuffer.SubmitAndSnapshotAsync()

```csharp
public Task<FrameDelta> SubmitAndSnapshotAsync()
```

伪代码：

```
1. 检查 buffer 是否为空 → 空则直接返回 Task.FromResult(empty delta)
2. 创建 FrozenBufferState，从 this 换出所有字段引用
3. buffer 重置为空状态
4. 启动 Task.Run: BuildFromFrozen(frozen) → DeepCopyOwnedData → Return slabs → 返回 delta
5. 在调用线程同步执行: SubmitFromFrozen(_world, frozen)
6. 返回步骤 4 的 Task<FrameDelta>
```

注意步骤 4 和 5 **并行执行**：Task.Run 先启动（确保后台线程尽早开始），然后主线程同步 Submit。

### CommandBuffer.SubmitFromFrozen（private static）

```csharp
private static void SubmitFromFrozen(World world, FrozenBufferState frozen)
```

逻辑与现有 `Submit()` 完全相同，只是数据来源从 `this._xxx` 替换为 `frozen._xxx`。不调用 `Clear()`（换出已完成清空）。**必须在主线程调用**（因为操作 world）。

### CommandBuffer.BuildFromFrozen（private static）

```csharp
private static FrameDelta BuildFromFrozen(FrozenBufferState frozen)
```

逻辑从现有 `BuildDelta` + `DeepCopyOwnedData` 提取，`this._xxx` 替换为 `frozen._xxx`。内部自建 `_tempComponents` scratch list。完成后 Return frozen slabs 到 ArrayPool。

## 用法

```csharp
// 录制
cb.Create(); cb.Add<T>(e, comp); ...

// 换出 + Submit 同步 apply + delta 后台构建
var task = cb.SubmitAndSnapshotAsync();

// 此时：world 已更新，buffer 已空，可立刻开始下一帧录制
cb.Create(); cb.Add<T>(e2, comp2); // 下一帧录制

// 需要发网络时 await（大概率已完成）
FrameDelta delta = await task;
SendToNetwork(delta);
```

## SubmitFromFrozen 与现有 Submit 的代码复用

两者逻辑完全一致，区别仅在数据来源。可通过以下方式复用：

- **方案 A**：提取内部方法 `SubmitCore(world, createdStatePool, createdStatePoolCount, ...)`，两个入口都调用它
- **方案 B**：`SubmitFromFrozen` 接受 `FrozenBufferState`，内部直接读字段，与 `Submit` 代码平行（代码略冗余但无间接开销）

推荐方案 A，避免逻辑分叉。

## 风险

| 风险 | 缓解 |
|------|------|
| 后台线程与主线程读同一份 frozen state | 两者都只读，无共享可变状态，安全 |
| `ExistingEntityOps` 内嵌 Overflow 字典 | struct 数组整体换出，Overflow 跟数组走，无共享 |
| slab Return 时机 | 后台线程 DeepCopy 完成后才 Return，delta 已独立；Submit 不读 slab 内容（只读 ops 元数据），不受 Return 影响 |
| 空 buffer 调用 | 提前检查 `DeltaCount == 0`，返回 `Task.FromResult(new FrameDelta())`，不调度后台线程 |
| SubmitFromFrozen 中 hierarchy 检查 created entity destroyed | 需要查 createdStateLookup，frozen state 包含此数据 |

## 验证

- 行为等价：`SubmitAndSnapshotAsync()` 的效果 = `Submit()` + `Snapshot()`
  - world 状态一致
  - delta 内容一致
  - 复用现有 `CommandBufferTests` 中的所有 Submit + Snapshot 测试，增加 async 版本
- 性能：主线程换出开销 < 100ns（微基准验证）
- 正确性：slab Return 后 delta 仍可安全 Replay
- 并行安全：stress test 多帧连续调用
