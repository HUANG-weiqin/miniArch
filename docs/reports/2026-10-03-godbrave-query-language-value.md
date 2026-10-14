# GodBrave：声明式查询语言的真实价值与编译摊销

日期：2026-10-03。范围：只读调查 `E:/godot/godbrave`，未实现语言、未修改游戏代码、未对该游戏进行新的性能测量。

源码基点：`f3a609d9b5b3328a831ecdea32abc9fd944827fe`。调查时唯一已有工作区改动是 `presentation/world/ground_item_panel_theme.tres`，与本报告无关且未改动。

## 核心一句话

**GodBrave 有“减少重复查询组织”的真实需求，也有编译一次、多次执行的生命周期；但目前证据支持先验证有界查询计划，不支持直接立项完整 SQL。**

给老板的话：

> 这里不是缺一套规则语言：游戏已经把 GDS 规则编译成不可变 IR，在整个 session 中反复执行。仍由应用手写的是物品位置筛选、跨边界逐项补齐 profile、状态枚举、邻域选择，以及多项属性查询重复收集同一角色的状态和装备。查询层若能在同一读取阶段融合这些工作，并保留强类型输出与原有顺序，就有价值。但当前正式世界只有 2～11 个角色，机制世界初始 33 件物品；很多收益用小型 typed helper 就能得到。语言必须与这样的优秀手写实现比较，并计入新增编译、索引维护和结果物化成本，不能沿用十万行合成实验的倍数。

本报告补充 [MiniArch 候选执行策略实验](2026-10-03-query-language-value.md)，不把其查询阶段数字移植成 GodBrave 的收益。

## 1. 核心运行机制：不是每帧扫十万角色

```text
创建或 Reset：GDS 定义 → 编译/原生校验 → session 持有不可变 Program
每次决策：fresh observation → GDS 选择有限 intent/具名 root
执行一根：查询属性/费用 → typed commit → FIFO reactions/必需收尾
建立下一调度边界 → 下一个角色决策，或发布人类可行动的稳定投影
切回已有 live session：复用它的 Program；不重新解释同名定义
```

这里的 Scheduler 是绝对时间、事件驱动的，不是每个渲染帧运行同一批查询。自动行动可跨帧配速，但一根内部同步完成。高频指“随根、反应、表达式调用重复”，不代表已经测得每秒执行次数。

源码入口（下文路径均相对 `E:/godot/godbrave`）：

- `worlds/world_declarations.gd:36`：`compile_rules` 汇集模块并调用编译器。
- `product/rules/ir_compiler.gd:26`：压平整数表，经 `ProgramIngress.build` 得到已校验 native Program；不是生成机器码的 SQL 编译器。
- `product/activation_lifecycle.gd:262`：新 candidate 编译；同文件 `switch_to` 的已有 handle 分支不编译。
- `src/GodBrave.Product/ProductWorldAssembly.cs`：每个 session 的组装保存 exact `RuleIrProgram`。
- `src/GodBrave.Foundation/Session/WorldSessionConstruction.cs:105`：Foundation 创建 MiniArch World。
- `product/automated_turn.gd`：每次决策读取 targeted Status 与邻格，选择后调用 `RunAutomatedIntent`。
- `src/GodBrave.Product/Rules/RuleIrRootExecution.cs:188`、`RuleIrReactionFlow.cs`：具名根、查询、提交和 FIFO 反应。

三个 C# runtime 项目固定使用 `MiniArch [6.1.0]`、`Roug2.Kernel [2.0.0]`；本调查不是把当前 MiniArch 工作区代码注入游戏测量。

## 2. 状态结构：计划可复用，不等于查询结果可复用

| 需要保留的东西 | 生命周期 | 对查询层的意义 |
|---|---|---|
| Program、定义地址、规则/修正器表、建局关系 | session 内不可变 | 可用于静态裁剪、预先绑定词汇和固定查询形状 |
| Character HP/位置/调度 anchor、Status Attachment、Item 位置/存活 | ECS live authority | 每次执行读取当前值；提交后不能无条件沿用旧索引或旧绑定 |
| root 内事件、冻结效果数量、限次、Trace | 当前同步 root | 有明确观察顺序；不是可随意合并的数据库事务 |
| HUD 投影与派生索引 | 被证明有效的读取/发布阶段 | 可复用派生值，但不能代替 command 的 fresh admission |

