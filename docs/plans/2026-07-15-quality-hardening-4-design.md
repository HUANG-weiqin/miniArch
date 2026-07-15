# MiniArch 4.0 Quality Hardening Design

## 结论

本轮采用“边界硬化 + 两阶段预检 + 局部 API 校准 + 实测性能优化”的路线，不引入 World shadow、通用事务日志或回滚框架。目标不是通过平均分掩盖短板，而是让正确性、内存安全、性能、确定性、并发、API、可维护性、测试与文档各自都具备 8 分以上的当前证据。

本轮允许破坏性 API 调整，版本按 4.0.0 处理；但破坏范围必须最小。具有真实热路径价值、且风险可以由显式契约界定的高速 API 继续保留。

## 已确认的问题

1. 单字节 archetype 的 segment capacity 计算在 `RoundUpToPowerOf2` 后转为 `int` 时溢出。默认 `chunkCapacity=128` 下，第 129 个单 `byte` 实体创建会抛 `OverflowException`，并留下半更新状态。
2. `ChunkView.GetComponentSpanAt<T>(int)` 允许调用者错配 `T` 与 column index，Release 下可以静默跨行覆写组件数据。
3. `CommandStream.Submit()` 在 pending create materialize 后才执行可能失败的 strict Add/Set/Remove 与 hierarchy 操作，正常公共路径可产生部分提交。
4. 两个 async submit API 在同步 Apply 前启动后台任务，但 Apply 失败时调用者拿不到 Task；后台仍可改写目标 `FrameDelta`。
5. Debug structural-change 计数在部分入口没有 `try/finally`，异常会污染后续诊断。
6. `CommandStreamCore` 体积过大，异常边界、pending、hierarchy、component store 和 async 状态交接难以局部推理。
7. 当前知识库存在绝对化安全结论、历史 API、过期契约和相互矛盾的描述。
8. 当前机器上 `HeroComing.Perf --check-baseline` 的 Attack 场景连续两次未达到 997 rounds/s，不能将当前性能门禁描述为通过。

## 设计原则

- **用户错误先验证，后修改状态**：所有正常可达的契约错误必须在首次 alive-world mutation 前被发现。
- **局部 commit-last**：涉及多字段切换的存储迁移先在局部构造完整结果，再一次性发布。
- **不伪造通用事务**：不承诺 OOM、CLR 灾难或已损坏内部 invariant 的回滚。
- **高速 unsafe 能力可保留**：前提是名字或 XML 明确边界、存在真实性能用途、Debug 能尽早发现误用。
- **不为评分造概念**：不新增 World shadow、transaction object、typed column token 或兼容 shim。
- **性能结论只认 Release 端到端证据**：stage-local 或微基准收益不足以保留改动。

## 存储扩容：局部构造后发布

### Segment capacity

把 segment capacity 计算集中为一个纯 helper：

- 全程使用 `uint`/`ulong` 或 checked 算术；
- 在转为 `int` 前 clamp 到 `MaxSegCap`；
- 明确覆盖 `perEntityBytes=1`、2、极大组件和 array-length 上限；
- 结果始终为正、为 2 的幂、且不超过 `MaxSegCap`。

### Flat → chunked promotion

`ConvertToChunked` 不再提前写 `_segments`、`_segmentCount` 等实例字段：

1. 计算局部 offsets 与 segment count；
2. 在局部 `Segment[]` 中完成全部实体与组件数据复制；
3. 全部成功后，一次性替换 `_segments`、`_segmentCount`、`_columnByteOffsets`；
4. 最后释放 flat backing 引用。

这样普通算术错误或可恢复的分配异常不会暴露半转换 archetype。

### Entity ID 与容量顺序

直接 `World.Create` 在取得/修改 entity allocator 状态前先确保目标 archetype 有足够容量。容量准备失败时，records、free-list、EntityCount 和 hierarchy 均不变化。OOM 不属于可恢复事务保证，但不应因普通边界算术错误留下逻辑损坏。

## 高速 unsafe API 政策

### 局部改名

只把最容易造成类型混淆的入口改名：

```csharp
GetComponentSpanAt<T>(int columnIndex)
    -> UnsafeGetComponentSpanAt<T>(int columnIndex)
```

其语义保持零额外 Release 检查。Debug 构建增加：

- column index 范围断言；
- column 的真实 `ComponentType` 与 `T` 一致断言；
- ChunkView 已初始化断言。

XML 必须明确：column index 必须来自同一个 ChunkView/archetype 上针对同一个 `T` 的 `TryGetComponentIndex<T>`；错配、跨 structural change 保留或跨 archetype 复用属于未定义行为。

### 保留的高速 API

