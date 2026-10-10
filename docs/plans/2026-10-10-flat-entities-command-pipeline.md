# 扁平实体与命令管线收敛执行计划

日期：2026-10-10  
起点：`a98f4d3`（MiniArch 6.2.0）  
状态：已实施并完成本轮验收；实现提交 `393ca72`。性能门槛通过，但未取得全机干净窗口，不作性能提升结论。

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

- [x] S0 保存起始状态证据，运行原始 Release 测试及性能门禁；原始 Attack 受干扰窗口下未过，不降低阈值。
- [x] S1 A/B 完成层级能力与调用方迁移；真实旧格式 golden、明确拒绝回归与 public API sentinel 已更新。
- [x] S2 集成扁平核心；共同命令规则保留独立 typed apply / wire emit。
- [x] S3 最小命令时间线、旧帧拒绝/安全 Clear、异步冻结数据边界及回归已落实。
- [x] S4 两项内存候选均采用：record 版本单源、按实际维度惰性 scratch；诊断合同与预热复用保留。
- [x] S5 最终回归、独立审阅与受影响复测通过；API/知识库已同步，外部负载限制单列。
- [x] S6 实现提交 `393ca72`；只提交本任务路径，外部 QueryPlan/知识页修改未纳入。

## 最小充分验证集

1. `dotnet build -c Release miniArch.sln` + `dotnet test -c Release miniArch.sln`；新增针对性测试证明删除边界和回滚/分配器/命令收敛不变量。不能只删除失败测试来获得绿色。
2. 单 host soak：正常种子覆盖，加小容量、高操作密度的 allocator churn（覆盖取消与回收顺序）。多 host soak：至少 4 hosts，验证 placeholder、普通 Entity 引用与跨 host 分配器收敛。这两层证明不同性质。
3. BulletLockstep 示例：保留的每个场景运行，特别是持久化、回滚、Authority+Mirror async；Lead 直接执行真实命令行入口。项目是非交互式 CLI、无渲染，后台 `dotnet run` 足够，不需要打开桌面窗口。
4. 所有受迁移影响的工具/示例构建；保留调用方的真实行为断言，删除的层级场景在文档中明确标记。
5. 必须运行 `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`，Movement ≥1642、Attack ≥997 rounds/s，heap 不持续增长、无异常。性能测量独占窗口，不与 build/test/其他 perf 并发；禁止 `--update-baseline`。若失败先排除环境干扰，同窗口对比并仅撤回本任务失败单元，不使用全树 checkout/stash。
6. 每个源码提交前覆盖对应性能门禁；最终集成态重新跑门禁。独立评审只报告具体可达违反契约的反例，不扩张需求。

## 验证与裁决记录

### 原始基线（a98f4d3）

- `dotnet test -c Release miniArch.sln --nologo`：exit 0，完整日志 `artifacts/flat-command-20261010/baseline-test.log`。
- 原始 `HeroComing.Perf --check-baseline`：exit 1；Movement 1670.6（门槛 1642），Attack 891.6（门槛 997），内存稳定；日志 `baseline-perf.log`。
- 随后 CPU 采样发现 7 个外部 Python 进程各约 100% 单核负载，另有两个 Godot 进程合计约 60%。此轮没有干净测量窗口，不能归因于本任务（源码尚未改动），不以此降低门槛或刷新 baseline。已向用户请求方便时暂停外部计算；团队先继续开发和正确性测试，性能门禁在干净窗口重跑。
- 初始外部修改保存在 `artifacts/flat-command-20261010/external-initial.patch` 与 `initial-status.txt`，仅用于保护/核对，不进入提交。

### 实现与集成裁决

