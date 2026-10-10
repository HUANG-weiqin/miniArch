# 扁平实体与命令管线收敛执行计划

日期：2026-10-10  
起点：`a98f4d3`（MiniArch 6.2.0）  
状态：执行中；用户已授权端到端实施、并行团队及独立工作树。

## 目标与完成定义

删除内置实体层级，减少命令规则的重复实现，并使回滚后的命令所有权边界可检查。在证据支持时，删除 free-list 的重复版本状态、按需分配销毁工作区。保留扁平 ECS 的现有能力、确定性与性能。

完成必须满足：全部接受范围落地；公开 API 与文档一致；Release build/test、单 host 与多 host soak、真实示例、HeroComing 性能门禁通过；最终集成状态接受独立审阅；本任务修改形成明确提交，原有外部改动不进入提交。候选优化若不满足证明/收益条件，记录证据与不采用理由，不为了完成清单强行实施。

## 冻结契约

1. **授权的能力删除**：内置 AddChild/RemoveChild、父/子/祖先遍历、关系表、级联销毁、子树克隆及相应诊断。`Destroy(Entity)` 只销毁指定实体；`Clone(Entity)` 只复制指定实体组件。保留整 World Clone、组件内普通 Entity 引用及其 placeholder/remap。
2. **其余能力保留**：直接 ref/Span 写入、单 writer、typed component storage、query 顺序/缓存、显式 Validate、Snapshot/Submit/Replay、同步/异步 FrameDelta、嵌套 Entity 解析、reservation/free-list 顺序、物理回滚与 canonical 持久化。
3. **旧数据不能被静默丢弃**：保留无层级 v5 存档布局（关系计数恒为 0）；旧的非零关系计数明确拒绝。保留非层级 delta opcode 数值与格式；旧 AddChild/RemoveChild opcode 明确拒绝，不能重新编号或当作 no-op。拒绝必须发生在可避免的 World 修改之前。需要旧层级数据的用户须使用旧版本导出/迁移，而非由本轮暗自丢弃关系。
4. **命令收敛**：规则同源，typed apply 与 wire emit 分开；不经序列化实现 Submit，不引入万能 IR、重复预计算状态或无必要公共接口。优先复用现有 preparation/store 机制。比较不仅包括组件值，还包括后续 ID、遍历顺序、wire 顺序与异常边界。
5. **回滚边界**：旧时间线命令不得误用恢复后的槽位；丢弃旧命令不得向新时间线释放 reservation。Capture 时的录制状态、Restore 后的旧流、异步已冻结数据须一起建模。普通 Entity/ref/Span 跨 Restore 的陈旧引用仍是调用方边界，不声称凭时间线标记自动修复所有引用。
6. **局部内存优化**：只有证明所有 reservation、取消、Replay、Clone、回滚路径都能从 record 得到同一 Version 才将 free-list 改为仅 Id。scratch 根据真实用途延迟建立，保留预热后复用；不能以首帧尖峰或热路径倒退换取纸面节省。
7. 不改默认 chunk 模型、查询语言、线程模型、公开版本号或发布包；不刷新性能 baseline；不扩大为通用 Replay 事务化。

## 所有权与工作图

| 单元 | 边界 | 依赖 | 验收 |
|---|---|---|---|
| A 扁平核心 | `src/MiniArch/**` 的层级删除与 `tests/MiniArch.Tests/**` 对应迁移 | 契约冻结 | 层级公共 API/实现移除，单体 Clone、旧格式拒绝、非层级 parity 测试通过 |
| B 调用方迁移 | `samples/**`、`tools/**`、`tests/HeroPipeline.Tests/**`、`tests/MiniArch.Benchmarks/**`、`tests/SharedInfrastructure/**` 的层级使用 | 可与 A 并行；不改 core | 删除层级场景或直接使用既有业务身份，不重建通用层级；其余场景照常运行 |
| C 命令管线与回滚 | A 完成后的 CommandStream preparation/consume 与 World Capture/Restore 边界 | 先读设计，实施基于 A 集成态 | 同步/异步/Into/Replay 收敛；旧流拒绝/安全清理；零额外热路径抽象 |
| D 分配器与 scratch | free-list Version 单源、销毁工作区 | 先独立证明；实施基于 A，避开 C 所有权 | ID/version/order/恢复不变，内存边界可验证，门禁不退化 |
| E 集成与知识 | Lead 集成；一次独立最终评审；同步 API/KB/迁移说明 | A–D | 最终完整验证与精确提交 |