**三个不同寿命：编译计划、派生索引、结果值。** 改 HP/位置/装备通常不需要重新解析或编译固定查询；但依赖这些值的索引、绑定和结果可能需要刷新。Reset 的新 session 必须重新绑定能力；不能让旧 Entity/row ref 跨 session 使用。

## 3. 信息流与实际规模

信息流：`ECS 当前值 → Foundation 窄观察 → Product 补齐机制值/求规则 → Bridge 强类型结果 → GDS 选择或 UI 投影 → 下一次请求`。

正式声明的初始规模，经三个 draft 数组的逐项计数确认：

| World | 地图格数 | Character | Carried Item | Ground Item |
|---|---:|---:|---:|---:|
| `tutorial_world.gd` | 7×5 | 2 | 0 | 0 |
| `mechanism_world.gd` | 12×8 | 2 | 10 | 23 |
| `generated_world.gd` | 96×96 | 5 | 0 | 0 |
| `generated_mechanism_world.gd` | 256×256 | 11 | 10 | 23 |

这些是 construction 输入，不是运行期峰值：状态与物品可随玩法增减。地图有 65,536 格也不意味着有 65,536 个角色或物品，不能把地图大小直接代入这些查询的行数。

项目知识页 `kb-presentation-orchestration.md:398` 留有 headless 性能记录，其中结算分段明确标为 2026-09-26。其后规则执行已下沉 Product C#，当前投影也有缓存；这些旧记录只能指引重新采样，**不能作为本次源码版本的耗时基线，更不能用于推算 SQL 加速比例**。

## 4. 用户现在手写什么，查询层真正能省什么

### A. 物品观察与 profile 补齐：最清楚的重复工作

需求：背包/装备/地面物品的有序完整只读 profile；地面面板只需要角色脚下物品。

现状：

1. `src/GodBrave.Foundation/Items/CarriedItem.cs:101`、`:148` 与 `EquipmentObservation.cs:12` 分别扫描 Item，按位置/owner 筛选、排序，返回 reference 行。
2. `GodotBridge/Generated/CarriedItemsObservation.cs:58`、`GroundItemsObservation.cs:72`、`EquippedItemsObservation.cs:69` 逐行补齐 Product profile。
3. `GodotBridge/ProductItemProfileProjection.cs` → `src/GodBrave.Product/Items/ItemProfileDecoder.cs:34` → `ProductOperationScope.TryFindItem` → `CarriedItem.cs:215` 的 `FindItem`，每颗再次线性扫描 Item。
4. `presentation/items/item_projection.gd:56` 自己按完整 `InventoryStackKey` 分组、计数、选择最小 exact reference、排序；`product/ground_pickup.gd:205` 另做按 cell 筛选。

若总物品数为 I、返回 R 行，定位部分是 `O(I + R×I)` 上界，R 接近 I 时为二次量级；这只是源码工作量分析，不是实测热点。profile 的不同 role 数据实际位于同一个 Item Entity，**不是必须进行 hash join 的独立实体表**。

可验证的优化：一次位置筛选后就完成合法的 typed profile 投影；或在一次稳定读阶段共享 exact reference→Entity 定位。按 cell 的窄结果还能避免把全部 Ground profile 物化到 Bridge 后才筛选。完整 HUD 仍需要全世界地面投影，不能只给它脚下结果。

不做语言的最小替代：有界批量 profile projection / `ObserveGroundItemsAt` helper，或由 Foundation 持有的定位优化。必须经现有扩展面保留 Product profile owner，不能让 Product 直接拿 World 扫表。

调用频率反证：`presentation/product_hud.gd:84` 在无 Item 变化且 activation 缓存有效时直接复用已发布行；`stable_projection_publisher.gd:28` 由 committed Item 证据决定重读。它不是每个行动都重扫/重组全部物品。

**判断：需求与优化机会强；完整 SQL 的必要性弱。**

### B. 一次攻击/属性面板的多项查询：共享纯读取阶段

`RuleIrRootExecution.cs:188` 的正常双方存活路径，在第一次攻击提交前依次查询：

