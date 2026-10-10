---
title: Snapshot Persistence
module: MiniArch.Core Snapshot
description: WorldSnapshot v5 字段级 canonical 持久化（canonical 只含非空原型）、双 checksum 观察边界，以及 Clone/CaptureState 的职责分工
updated: 2026-10-10
---
# Snapshot Persistence

## 这个模块是干什么的

- `WorldSnapshot.Save/Load`：把完整 `World` 状态编码成可跨进程传输的 v5 字节流，并严格重建。
- `World.Clone()`：创建独立的内存副本，不经过持久化格式。
- `World.CaptureState/RestoreState`：为高频原地回滚保存绑定源 World 的 opaque lease。
- `Checksum()` / `CanonicalChecksum()`：分别观察回滚/lockstep 投影和完整 v5 持久化状态；两者都忽略空原型。

## 架构

核心入口：

- `src/MiniArch/Core/WorldSnapshot.cs`：v5 writer/loader、字段计划、两个 checksum。
- `src/MiniArch/Core/WorldClone.cs`：内存克隆。
- `src/MiniArch/Core/World.cs`：slot version、location、free-list 与 reserved-count 重建桥接。
- `src/MiniArch/Core/Archetype.Storage.cs`：提供单个 component cell 的只读/可写 byte span；持久化层不会整列 raw copy。

### 三套状态复制机制

| 机制 | 产物 | 跨进程 | 主要用途 |
|---|---|---|---|
| `WorldSnapshot.Save/Load` | v5 canonical 字节流 / 新 `World` | ✅ | 存档、状态同步、replay checkpoint |
| `World.Clone()` | 新的独立 `World` | ❌ | 分支模拟、长生命周期内存副本 |
| `CaptureState/RestoreState` | 绑定源 World 与 generation 的 value lease | ❌ | 高频原地 rollback |

三者的状态边界不同，不能互换：`Clone` 每次创建新 World；`WorldStateSnapshot` 包含内部数组并绑定来源；只有 `WorldSnapshot` 是持久化协议。

## v5 canonical 持久化

### 唯一字节域

定义 `P(World)` 为 `WriteCanonicalWorld(world, sink)` 输出：

```text
P(World) = magic:LE32 + version(5):LE32 + canonical payload
Save     = P(World) + CRC32(P(World)):LE32
CanonicalChecksum = SHA256(P(World))
```

`Save` 与 `ComputeCanonicalChecksum` 调用同一 traversal，只替换 sink：前者边写 stream 边累计 CRC，后者增量喂给 SHA-256。两者都不物化完整 snapshot payload。

v5 payload 依次包含：

1. chunk capacity、slot count、schema count、archetype count、关系计数（固定 0）；
2. 每个 slot 的 version；
3. schema table：**非空 archetype 实际用到的**组件类型，含精确 identity 与 canonical schema shape；
4. **非空** archetype 的 signature、升序 entity id 与字段级 component values；
5. 无关系条目（布局保留，计数恒为 0）；
6. 保持实际 stack 顺序的 free-list entity ids。

free-list 不重复保存 version；它由 slot version table 唯一推导。runtime `_freeIds` 与 `WorldStateSnapshotPayload.FreeIds` 也都是有序 `int[]`，不再有 `RecycledEntity`/`FreeEntityEntry` 的冗余 version；数组元素由 8B 降为 4B，但不能据此声称整个 World 内存减半。顺序决定下一次分配，不能仅由 records 重建。v5 本来就保存全部 slot versions 与有序 free ids，wire 格式不变。reservation 也不单独保存，Load 在 live/free 状态就位后由 `slotCount - liveCount - freeCount` 推导。

空 archetype 不进 payload，也不贡献 schema。因此“曾创建后销毁某种组合”或“回退后残留空原型”不改变 canonical 内容。v5 字段布局保持不变：旧关系计数为 0 的文件仍可 Load，其余 wire 兼容；关系计数非零的文件在修改/构造 World 前明确拒绝，不能只跳过关系条目。需要保留旧关系先由旧版导出（`kb-hierarchy-runtime.md`）。旧 v5 payload 中的空原型条目仍能 Load，但重新 Save 会被规范化删除：旧存档单向可读，原始字节不保留。

### 与注册顺序无关的顺序

canonical traversal 不使用运行时 `ComponentType.Value`：

- schema：按精确 `Type.AssemblyQualifiedName` 做 ordinal 排序；identity 必须非空、唯一，UTF-8 最长 16 KiB；
- archetype：先把 component 映射为 schema index，再按 index signature 做 lexicographic 排序；
- archetype 内 row：按 `Entity.Id` 严格升序；对应 column values 使用同一 row permutation；
- free list：不排序，保留 allocator stack 顺序，因为它决定下一次 id 分配。

