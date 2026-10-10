---
title: 已移除的内置实体层级与迁移
module: MiniArch.Core
description: 扁平实体的删除边界、旧层级数据兼容性与调用方迁移入口
updated: 2026-10-11
---
# 已移除的内置实体层级与迁移

## 这个模块是干什么的

- **结论**：MiniArch 不再管理实体间的父子关系。`World` 的实体是扁平的；`Destroy(Entity)` 只销毁指定实体，`Clone(Entity)` 只复制指定实体的组件。批量/Query 销毁仅作用于明确选中的实体。
- 本页记录已移除能力及迁移边界；不定义通用 `Parent` 组件或新的树运行时。
- `World.Clone()` 仍复制整个 World；组件中的普通 `Entity` 引用、嵌套引用及 deferred placeholder 解析仍受原有合同支持，不因层级删除而消失。

## 架构

- 已移除 `World` / `CommandStream` 的 `AddChild`、`RemoveChild`、父/子/祖先查询与遍历、关系表、级联销毁、子树克隆及层级诊断域。原 `ChildrenEnumerable` 和内部 hierarchy 相关实现不再是当前入口。
- 现有 `WorldSnapshot` **仍写 v5 布局**，其中关系计数恒为 0；无层级旧 v5 文件其余 wire 保持兼容。旧文件的非零关系计数必须明确拒绝，不能静默丢关系。`FrameDelta` 中非层级 opcode 的数值与格式不变；旧 `0x04`/`0x05`（AddChild/RemoveChild）必须明确拒绝，不能重编号或当作 no-op。拒绝应在可避免的 World 修改之前发生。
- 同步/异步 CommandStream、Snapshot/Replay、rollback、Query、普通 Entity 字段及其 placeholder/remap 是独立能力；层级移除不改变其用途。各自细节见 `kb-command-stream.md`、`kb-snapshot-persistence.md`、`kb-core-ecs.md`。

## 决策

- 不把过去的 runtime 关系表暗换成内置 `Parent` 组件：这会重新引入库级所有权、遍历、级联与序列化语义。
- **需要保留旧父子关系时**，先用含层级支持的旧版程序读取并显式导出/迁移关系数据，再由应用定义其业务表示和销毁规则；不要直接用新版 Load 期待关系自动转换，更不要把拒绝改为静默跳过。仅有普通 Entity 引用不等于有所有权关系。
- 新调用方只需要业务身份时，使用现有业务键或普通 `Entity` 字段并在应用层处理其生命周期；不必为每个旧调用点重建完整树系统。
- 当前调用方实例：`tests/HeroPipeline.Tests` 以普通 `ActionOwner(Entity Value)` 组件连接 action/card 与角色；`ActionOwnerTests` 检查两个角色各自的 action/card 归属及攻击链，`HeroComing.Perf` 保留每轮 500 player + 500 enemy 的 Movement/Attack 完整链（不因此宣称新性能数字）。`samples/BulletLockstep.Demo` 保留 Slice 2–9，Slice 5 通过业务 `BossTag`/`WeakPointTag` 和显式销毁处理 boss encounter，见 `kb-bullet-lockstep-demo.md`。

### 删除后的实现收敛（2026-10-11）

**已收敛 Query 销毁与单体 Clone 的旧层级妥协，未重写存储或删除公开 API。** 保留普通 Entity 引用、显式 span 批量删除与持久化兼容合同；本轮没有升级包版本或移动 `v7.0.0`。

- `World.Destroy(in QueryDescription)` 直接共用 `Clear`。旧级联可能扩展到查询外后代，才需要收集、去重和重新分组；当前 Query 选中唯一的完整原型，直接按原 archetype/row 顺序推进版本、压入 free-list、ResetCount 即可。Query-only workload 不再初始化 batch destroy scratch。`FreeListAndDestroyScratchTests.Query_destroy_does_not_initialize_batch_destroy_scratch` 锁定这一点；`WorldLifecycleTests` 保留多 archetype/segment 的状态与回收顺序对照。旧 Clear 相对 Destroy(query) 的速度优势已不再描述当前两个入口。
- `CommandStream.Clone` 只检查 queued existing Destroy，不再全扫 cancelled pending batches。成功取消已移除 pending 索引，real-id 还释放 reservation 并推进版本；source 查找失败仍拒绝 Clone。拒绝类型保持 `InvalidOperationException`，取消 source 的文案为 “no longer alive”。两类 source 先完成有效性判断，再由现有 `CreateCore` 生成一个目标；pending last-wins copy 与 materialized archetype+store overlay 算法仍分开。取消后失败 Clone 的 immediate/deferred 回归还检查 delta 可 Validate、无多余 Reserve/Create、无遗留 reservation。
- Destroy 队列没有 placeholder 生产者：pending 当场取消，未知 placeholder 不 Append。已删除通用 endpoint guard 的 placeholder/role 分支与 `ResolveDeferredCreates` 中的 Destroy-placeholder 循环。`RejectInvalidDestroyEntities` 和 frame flag 仍在 consume 修改 World/target/worker 前拒绝非法句柄；组件内普通/nested/deferred Entity 解析不变。
- 私有 `CloneSingle` 包装已内联；`DestroyAliveEntity`、`CollectDestroyTargetIfAlive` 与 Hero `owner` 名字描述当前行为，单实体 DEBUG scope 保留。Clone 创建包装及 `CopyComponentsFromBatch` 的无用 Entity 参数一并删除；这些死参数在层级删除前就已无用，不把它们算作本次移除层级才失效。

最终 Release/Debug、soak、Hero 门禁与补充基准的已知失败见 `kb-safety-proof.md`。本节描述实现边界，不把单次测量当成稳定性能承诺。

**保留边界：** span 批量 Destroy 仍接受 duplicate/stale/dead 输入及别名 chunk span，稳定收集、去重、稀疏删除/密集压缩不可一起删除。free-list 顺序/版本、reservation、FrozenState/时间线、rollback lease 与原型缓存也不是层级状态。v5 固定零字段、`ComputeChecksum` 的末尾零及旧 opcode 拒绝继续保留。整 World Clone 的空原型策略与 validator 计数扫描不属于本次清理。

## 认知模型

- 把 `Entity` 看成版本化句柄，把组件中的 `Entity` 字段看成数据引用，**不是**隐式 parent、级联规则或子树克隆指令。
- `World.Clone()` 与 `World.Clone(Entity)` 是不同范围：前者整个 World，后者单实体组件；`CommandStream.Clone(Entity)` 也是单实体的延迟创建。

## 入口

- 用户 API 与可运行示例：`docs/api.md`、`docs/examples.md`。
- 旧存档迁移：`kb-snapshot-persistence.md` 的 v5 布局/严格 Load；命令 wire：`kb-command-stream.md`。
- 历史层级实现和缺陷仅供考古：`kb-code-review-findings.md` 已标记退役项；历史计划/审计保留当时事实，不作当前 API 指南。

## 坑点

- 旧含层级存档及旧关系 opcode 被拒绝是**有意的不兼容**；从旧版导出所需数据，再导入应用自己的表示。
- `Destroy` 一个被其他组件引用的实体不会自动遍历、清理引用或销毁引用方。调用方自己决定业务清理和批量销毁目标。
- 不把普通 Entity 引用、`EntitySlot` 跟踪或 nested placeholder 解析误删为“层级功能”。
