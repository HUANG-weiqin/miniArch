# CommandStream Design

## 结论

新增 `MiniArch.Core.CommandStream` 作为专家模式命令流：默认 `CommandBuffer` 继续保留录制期去重与安全归约语义；`CommandStream` 使用 typed component command stores + lightweight structural log，优先降低 record 成本，并保持后续生成 `FrameDelta` 的数据形状。

## 目标

- 在真实游戏稳态压测中提供比默认 `CommandBuffer` 更低的录制开销。
- 不改变现有 `CommandBuffer` API、语义和性能边界。
- `Create()` 仍通过 `World.ReserveDeferredEntity()` 返回确定 `Entity`，保证后续 `FrameDelta` 能复用相同 id/version。
- 记录组件时保存 typed value，`Submit()` 直接写 typed value；`Snapshot()` 再转成 `FrameDelta` 需要的 raw bytes。

## 非目标

- 不在首版实现跨线程提交、异步 submit+snapshot 或全局命令排序优化。
- 不把 `CommandStream` 做成默认安全 API；重复 add/set/remove 的净效果由专家用户负责。
- 不删除或重写 `CommandBuffer`。

## 架构

- `CommandStream` 维护三类数据：
  - typed component stores：`Add/Set` 按组件类型记录 typed value 和 existing command，模仿 Friflo 的 `ComponentCommands<T>[]`。
  - structural log：`Remove/Destroy` 轻量追加。
  - created entity side table：`Create()` 预留实体，created entity 的组件在录制期分流到 created component refs，提交时一次性 materialize。
- 专家语义：component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；调用方需避免依赖同帧冲突命令的顺序副作用。
- `Submit()` 流程：
  1. 计算本帧被 destroy 的 created entity，释放这类 reservation。
  2. 对未销毁 created entity，按 typed component refs 构造 archetype 并直接 materialize。
  3. 对 existing entity 按组件类型批量应用 typed add/set。
  4. 应用 structural log 中的 remove/destroy。
  5. 清空当前流。
- `Snapshot()` 流程：
  - 用 typed stores + side table + structural log 构造 `FrameDelta`，组件值在此阶段转 raw bytes，然后 `DeepCopyOwnedData()`，不修改 world。

## 性能注意

- component `Add/Set` 必须按 typed store 记录；raw byte slab 会让 record 明显慢于 Friflo。
- created components 必须在录制期分流，否则提交期“每个 created entity 扫整条日志”会退化到 O(created × commands)。
- created materialize 使用小型 archetype cache，避免每个 projectile spawn 都分配 signature/type array。
- **2026-06-11 修正**：pending batch 组件存储由前缀和+扁平数组改为 per-batch 单链表，消除交错创建时的组件归属错误。详见 `kb-command-buffer-feasibility.md` 坑点。
- 性能测量必须使用 `-c Release`。

## 验证

- 单元测试覆盖 create/add/set/remove/destroy/reuse，以及 `Snapshot()` 生成可 replay 的 `FrameDelta`。
- `CommandBufferGame.Perf` 增加 `MiniStream` row，对比默认 MiniArch、Friflo、Arch。
- 修改 `src/MiniArch/` 后必须跑 HeroComing 回归门禁。