因此，相同类型和完整持久化状态在不同进程、不同 component 注册顺序下产生相同 bytes/checksum。精确 assembly-qualified identity 仍意味着程序集名或版本发生变化属于 schema 变化，不承诺跨版本兼容。

### 字段级 canonical component plan

`WorldSnapshot` 以 `Type` 为 key，通过线程安全 cache 只构建一次 immutable `ComponentPlan`。首次构建使用反射发现 shape，并通过 `DynamicMethod` + `ldflda` 取得当前 CLR 中真正的 managed field offset；稳态写/读仅执行缓存的 leaf operations，不做 per-component 反射、boxing 或分配。

持久化的逻辑字段集合与顺序：

- 所有 non-static instance fields，包含 private fields 与 auto-property backing fields；
- struct fields 按 field name 做 ordinal 排序，declaration order 不进入协议；
- nested unmanaged structs 递归展开；
- fixed buffer 与 `InlineArrayAttribute` 按声明长度展开元素；
- schema shape 记录 field name、declared/nested nominal type、primitive kind 与 fixed/inline length。

wire 规则：

- `bool` 固定 1 byte；Save 将任意非零 CLR 表示规范化为 `1`，Load 只接受 `0` 或 `1`；
- `sbyte/byte` 1 byte，`short/ushort/char` 2 bytes，`int/uint/float` 4 bytes，`long/ulong/double` 8 bytes；
- 所有多字节 primitive 都是 little-endian；
- enum 按 underlying primitive 编码；
- `float` / `double` 保留 IEEE bit pattern，不做数值归一化。

CLR alignment、实际 offset、pack、field declaration order、inter-field/tail padding 和 `Unsafe.SizeOf<T>()` 都不写入 wire/schema。offset 只用于在当前进程找到逻辑 leaf。因此两个 component 的逻辑字段相同但 padding bytes 不同，仍产生相同 snapshot bytes 和 checksum。

以下 shape 无 canonical 表示；若出现在**非空**原型中，会在 `Save` 写第一个 byte 前以 `NotSupportedException` 拒绝，Load 对应包装为 `InvalidDataException`。只属于空原型的组件类型不写入 schema，也不参与 shape 校验：

- pointer、function pointer、`IntPtr`、`UIntPtr`；
- open/by-ref/by-ref-like、含 managed reference 或其他非 closed-unmanaged shape；
- `LayoutKind.Auto`；
- 任意实际 field range overlap（包括 overlapping explicit layout）；
- recursive value shape；
- 非法 fixed buffer / inline array layout 或长度。

不存在 raw-memory fallback。

### Schema shape 是兼容门禁

每个 schema row 保存：

```text
identityLength + exact AssemblyQualifiedName UTF-8
shapeLength    + canonical schema-shape bytes
```

Load 解析 identity 后为本地 `Type` 构建同一个 plan，并要求 identity 与 shape byte-equal。字段增删、字段名或类型变化、nested nominal shape 变化、fixed/inline 长度变化都会拒绝。单纯 padding、offset、pack 或 declaration order 变化不会改变 shape。

### 严格 Load 与往返不变量

`Load` 只接受 version 5；v3、v4 和未知版本都抛 `InvalidDataException`。处理顺序：

1. 把输入复制到单个 `MemoryStream` backing buffer；
2. 验证最小长度、magic、version 和覆盖整个 `P(World)` 的 CRC32；
3. 对 payload 做完整 dry validation；
4. 只有全部合法后才注册 runtime component ids、创建 World 并反向执行同一 component plan；
5. 构造阶段异常时 dispose 部分 World，不发布半构造结果。

dry validation 覆盖有界 header counts、严格 UTF-8/identity、schema 顺序与重复、shape、archetype signature 顺序与重复、schema usage、row/id 重复和范围、component payload 长度与 bool wire、**非零关系计数明确拒绝**、free-list count/id/重复/live overlap，以及 trailing payload。

核心不变量：

```text
Save(Load(Save(world))) == Save(world)   // byte-for-byte
```

该不变量针对**当前版本自己写出的**快照（payload 已是 canonical）。对旧 v5 payload（带空原型条目），`Load` 成功但 `Save(Load(旧))` 会规范化掉空原型，因此只有幂等式 `Save(Load(Save(Load(旧)))) == Save(Load(旧))` 成立，原字节不保留。

CRC 只检测传输/存储损坏，不提供 schema migration 或兼容能力。Save 对 stream I/O 失败不承诺事务性回滚；调用方若需要原子文件替换，应先写临时文件再 rename。

## Checksum 双模式