- `393ca72` 是唯一源码集成提交，包含核心、调用方、测试和合同文档；相对起点 `src/MiniArch/` 净减少 **1934 行**（含注释/空白）。没有重建通用 Parent 系统，也没有改变存储、线程、chunk 模型或包版本。
- A 保留非层级 Clone/Destroy 与 Entity 引用，真实旧版 API 产生的 flat v5 / FrameDelta golden 在最终 suite 复验；旧非零关系 v5 和 `0x04` / `0x05` 明确拒绝。
- B 保留 Bullet Slice 2–9；Boss encounter 显式清理业务实体。Hero 用 `ActionOwner` 普通组件，两个角色归属/攻击链测试保留 500+500 的完整门禁负载。只删除纯层级 benchmark 场景。
- C 共用阶段/过滤/group 检查而非万能 IR；World 时间线只在成功 Restore 推进，不进 snapshot/wire。Capture 仅拒绝 real-id reservation；旧帧 Clear 不释放新时间线 reservation。失败 Submit/async 仍清理既有 reservation，group preflight 不是 allocator 事务。
- 异步 soak 暴露的 pending-map 尾部复用缺口在原版也存在；Clone 分支先得到失败见证，再以 `batchIdx < PendingBatchCount` 最小修复，Clone/Set 两变体通过。未以修改 dedup 或跳过 Clone 掩盖错误。
- D 的 free-list 与 rollback payload 只存有序 id，record 唯一持有版本；保持 LIFO、旧代 cancellation 防护和诊断版本观察。scratch 容量规划与执行共用 `UseSparseDestroy`，按 slot/batch/archetype row 各自维度增长。8B→4B 指数组元素，不是整个 World 内存减半。
- 最终集成时补齐两种 soak 的 free-list 日志：版本从 record 派生，多 host 删除旧 pair 反射，使用已有内部访问。工具独立构建后复跑了对应 soak。
- 非作者最终只读审阅未发现新可达违约；覆盖 timeline/Clear、worker 隔离、group/预算一致性、free-list 版本与顺序、scratch 容量、旧 wire 拒绝。无强制阻塞 worker 的测试，不声称确定覆盖多个未完成 worker。

### 最终执行证据

最终数值结果以 [正确性验证报告](../../.knowledge/kb-safety-proof.md#当前复验快照2026-10-10) 为单一汇总；本地完整日志位于 `artifacts/flat-command-20261010/`（原始日志不进入提交）。

```bash
dotnet build -c Release miniArch.sln --nologo
dotnet test -c Release miniArch.sln --no-build --nologo

dotnet run -c Release --no-build --project tools/soak/MiniArch.Soak -- --seed 111 --entity-cap 100 --entity-floor 20 --ops-per-frame 50 --frames 20000 --validate-interval 1 --quiet
dotnet run -c Release --no-build --project tools/soak/MiniArch.Soak -- --seed 111 --entity-cap 100 --entity-floor 20 --ops-per-frame 50 --frames 5000 --async --validate-interval 1 --quiet
dotnet run -c Release --no-build --project tools/lockstep-soak/MiniArch.LockstepSoak -- --seed 222 --hosts 4 --entity-cap 100 --entity-floor 20 --ops-per-frame 50 --frames 10000 --validate-interval 1 --checksum-interval 1 --quiet

# Run every retained slice; Slice 5 must reach boss death, Slice 7 exercises scale.
for slice in 2 3 4 5 6 7 8 9; do
  frames=100
  if [ "$slice" = 5 ]; then frames=520; fi
  if [ "$slice" = 7 ]; then frames=200; fi
  dotnet run -c Release --no-build --project samples/BulletLockstep.Demo -- "$slice" 4 "$frames"
done

dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

- 上述最终运行均 exit 0；`final-accepted-build.log` / `final-accepted-test.log` 覆盖最后的 XML/注释修正。该修正不改 IL，按 AGENTS §5a 不重复性能测量。
- Soak/LockstepSoak/DestroyMany.Perf/HeroComing.Perf/BulletLockstep.Demo/CommandBufferGame.Perf 的独立 Release build 均 exit 0；对应 `final-*-build.log`。
- soak 日志：`final-soak-sync.log`、`final-soak-async.log`、`final-lockstep.log`；八个 CLI 场景：`final-bullet-2.log` 到 `final-bullet-9.log`。可见 PowerShell 窗口并不增加受测路径；按用户要求停止了桌面操作，未将窗口尝试算作验收证据。
- 性能最终日志：`perf-final-comparison.log`；先运行的原始 `a98f4d3` 对照为 `perf-original-comparison.log`，exit 1。`cpu-before-final-perf.log` 记录了外部负载仍存在；团队没有并发 build/test/perf，没有结束外部进程或更新 baseline。本轮通过原数值/内存门槛，不代表已经取得无干扰 benchmark。
- `git diff --check` 通过。提交前用起始 patch 核对两份外部 KB 未变；INDEX 只暂存本任务条目，原 QueryPlan 两个新增条目留在工作树。`kb-query-language-value.md`、`docs/reports/`、`tools/perf/QueryPlan.ValueLab/` 仍未跟踪，未纳入本任务。

### 明确保留的边界

- 干净窗口下的重复、交错性能测量没有取得；不发布性能提升比例，不刷新历史阈值。
- async soak 的 per-thread alloc 计数跨 await 可能为负，只采信正确性结果；不在本轮扩张为性能 harness 重做。
- 不承诺任意 Entity/ref/span 跨 Restore 有效，不把有限测试当形式化证明；Replay 仍不提供通用事务回滚。