```text
actor.Attack → target.Defense → target.DamageTaken → actor.ActionCost
```

每个 `RuleIrQueries.Query` 经 `TryCollectMatching` 调 `RuleIrBindings.TryCollectCharacter`，重新观察对应角色的 Status、装备、profile 和 traits。面板 `RuleIrQueries.cs:260` 的 `EffectiveStatsCore` 同样调用 Attack/Defense/Speed 三次。

Status 观察还经 `CharacterStatusSession.cs:285` 先物化全世界 Attachment 列表，再筛选一个 owner；装备观察扫描 Item，逐件 profile 读取又走 exact reference 定位。

可验证的优化：在**无 intervening mutation 的该次纯读取段**，每个角色只收集一次绑定；按不同 query 上下文分别求值。可把不可变定义的 query/hook 匹配纳入计划，避免收集确定无用的候选。

不做语言的最小替代：一个 call-local 多查询 helper，加有界的共享读取数据；不建立跨 root 的有效属性缓存。

必须保留：每项 query 的 base/context、算术先于夹限的两阶段顺序、checked 整数、每个 binding/ordinal 的贡献和原有失败顺序。普通 `SUM(modifier)` 不能表达这些结果。`STATUS_COUNT` 等条件仍需要 fresh Status，不能因“没有 Status modifier”就省掉所有状态读取。

**判断：这是较有价值的查询计划消费者；更像优化现有 Rule IR backend，而不是再造规则语言。**

### C. 状态 badge：批量读比逐角色重复扫更自然

`presentation/product_hud.gd:336` 在状态投影时对每个声明角色调用一次 targeted Status observation；每次底层都重读全部 Attachment。

若角色数为 C、Attachment 总数为 S，存在 `O(C×S)` 重复读取。给全部 badge 批量输出时，可一次读 Attachment 按 owner 分组，再按角色/reference 与 status key 的合同顺序输出；无状态角色也必须得到合法空组。

不做语言的最小替代：用途有界的批量 Status observation。不能为了优化发布阶段，迫使普通单角色 reaction 读取全量 census。

频率：状态变化/需要全量同步的发布路径，不是每个渲染帧。当前 C 最大 11；一次目标查询则可能直接扫描更便宜，不宜每次都新建全量 hash index。

**判断：批量查询需求成立；目前性能收益未测，固定全量分组策略有退化风险。**

### D. NEIGHBORS 与 ANY_CHARACTER：可以优化读取，不能优化掉时序

`RuleIrExpressionEvaluator.cs:251` 的 `ReadNeighbors` 读 roster，再逐个 `ObserveCharacter`；后者的 exact reference 定位在 `WorldSessionObservation.cs:303` 线性扫描。因此有 `O(C²)` 定位上界。需求只是以当前中心为基准的 3×3、非本人、未败退、有序 exact references；一次 Character 扫描就可完成，不一定需要空间索引。

它有真实内容消费者：`product/rules/equipment.gd:307` 的引信对邻域加状态。另一个 ANY_CHARACTER 消费者是重锤。`RuleIrReactionFlow.cs:264` 还会对其他 living holder 收集绑定。

这里已经有一个有界优化：`RuleIrProgram.cs:224` 的 `HasAnyCharacterRule(hook)` 检查不可变规则表，没有相应规则就不读 roster。**源码是每次查规则表再裁剪路径，不是本次证明了一个提前生成的索引/机器码。**

不能把整根反应一次性变成旧 snapshot 查询：subject 规则提交后，ANY_CHARACTER 可能读到新获得的状态；早先反应也可能杀死 holder 或移除状态。`IsCurrent` 与每个效果开始时的目标求值有实际语义，不是可随意删除的重复检查。

不做语言的最小替代：Foundation 窄邻域枚举 helper；只在相同读阶段共享定位。AI `ObserveNeighborhood` 还包含穿角与可达性判据，**不等同**于 Rule IR 的 3×3 NEIGHBORS，不能以同名“附近”合并。

**判断：形态适合声明式选择；当前规模小，且通用自动融合最容易破坏真实语义。**

## 5. 类 SQL 示例：只是需求记法，不是已实现 API