以下能力不删除、不统一加 runtime guard：

- `TryGetComponentIndex<T>`
- `World.GetRef<T>`
- `ChunkView.GetSpan<T>`
- `EntityAccessor.Get<T>/Set<T>`
- `RowRef.Component<T>`
- `FrameDelta.AsSpan()`
- `World.Clear(in QueryDescription)` 的高速非级联语义

这些 API 的借用期、结构变更失效、hierarchy 边界等前置条件通过 XML 契约与 Debug 诊断表达。`Clear(query)` 的安全替代仍是 `Destroy(query)`；本轮修正文档中的错误陈述，但不把高速路径强制退化为逐实体安全路径。

### 内化项

`Entity.IsUnmappedSentinel` 改为 internal。它仅描述 Replay placeholder map 的内部哨兵，不是用户领域概念，也没有外部性能用途。

`CommandStreamCore` 保持 public abstract：它是 `CommandStream` 与 `ParallelCommandStream` 的公共消费抽象，允许调用方共享 Submit/Snapshot/Replay 代码；其 `private protected` 构造器已经阻止外部派生。

## CommandStream 两阶段执行

统一消费顺序：

```text
PrepareStores
→ PreflightValidate
→ Resolve/validate reservations
→ Apply: Create → Hierarchy → ComponentOps → Destroy
→ Clear/reclaim
```

### Component preflight

每个 `ComponentStore<T>` 增加只读验证能力，按真实录制顺序模拟该组件在实体上的存在状态：

- Add 要求当前不存在；
- Set 要求当前存在；
- Remove 要求当前存在；
- 同一实体同一组件的多条命令按顺序更新虚拟存在位；
- stale/已销毁 existing entity 继续遵循当前 `PruneStaleCommands` 的静默丢弃契约；
- pending entity 的组件仍由 pending-batch folding 处理，不重复进入 store preflight。

实现使用 CommandStream 级、按 entity id 索引的 lazy scratch 数组与 generation 标记。每个 store 复用同一组 scratch，稳态零分配，不建立 per-type Dictionary。

Set-only store 保留独立快速验证循环，避免把 mixed-kind 分支带回 Attack 热路径。

### Hierarchy preflight

对 `HierarchyByChild` 的最终 intent overlay 按与 Apply 相同的 child-id 顺序验证：

- 提交后 parent/child 必须存在；
- parent != child；
- 本帧多个 intent 联合后不得形成环；
- 被本帧 Destroy 的关系按当前语义跳过；
- pending placeholder 在 preflight 中视为“提交后存在”，不要求提前 materialize。

验证只读取 World 与 frozen recording state，不写 hierarchy table。

### 原子性保证

对 strict component、hierarchy、placeholder、reserved slot 等正常用户契约错误：

- 在首次 alive-world mutation 前抛出；
- pending entity 不 materialize；
- stream 持有的 reservation 被释放；
- source World 的存活实体、组件与 hierarchy 状态不变；
- `SnapshotInto`/async target 不被后台继续改写；
- 不遗失后台 Task。

立即模式 `Create()` 在录制时已经 reserve ID，因此失败后的 allocator 内部顺序只承诺按现有 `Clear(releaseReserved:true)` 契约释放，不承诺与“从未录制”字节级相同。

## Async FrozenState 所有权

async API 在 active state 上完成 Preflight 后才允许 SwapOut/启动 worker。worker 启动后立即登记 `_pendingFrozen` 与 `_pendingTask`，不能等同步 Apply 成功后才登记。

正常用户错误不会启动 worker。若 Apply 仍因内部 invariant 或运行时异常失败：

1. 观察并等待已启动 worker；
2. 对 `SnapshotInto` 目标执行明确的失败清理；
3. 保留 FrozenState 所有权直到 worker 完成；
4. 重新抛出原始 Apply 异常；
5. 不宣称 World 可从灾难性 Apply 异常回滚。

## Debug structural-change 诊断

所有 direct structural entry point 的 Begin/End 必须异常安全。实现要保证 Release IL 不增加保护分支：Debug-only scope 或 `#if DEBUG` 包围的 `try/finally` 均可，最终以 Release IL/性能门禁决定。

批量 Destroy、Destroy(query)、Clear(query) 与 CommandStream internal apply 的诊断覆盖范围要在代码与 XML 中一致，不能把部分 guard 描述成全局并发/迭代安全保证。

## 可维护性重构

在行为修复全部 green 后，将 `CommandStreamCore` 按同一 partial class 拆分：