| API | 观察边界 | 适用场景 |
|---|---|---|
| `world.Checksum()` | slot versions、非空 archetypes、按物理 row 保持 entity-to-component 关联的字段值；排除 chunk capacity、空 archetype、free list | 同 mutation/replay 路径的 rollback parity；保留物理 row 投影 |
| `world.CanonicalChecksum()` | 精确 `SHA256(P(World))`：chunk capacity、全部 slot versions、非空 archetype 及其 schema、固定为 0 的关系计数、有序 free list | 判断完整持久化状态是否一致；跨进程/注册顺序比较 |

`Checksum()` 保留 path-sensitive 行为：archetype 内 entity id 与 component values 都按同一物理 row 顺序写入，因此不会丢失“值属于哪个实体”的关联。逻辑值相同而 storage history 不同的 worlds 仍可能得到不同结果。它现在也使用字段计划，不再 hash raw component memory。

`CanonicalChecksum()` 仍包含 chunk capacity、所有 slot versions、固定为 0 的关系计数与有序 free list，所以它不是“忽略存储形状的业务逻辑等价”hash；但它**不观察空 archetype**：只收录 `EntityCount > 0` 的原型及其用到的 schema。于是 `RestoreState`（或先创建后销毁某种组合）留下的空原型不会改变 canonical 结果，回退后的 World 与从未回退过的 World 在 canonical 口径下内容一致，rollback parity 对两个 checksum 都成立。两者仍不可互换：`Checksum()` 保留物理 row 投影并排除 free list。

两个 checksum 都使用 SHA-256；它们不能替代版本/schema 协商，也不保证应用层 float 运算跨平台确定。

## WorldStateSnapshot 生命周期

- `World._stateSnapshotPool` 只池化大数组 payload；公开 `WorldStateSnapshot` 是 readonly value lease，保存 payload 引用与 capture generation。
- `CaptureState()` 从池取 payload 或冷启动创建，递增 generation 并填充数据。
- `RestoreState(lease)` 校验 payload 存活、generation 与 source World；恢复后清理并归还池。
- `lease.Dispose()` 丢弃 checkpoint 而不恢复；default、已消费或 stale lease 是幂等 no-op。
- value lease 必须携带 generation：若复用公开 class handle，旧引用会与新引用别名同一对象，无法阻止 ABA。
- 池化 backing arrays 必须保存 capture 时的逻辑长度；Restore 不能把较大 capacity 当作有效数据。
- `IsRecycled` 对 default、已 Restore/Dispose、stale generation 或来源 World 已 Dispose 的 lease 返回 `true`。

- 支持多帧 rollback window：覆盖 ring slot 前先 `Dispose()` 未消费 lease；一轮 warm-up 后相同深度的 capture/restore/dispose 可复用 payload。
- `RestoreState` 先将各原型实体数清零、再恢复快照中的活原型；原型对象仍留在 `_archetypes` 中，所以创建实体用的原型缓存保持有效：预热后“拍快照 → 用多种组合 Create/Destroy → 退回 → 再 Create”的稳态分配为 0 字节。`World.Reset` 走整体清空路径（会移除原型），仍照常作废该缓存。

```csharp
var ring = new WorldStateSnapshot[8];
for (var i = 0; i < ring.Length; i++)
{
    ring[i] = world.CaptureState();
    Simulate();
}

world.RestoreState(ring[3]);
ring[4].Dispose();
```

`WorldStateSnapshot` 不包含 `CommandStream` pending batch、typed stores、placeholder sequence 或 async frozen state。

### 公开合同：Capture 的 reservation 边界与 Restore 的命令时间线