下面 `ItemView`/`StatusView`/`CharacterView` 是逻辑只读视图，不是现有表、raw World 入口或获批词汇。参数在执行时绑定，不拼进查询字符串。

```sql
-- 脚下地面面板：先筛选，再生成返回所需的完整 typed profile
SELECT Reference, Profile
FROM ItemView
WHERE Location = GROUND AND Cell = :cell
ORDER BY Reference;

-- 背包投影：StackKey 表示现有 owner 定义的完整 profile 等价关系
SELECT StackKey, COUNT(*) AS Count, MIN(Reference) AS MinimumReference
FROM ItemView
WHERE Location = CARRIED AND OwnerReference = :owner
GROUP BY StackKey
ORDER BY MinimumReference;

-- 所有 badge 的有序状态行；调用方仍要为没有状态的角色输出空组
SELECT CharacterReference, DefinitionKey, Stacks
FROM StatusView
WHERE CharacterReference IN :references
ORDER BY CharacterReference, DefinitionKey;
```

第二条还需取最小 reference 对应的完整 profile 才能保持现有 UI 结果契约。只按 definition key 分组会错误合并不同药量/参数/装备 proc 的 Item。聚合代表只服务已有 UI 选择合同，不自动授权“任意同类实例”命令；提交仍 exact-target-or-reject。

这些写法展示用户愿意声明的结果，并没有证明 SQL 文本比 C# builder 或扩展既有 IR 更合适。

## 6. 把编译成本真正算进去

### 6.1 这类工作负载有摊销机会

- query 形状固定，actor/cell/hook/damage type 是参数；不是每个参数值生成一份新程序。
- Program 与词汇在 session 内固定；可在 candidate 编译/校验时一起准备计划，也可对不依赖内容的固定查询在应用构建/启动阶段准备。
- 同一 session 多根、多表达式执行同一形状；已有 session 切回复用原 Program。
- 不可变定义可预先绑定；mutable HP/Status/Item membership 仍在执行时读取。

因此，“有编译成本所以一定不值”不成立；“能复用所以编译成本忽略不计”同样不成立。新计划是否实际生成机器码、解释整数表或走泛型执行器，当前没有实现可测。

### 6.2 正确比较式

对同一 session/workload：

```text
ΔT = 新增编译/绑定/优化/代码生成成本 + 新增首用 JIT/冷启动
     + 索引构建/维护/重优化相对手写方案的额外总成本
     + Σ(计划执行与完整结果交付 - 优秀手写执行与同样结果交付)
```

`ΔT < 0` 才证明时间上赚回。原本已有的规则 authoring/IR 校验共同成本不能全部重复记为新查询语言成本；两方案不同的额外成本也不能藏进 setup。

若每次执行节省固定 `d > 0`，额外固定成本为 C，则需 `N > C/d`；如果每次还多维护 m，则分母为 `d-m`，小于等于零时永远无法靠重复次数回本。工作集变化时应逐次累加，不能套固定 d。

纯算术示例，**不是 GodBrave 测量值**：额外编译 20ms，单次净省 20µs，需要超过 1,000 次；只省 1µs 则超过 20,000 次。被缓存跳过的物品刷新不算执行，11 个角色也不等于 11 次重复编译。

### 6.3 最小验证，不是现在开始实现

只选 A 的完整物品投影与 B 的多项属性查询，各比较三个对手：

1. 现有实现；
2. 最小优秀 typed 手写 helper（同样复用容量）；
3. 若后续获批，能实际产出的受限计划执行器。

先按真实正式世界测，再用扩大 C/I/S 的压力数据找交叉点；后者标为合成扩容，不冒充现行用户规模。

Release 分段记录：新增编译/首用、定位/扫描、绑定与表达式、结果复制/Bridge marshalling、整根/整轮耗时、分配与长期内存。既测首次 activation/Reset，也测稳态和切回；累计实际调用次数，给出包含冷成本的回本点。未出现的 Top-K 不作为本项目的必测正例。

## 7. 去幻觉：哪些看起来像 SQL 机会，其实不是