- `CommandStreamCore.Submit.cs`：Submit、Preflight、async、FrozenState handoff；
- `CommandStreamCore.ComponentStore.cs`：ComponentStore、typed store、preflight scratch；
- `CommandStreamCore.Pending.cs`：pending batch、placeholder resolve、materialize；
- `CommandStreamCore.Hierarchy.cs`：virtual hierarchy、intent emit/apply/validate；
- 原 `CommandStreamCore.cs` 保留核心字段、构造、公共消费 API 与通用 helper。

同类 partial 迁移不改变类型、可见性或运行时语义。迁移必须机械化、每步全量测试，不夹带行为修改。

## 性能策略

正确性修复优先，但任何 `src/MiniArch` 改动都必须通过 Release 架构门禁。

1. 在修改前记录 `HeroComing.Perf` 与 `CommandStream.Profile` 的当前 checkout 数据。
2. 分离 record、preflight、submit、snapshot、clear 占比。
3. 对 Set-only、mixed Add/Remove、create-heavy、async 分别测量。
4. 只保留端到端稳定收益；stage-local 改善但 HeroComing 无保留收益的候选回退。
5. 不运行 `--update-baseline`。
6. 最终 HeroComing Movement/Attack 均达到项目阈值且内存稳定；若机器噪声接近阈值，连续运行验证，不用单次幸运结果闭环。

当前 Attack 基线在本机为红，因此本轮需要先用 `tools/perf/CommandStream.Profile` 定位，再做有证据的优化，不把既有红灯归因于尚未实施的修复。

## TDD 与验证矩阵

### 必须先红后绿的回归测试

- 单 `byte`、`bool`、单字节 tag 在 `chunkCapacity + 1` 创建成功；
- segment capacity helper 的 1-byte、2-byte、巨大组件和上限边界；
- flat → chunked 后实体、组件与 validator 一致；
- unsafe span API 的 Debug 类型/列配对诊断及 XML/API baseline；
- Submit 中 pending Create + invalid Add/Set/Remove 在 materialize 前失败；
- 同一 entity/type 多命令的虚拟存在状态与 Apply 一致；
- 多 hierarchy intent 联合形成环时零部分提交；
- async preflight 失败不启动/遗失 worker，不修改目标 delta；
- unexpected async apply failure 的 task ownership/reclaim 路径；
- Debug structural scope 在异常后恢复计数；
- `IsUnmappedSentinel` 不再属于 public API。

### 完成门禁

```powershell
dotnet build -c Release --no-restore miniArch.sln
dotnet test -c Release --no-build miniArch.sln
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 10000 --hosts 4
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

CommandStream/async 变更还必须运行相应 focused tests 与 `CommandStream.Profile`。所有性能命令使用 `-c Release`。

## 文档与知识库

更新而不是继续叠加历史描述：

- `.knowledge/kb-code-review-findings.md`：记录新 bug witness、回归测试与真实修复边界；推翻“Submit 无事务风险已完全修复”的旧条目；
- `.knowledge/kb-safety-proof.md`：从绝对“发布级正确性证明”改为带版本、范围、未覆盖项和当前门禁状态的验证报告；
- `.knowledge/kb-command-stream.md`：删除 `_parallelMode`、`ParallelRecording` 等旧路径描述，写入 Preflight/async ownership；
- `.knowledge/kb-snapshot-persistence.md`：移除“无校验和”等旧说法；
- `.knowledge/kb-design-rationale.md`、`.knowledge/kb-core-ecs.md`：移除旧 Track/ModifiedChunks 和错误示例；
- `docs/api.md`、XML docs、public API baseline：同步 4.0 破坏性变化；
- `.knowledge/INDEX.md`：只有关键词或主题路由变化时才调整，不复制正文。

所有知识页遵守 `_template.md` front matter，更新 `updated`，结论先行、单一事实来源。

## 非目标

- 不实现通用 transaction/rollback journal；
- 不为 unsafe API 增加 Release runtime guard；
- 不把 `Clear(query)` 改成 `Destroy(query)` 的别名；
- 不更新性能 baseline；
- 不新增兼容 shim 或保留旧 API 别名；
- 不进行没有基准保留条件的微优化；
- 不借本轮引入新的 ECS 功能。

## 完成定义

本轮只有在以下条件全部满足时才能宣称“各维度达到 8+”：

1. 新增 bug witness 均经历 RED → GREEN；
2. 全量 Release build/test、单 host soak、determinism soak、multi-host lockstep soak 通过；
3. HeroComing Movement/Attack 门禁与内存门禁通过；
4. async worker ownership、preflight 原子性和 unsafe API 边界有直接测试/文档证据；
5. public API baseline 只包含已确认的局部破坏；
6. 相关知识页不再含已知错误、重复或绝对化结论；
7. 工作树干净，最终证据与当前 commit 对应。