A/B 使用不同分支及工作树。跨责任修改由 Lead 裁决，不互相扩大所有权。C/D 在 A 收缩后的实现上推进，避免整理将被删除的逻辑。不得修改、提交、回退或格式化主工作树已有 QueryPlan/知识页改动。

## 执行顺序

- [ ] S0 保存起始状态证据，运行原始 Release 测试及性能门禁。
- [ ] S1 A/B 并行完成层级能力与调用方迁移；新增旧格式明确拒绝回归测试，更新 public API sentinel。
- [ ] S2 集成扁平核心，确认全项目可构建；再落实共同命令规则及独立 typed/wire 输出。
- [ ] S3 落实最小回滚命令所有权边界与错误路径测试，含异步冻结/回收。
- [ ] S4 依据独立不变量证明实施/拒绝两项内存候选，并记录理由。
- [ ] S5 运行最终回归、独立审阅、修复后复跑受影响检查；同步知识库与迁移文档。
- [ ] S6 检查外部改动仍原样保留，提交本任务所有权范围，报告最终证据与明确剩余限制。

## 最小充分验证集

1. `dotnet build -c Release miniArch.sln` + `dotnet test -c Release miniArch.sln`；新增针对性测试证明删除边界和回滚/分配器/命令收敛不变量。不能只删除失败测试来获得绿色。
2. 单 host soak：正常种子覆盖，加小容量、高操作密度的 allocator churn（覆盖取消与回收顺序）。多 host soak：至少 4 hosts，验证 placeholder、普通 Entity 引用与跨 host 分配器收敛。这两层证明不同性质。
3. BulletLockstep 示例：保留的每个场景运行，特别是持久化、回滚、Authority+Mirror async；Lead 亲自驱动一次真实控制台入口。项目无渲染，不额外创造 GUI。
4. 所有受迁移影响的工具/示例构建；保留调用方的真实行为断言，删除的层级场景在文档中明确标记。
5. 必须运行 `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`，Movement ≥1642、Attack ≥997 rounds/s，heap 不持续增长、无异常。性能测量独占窗口，不与 build/test/其他 perf 并发；禁止 `--update-baseline`。若失败先排除环境干扰，同窗口对比并仅撤回本任务失败单元，不使用全树 checkout/stash。
6. 每个源码提交前覆盖对应性能门禁；最终集成态重新跑门禁。独立评审只报告具体可达违反契约的反例，不扩张需求。

## 验证与裁决记录

### 原始基线（a98f4d3）

- `dotnet test -c Release miniArch.sln --nologo`：exit 0，完整日志 `artifacts/flat-command-20261010/baseline-test.log`。
- 原始 `HeroComing.Perf --check-baseline`：exit 1；Movement 1670.6（门槛 1642），Attack 891.6（门槛 997），内存稳定；日志 `baseline-perf.log`。
- 随后 CPU 采样发现 7 个外部 Python 进程各约 100% 单核负载，另有两个 Godot 进程合计约 60%。此轮没有干净测量窗口，不能归因于本任务（源码尚未改动），不以此降低门槛或刷新 baseline。已向用户请求方便时暂停外部计算；团队先继续开发和正确性测试，性能门禁在干净窗口重跑。
- 初始外部修改保存在 `artifacts/flat-command-20261010/external-initial.patch` 与 `initial-status.txt`，仅用于保护/核对，不进入提交。

后续填写各单元与最终集成状态的实际命令、退出码、结果、提交和有证据的范围调整。历史测试/性能记录不当作本轮验收。