- **游戏已能数据定义规则。** GDS→typed IR 已解决大量“加内容不改执行器”的问题；SQL 不能重复领取这部分价值。
- **Fixture 查询已索引。** `FixtureOccupancy.cs` 在构造重建 cell/reference 索引，并由提交收尾维护；`FindAt`/`FindFixture` 是 lookup。把它换成扫描再 hash join 不叫优化。
- **Scheduler 已有专用算法。** `SchedulerBoundaryDriver` 复用输入/候选缓冲；`SchedulerBoundaryResolver` 扫描最早 exact rational 时刻并输出完整 ready batch。只取 Top-1 会改变公开 ready-list 契约；通用 SQL 也不能用浮点替代精确时间。
- **不是每个发布都重拍地图/物品。** map 使用 change log；Item 投影有 evidence/epoch 缓存。不能用已被删除的重复工作作为新方案收益。
- **没有找到现行通用排行榜/大规模 Top-K 消费者。** 引信“附近”也不是最近 K 个。MiniArch 合成 Top-K 加速不证明本游戏需要它。
- **全量扫描可以省，因果观察不一定能省。** 规则条件、效果目标与提交各有时点，不能跨 mutation 做公共子表达式缓存，不能用最终 HP 或 snapshot 替代 Trace。
- **SQL 前端主要是表达与工具成本。** parser、报错、词汇绑定、计划选择、调试和生命周期不是免费附送；一次项目里几个稳定查询未必抵得上长期语言维护。

语义回归指路（本次阅读了对应测试，未重新执行 GodBrave 测试）：

- `tests/GodBrave.Product.Tests/RuleIrNumericQueryTests.cs`：贡献顺序、同定义多 proc ordinal、零速度和故障语义。
- `RuleIrReactionFlowTests.AnyCharacter.cs`：subject 状态提交可在同一事件中启用其他角色规则。
- `RuleIrReactionFlowTests.HolderLiveness.cs`：已死亡/被移除绑定不再触发；已开始效果不会因自移除而取消。
- `tests/GodBrave.Foundation.Tests/FixtureOccupancySessionTests.cs`：真实提交同步释放占位、blocked mask 和 change log。

## 8. 重建直觉与立项边界

从零重建只需要三件事：

1. **声明要什么结果**：字段、筛选、关系、顺序和空结果；不把命令、状态 authority 混进查询。
2. **在合法读取阶段少做重复工作**：融合投影、共享定位/绑定、按实际规模选择扫描或索引。
3. **证明总账与语义都更好**：对齐优秀手写、计入编译/维护/物化，完整核对贡献、顺序和 fresh observation。

GodBrave 当前硬边界：Product 不能持有 raw World；GDS 只拿 opaque handle 与强类型值；规则 IR 之外的通用字符串 gameplay 路由/万能解释器不获授权。可行性讨论不等于修改这些合同。若要新增文本 authoring 或查询边界，需要 owner 明确批准；不能直接增加 `ExecuteSql(string)` 让 GDS 查询任意 ECS 槽位。

**建议给老板的决定：以真实 A/B 查询为消费者，允许验证“有界、参数化、可复用计划”的总成本和工程节省；暂不承诺 SQL 文本、通用优化器或新规则执行系统。** 若 typed helper 已消除主要重复工作，计划层没有额外收益，就停在 helper；只有更多独立查询消费者或实际非 C# authoring 需求出现，才重新评估文本语言。

## 调查验收边界

- 已完成：核对项目说明/相关 current owner、编译/session 主链、真实查询与调用条件、四个正式 World 的初始数量、既有优化和语义测试源码。
- 未完成且不声称完成：GodBrave 当前版本 profiling、编译器实现、编译延迟/回本点/端到端加速实测。
- 游戏工作区保持只读；本次仅在 MiniArch 写入本报告并更新查询价值知识导航。
- 文档验收：本报告及关联价值报告/知识页/导航共四文件的本地链接目标、知识页模板与外部源码定位检查通过；`git diff --check` 通过，GodBrave 原有 theme diff 的 SHA-256 前后相同。
- MiniArch `dotnet build src/MiniArch/MiniArch.csproj -c Release --no-restore` 通过（0 warnings/errors）；`dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore` 通过（1171 passed，0 failed/skipped）。它们是文档改动的仓库回归，不是 GodBrave 或新语言的性能/正确性验证。