以 `docs/api.md` 的 [Rollback contract](../docs/api.md#contract-commandstream-frames-across-capturestate-and-restorestate) 为调用方合同。`CaptureState()` 只在 World 仍有 real-id reservation（`_reservedCount != 0`）时抛 `InvalidOperationException`；它在租借 pooled payload 之前拒绝，不回收已有 snapshot。纯 Set、deferred-only、已取消且不占用 reservation 的 draft 可以捕获，但 `WorldStateSnapshot` 不保存它们。

成功的 `RestoreState()` 推进 World 的命令时间线，不把该时间线写入 snapshot/wire。已有录制帧若带旧时间线，`Validate`、`Submit`、`Snapshot`、`SnapshotInto`、两条 async consume 入口，以及继续预留/Clone/取消 pending entity 会拒绝；普通 existing-entity Add/Set/Remove 仍可 append，但不能使旧帧可消费，即使目标实体仍存活也不能重放旧 intent。空 stream 可继续使用；失败或空操作不会把一个空 stream 绑定到旧时间线。`Clear()` 检测旧帧后只丢弃本地 buffers，不再向已恢复 World 释放旧 reservation，因此同一 stream 可清理后重新录制，而不必重新构造。`Snapshot` → `Clear` → own `Replay(delta, resolveSlots: true)` 可在历史兼容时解析本 stream 追踪的 slots。

异步 API 在返回 task 前同步 apply；编码历史 delta 的 worker 可跨 `RestoreState()` 完成。一个 `_pendingTask` 字段不等于全部在途 worker。显式 Replay 历史 delta 仍允许，是否与当前 World allocator/历史兼容由调用方保证；跨 Restore 保留的 `Entity`、ref、span、`EntitySlot.Value` 不保证安全。

旧版 2026-09-27 的外部 probe 曾复现旧 stream 静默重放与 stale `Clear()` 重复释放；那是添加时间线/安全清理之前的历史缺陷，不是当前行为。World 侧仍能恢复 `Checksum()` / `CanonicalChecksum()` / alive 集 / free-list 顺序；restore 留下的空 archetype 不进 canonical 内容。

## 决策

- 持久化的 owner 是逻辑字段，不是 `Chunk._data`；raw bytes 同时携带字段值与 CLR padding，不能作为稳定协议。
- stable identity 和 schema shape 都是协议的一部分；runtime component id 仅用于本进程 storage。
- Save、Load、canonical checksum 共用一个 component plan；Save 与 canonical checksum再共用一个 world traversal，避免两个“canonical”定义漂移。
- 收录 allocator 状态（全部 slot versions、有序 free list）与 chunk capacity，因为目标是精确持久化/重存字节稳定，而不是只表示活实体；runtime free list 只存 id，`RepushFreeEntry(expectedVersion)` 以 record 核代际后保序移动，stale 取消不得影响新代 id。但不收录空 archetype：空原型不承载活数据，它的存在只反映创建/回退历史，写进协议会让内容相同的世界产生不同存档字节。
- v5 是破坏性替换，不保留 v3/v4 reader：错误地“兼容”旧 raw 格式会重新引入 padding 与注册顺序依赖。
- `ComputeChecksum` 公共签名保留，但只保留原有观察投影；其 component bytes 也必须 canonical。

## 认知模型

- `WorldSnapshot v5` 是“**逻辑字段协议 + 完整 World 状态机快照**”，不是 runtime chunk 内存镜像。
- `ComponentPlan` 是 Type 到 `(schema shape, ordered primitive leaves)` 的不可变编译结果。
- CLR field offset 只回答“当前进程去哪里取/写值”；wire order 由 schema 决定，两者不能混为一谈。
- `CanonicalChecksum` 是 Save payload 的另一种 sink，不是独立实现的近似 hash。

## 入口

- `src/MiniArch/Core/WorldSnapshot.cs`
- `src/MiniArch/Core/Archetype.Storage.cs`
- `src/MiniArch/Core/World.cs`
- `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`
- `tests/MiniArch.Tests/Core/HardeningEdgeCaseTests.cs`
- `tests/MiniArch.Tests/CrossFeatureParityTests.cs`
- `tests/MiniArch.Tests/Core/SubmitReplayRestoreParityTests.cs`
- `tests/MiniArch.Tests/Core/RestoreCreateArchetypeCacheTests.cs`

## 坑点

- 不要通过清零 padding 来“修复”持久化；padding 根本不属于协议。
- 不要在 snapshot schema/order 中写 `ComponentType.Value`；它由进程内注册顺序分配。
- 不要根据“World 里是否存在空原型”推断 `CanonicalChecksum` 会变——canonical 忽略它们；也不要把 canonical 当成“忽略存储形状的 hash”，chunk capacity、slot table 与 free-list 顺序仍严格观察。
- 不要通过 `Add/Set/Remove` 回放 Load；必须直接重建 slot、archetype row 与 allocator 状态。
- schema identity 变化会使旧文件不可读；v5 没有 migration registry。需要迁移时由应用在 MiniArch 边界外显式转换。
- `struct` 不等于可持久化：managed references、native pointers、overlap 和 AutoLayout 都会被拒绝。
- Load 是不可信输入边界；任何新字段都要同时加入 dry validation、construction 和 Save→Load→Save 回归。
- `RestoreState` 只恢复 World，不恢复任何 `CommandStream` buffers；旧帧由命令时间线拒绝，必须 `Clear()` 丢弃后才能复用 stream（合同见 `docs/api.md` 的 Rollback contract 段）。
- 不要把跨 Restore 的旧 `Entity`、ref、span 或 `EntitySlot.Value` 当作已验证身份；历史 delta 的显式 Replay 也需要相容的 World 历史。
- 无层级的旧 v5 存档（关系计数为 0）仍可 Load；非零关系计数必须在构造 World 前拒绝，不得静默丢弃。若要保留旧关系，先用支持层级的旧版导出并显式迁移，见 `kb-hierarchy-runtime.md`。
- 旧 v5 存档中的空原型条目仍可 Load，但重新 Save 会被规范化删除：单向可读，原始字节不保留。需要“旧字节仍原样重存”的链路必须两端都用同一版本。
