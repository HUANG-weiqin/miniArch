# MiniArch 代码硬化执行计划 (88-92 → 92-94)

**起草日期**: 2026-07-01
**目标仓库**: `miniArch`（主仓）。`miniArch-bullet-lockstep` 视为派生副本，每个 phase 完成后由负责人决定是否同步（建议同步）。
**总体目标**: 4 周内完成 7 个低垂果实，把代码质量评分从 88-92 推到 92-94。AOT/source-gen、并发模型重写、512-bit 掩码可生长化等大工程**不在本计划范围**（见末尾"Out of Scope"）。

---

## 0. 前置准备（执行者必读，开工前 0.5 天）

### 0.1 通读约束
1. 读 `AGENTS.md` 全文，特别注意：
   - **§5 架构变更回归门禁（强制）**：任何 `src/MiniArch/` 改动提交前必须跑
     ```powershell
     dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
     ```
     吞吐低于阈值 / 内存增长 / 崩溃 → **`git stash` 回退**。
     **阈值以你所在仓库的 `AGENTS.md` §5 为准**（miniArch 仓为 Movement ≥1210, Attack ≥767；bullet-lockstep 仓为 Movement ≥1407, Attack ≥854）。拿不准就跑。
   - **§5a 确定性豁免**：本计划的所有改动**都不符合豁免条件**（都不是纯文档/死代码删除/partial 迁移/private 重命名/空白），所以**全部要跑门禁**，除了：
     - Task 1（demo isqrt）改的是 `samples/`，不触发门禁，但仍需 `dotnet build` + 跑 demo。
     - Task 6（PBT）改的是 `tests/`（非 HeroPipeline.Tests），不触发架构门禁，但必须 `dotnet test` 通过。
2. 读 `.knowledge/INDEX.md` 和与本计划相关的 `kb-snapshot-persistence.md`、`kb-command-stream.md`、`kb-lockstep-playbook.md`、`kb-test-workflow.md`、`kb-profiling-workflow.md`。
3. 读 `.knowledge/kb-hero-pipeline-regression.md`，确认当前 baseline 数字。

### 0.2 分支与基线
1. 从 main 拉新分支：`git checkout -b chore/codebase-hardening`。
2. 跑一次门禁记录改动前 baseline（不改 baseline 文件，只看 stdout 数字），存档作为本分支的"零点"。
3. **每个 Task 独立 commit**，commit message 模板：`refactor(world): use Conditional DEBUG for ThrowIfDisposed (#Task3)`。便于失败时单 Task 回退。

### 0.3 门禁矩阵（速查）

| Task | 改 src/MiniArch? | 触发 §5 门禁? | 备注 |
|---|---|---|---|
| 1 demo isqrt | 否（samples/） | 否 | 仅 build + 跑 demo |
| 2 FrameDelta 预算 | 是 | **是** | hot path，重点观察 |
| 3 `[Conditional("DEBUG")]` | 是 | **是** | Release IL 实际变化，需验证 |
| 4 `Entity.IsPlaceholder` | 是（43 站点） | **是** | 语义重构，重点观察 |
| 5 WorldSnapshot CRC32 | 是 | **是** | 仅持久化路径，对 HeroComing 吞吐应无影响 |
| 6 PBT (FsCheck) | 否（tests/） | 否 | 仅 `dotnet test` |
| 7 SpanFeeder → struct | 是 | **是** | WorldSnapshot checksum 路径去分配 |

---

## Phase 1 — 可信度速赢（第 1 周，~2.5 人日）

> 目的：用最小改动堵掉"demo 自毁确定性证明"和"wire OOM 攻击面"和"快照损坏无诊断"三个逻辑硬伤。改完 demo 的 1000 帧 checksum 才真正跨硬件有效。

### Task 1 — demo 用整数 isqrt 替换 `Math.Sqrt` 【0.5 人日，无门禁】

**Why**: `samples/BulletLockstep.Demo/Systems/HomingBulletSteerSystem.cs:45` 用 `(int)Math.Sqrt(dist2)`。demo 是库确定性声明的**证据**，证据本身用了跨硬件不确定的 IEEE-754 sqrt，导致"1000 帧 checksum 一致"只在同型号 CPU 上成立。换成整数 isqrt 后声明才自洽。

**Files**:
- `samples/BulletLockstep.Demo/Systems/HomingBulletSteerSystem.cs:45`

**Steps**:
1. 在 demo 项目内（建议 `Components.cs` 同目录新建 `IntMath.cs`，或放进已有 utility 文件）加入位运算 isqrt：
   ```csharp
   internal static class IntMath
   {
       // 整数平方根：返回 floor(sqrt(x))，x >= 0。纯整数运算，跨硬件确定。
       public static int Isqrt(long x)
       {
           if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
           if (x == 0) return 0;
           var b = (int)Math.Min(x, long.MaxValue);
           // 经典位构造法：31 次迭代，无浮点
           int result = 0;
           int bit = 1 << 30;
           while (bit > b) bit >>= 2;
           while (bit != 0)
           {
               if (b >= result + bit)
               {
                   b -= result + bit;
                   result = (result >> 1) + bit;
               }
               else
               {
                   result >>= 1;
               }
               bit >>= 2;
           }
           return result;
       }
   }
   ```
   > 注意：`Math.Min(x, long.MaxValue)` 是常量折叠，**不是**浮点。整段方法无任何 `double`/`float`。
2. 把 `HomingBulletSteerSystem.cs:45` 的 `var dist = (int)Math.Sqrt(dist2);` 改为 `var dist = IntMath.Isqrt(dist2);`。
3. **全 demo 再 grep 一遍** `Math.Sqrt` / `MathF.Sqrt` / `Math.` 浮点 API，确认没有遗漏（`Math.Abs` 整数版可以保留）。执行者应跑：
   ```powershell
   rg -n "Math\.(Sqrt|Sin|Cos|Tan|Atan|Atan2|Pow|Log|Exp|Floor|Ceiling|Round|Truncate)|MathF\." samples/
   ```
   任何命中点都要评估是否破坏确定性，能换整数运算就换，不能换就在 commit message 和 PR 里显式标注"已知跨硬件软肋"。
4. 跑 demo：`dotnet run -c Release --project samples/BulletLockstep.Demo`，确认所有 slice 仍通过、跨 host checksum 仍一致（这次是真跨硬件一致，但本机跑只能证明本机一致，跨硬件需在 CI 多架构矩阵验证——记进风险登记）。

**Definition of Done (DoD)**:
- [ ] demo 无 `Math.Sqrt` / `MathF.*` 浮点调用
- [ ] `dotnet run -c Release --project samples/BulletLockstep.Demo` 全 9 slice 通过
- [ ] 新增 `IntMath.Isqrt` 单元测试覆盖 0/1/2/3/4/15/16/2^31-1 边界（放进 demo 同级 test 或 `tests/MiniArch.Tests`，独立 `[Fact]`）

**风险**: isqrt 与 `Math.Sqrt` 的结果在边界（完全平方数附近）可能差 1，导致 steering 方向在极少帧不同，进而改变后续碰撞链 → 整个 1000 帧 checksum 变化。这是**预期内**的，不是 bug。若 checksum 仍一致反而要怀疑 isqrt 写错。执行者应在 commit body 记录改动前后的 checksum 值对比。

---

### Task 2 — `FrameDelta` wire size 预算 【0.25 人日，**触发门禁**】

**Why**: `FrameDelta.Deserialize`（`src/MiniArch/Core/FrameDelta.cs`，方法头约在第 73 行起）目前只校验 op kind 和 varint 边界，没有 frame 级字节/操作数上限。恶意 peer 发一个 2GB 全 `Reserve` 的 delta 会把接收方 OOM 在校验之前。lockstep/netcode 库必须 fail-loud 在分配之前。

**Files**:
- `src/MiniArch/Core/FrameDelta.cs`（Deserialize 入口 + OpDecoder 主循环）
- `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`（新增拒收用例）

**Steps**:
1. 在 `FrameDelta` 类加常量（值可调，先保守）：
   ```csharp
   public const int MaxFrameBytes = 16 * 1024 * 1024;   // 16 MiB 单帧上限
   public const int MaxOpsPerFrame = 1_000_000;          // 100 万 op 上限
   ```
2. `Deserialize(ReadOnlySpan<byte> wire)` 入口在拷贝到 `_buffer` **之前**先判 `wire.Length > MaxFrameBytes` → 抛 `ArgumentException("FrameDelta exceeds MaxFrameBytes budget")`。
3. 在 `OpDecoder.MoveNext()` 主循环里维护一个 op 计数，每次成功 `MoveNext` 后 `if (++count > MaxOpsPerFrame) throw ...`。
4. 这两个常量在源码/XML doc 里写清"默认 16MiB/1M op，调用方可自行包裹传输层预算"。
5. 新增测试：构造超长 wire（如 `MaxFrameBytes + 1` 字节）和构造 1M+1 个 Reserve op 的 delta，断言 `Assert.Throws<ArgumentException>`。

**DoD**:
- [ ] 超字节 wire 在分配前被拒
- [ ] 超 op 数 delta 在分配前被拒
- [ ] 新增 ≥2 个 `[Fact]` 用例
- [ ] `dotnet test` 全绿
- [ ] **门禁通过**（`HeroComing.Perf` 吞吐 ≥ 零点基线的 80%；正常帧远小于 16MiB，应无影响）

**风险**: 极低。常量判比较不会进 hot path 的逐 op 循环以外的任何地方。仍要跑门禁确认 `Replay` 单帧路径没回归。

---

### Task 5 — `WorldSnapshot` 尾部 CRC32 完整性校验 【0.5 人日，**触发门禁**】

**Why**: `WorldSnapshot.Save/Load`（`src/MiniArch/Core/WorldSnapshot.cs`）目前 magic `0x4D415243` + `FormatVersion=3` 之后直接写 schema + 数据，没有尾部校验。文件截断/位翻转只能靠 `BinaryReader` 抛 `EndOfStreamException` 或 `IOException`，错误信息对调用方毫无意义。加 CRC32 让损坏在 load 入口被显式检测并给出 offset。

**Files**:
- `src/MiniArch/Core/WorldSnapshot.cs`（Save 末尾 + Load 开头 + 格式版本决策）
- `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

**Steps**:
1. **格式版本决策（执行者必须先定）**：
   - 选项 A：bump `FormatVersion = 4`，旧 v3 文件仍能读（v3 路径无 CRC，跳过校验），新写入用 v4 + CRC。**推荐**，向后兼容。
   - 选项 B：保持 v3，把 CRC 写成"可选尾段"——破坏纯净性，不推荐。
   - 采用 A。
2. 加 NuGet 依赖 `System.IO.Hashing`（.NET 8 官方包，含 `System.IO.Hashing.Crc32`）。改 `MiniArch.csproj`：
   ```xml
   <PackageReference Include="System.IO.Hashing" Version="8.0.0" />
   ```
3. **Save 路径**：写到 `BinaryWriter` 的所有 body 字节先缓冲（或在写入流外层套一个 `MemoryStream` 收集），算 `Crc32.HashToUInt32(bodyBytes)`，最后写：`writer.Write(crc32)`。结构 = `[magic][version=4][schema][...data...][crc32:u32]`。
4. **Load 路径**：读 magic + version → 若 v4，先读到尾部 crc 字段，重算 body CRC 比对，不匹配抛 `InvalidDataException($"WorldSnapshot corrupted: CRC mismatch at offset {offset}")`。若 v3，跳过 CRC（向后兼容），可在 trace 级别日志记 "v3 snapshot loaded without CRC verification"。
5. 新增测试：
   - v4 roundtrip：save → load 成功，checksum 不变。
   - v4 损坏：save 后翻转 body 中间一个字节 → load 抛 `InvalidDataException`。
   - v3 向后兼容：手写一段 v3 magic+version 的最小流，load 不抛 CRC 错（可能抛别的，但不是 CRC）。

**DoD**:
- [ ] v4 写入带 CRC，v4 读入校验 CRC
- [ ] v3 文件仍可读（向后兼容）
- [ ] 损坏检测给出可读错误信息
- [ ] ≥3 个新 `[Fact]`
- [ ] **门禁通过**（CRC 只在 Save/Load 路径，对 HeroComing steady-state 吞吐无影响）

**风险**: 若 Save 当前直接写到调用方传入的 `Stream`（而非内部 buffer），算 CRC 需要先在内存里凑齐 body 再写。执行者需读 Save 现有实现确认是 `BinaryWriter` over `FileStream` 还是 over `MemoryStream`。若是前者，方案改为：内部用 `MemoryStream` 收 body → 算 CRC → 把 `MemoryStream` 原样 + CRC 写到目标 stream。这会引入一次 body 大小的临时分配，对"save 整个 world"这种低频操作完全可接受，**不要**为此破坏流式 API。

---

## Phase 2 — Property-Based Testing（第 2 周，~2 人日）

> 目的：本计划**最高价值**的一项。引入 FsCheck，对序列化 roundtrip 和多 host replay 收敛这两条不变量做随机输入测试。PBT 很可能在现有代码里挖出真 bug（尤其在组件 id 511/512 边界、空 archetype、free list 耗尽、深层级拷贝），反向验证 Phase 1 的所有改动。

### Task 6 — 引入 FsCheck + 两条核心不变量 【2 人日，无门禁】

**Why**: 当前"1000 帧 fuzz"是固定序列循环，不是真 fuzz。serialization roundtrip 和 replay 收敛天生适合 PBT——任意输入都应满足不变量。FsCheck 会自动生成 boundary 输入找出现在手写用例覆盖不到的边角。

**Files**:
- 新增 `tests/MiniArch.Tests/PropertyBased/` 目录
- `tests/MiniArch.Tests/MiniArch.Tests.csproj`（加 FsCheck 依赖）
- 新增 `tests/MiniArch.Tests/PropertyBased/SerializationRoundtripPropertyTests.cs`
- 新增 `tests/MiniArch.Tests/PropertyBased/ReplayConvergencePropertyTests.cs`
- 新增 `tests/MiniArch.Tests/PropertyBased/Arbitraries/`（自定义 generator）

**Steps**:
1. 加 NuGet：
   ```xml
   <PackageReference Include="FsCheck" Version="3.0.0" />
   <PackageReference Include="FsCheck.Xunit" Version="3.0.0" />
   ```
   （执行者确认与 xUnit 2.5.3 / net8.0 兼容的最新 FsCheck 版本）
2. 写 generator（FsCheck Arbitrary）：
   - `GenComponentType`：生成 1..1024 范围组件 id（**故意覆盖 512 边界**，重点测慢路径）。
   - `GenComponentValue`：1/2/4/8/16/32 字节的 unmanaged 值（用 `record struct` 模板生成器）。
   - `GenWorld`：随机 Create N 个 entity，每个随机 attach 1..8 个组件，随机 AddChild 形成层级，随机 Set/Remove，随机 Destroy（含回收的 id 复用）。配置 `Arb.Default` + shrink 策略，让最小化用例可读。
3. **不变量 1（Serialization Roundtrip）**：
   ```csharp
   [Property(MaxTest = 500, QuietOnSuccess = true)]
   public void Snapshot_roundtrip_preserves_canonical_checksum(WorldModel model)
   {
       var w1 = model.Build();
       var ms = new MemoryStream();
       WorldSnapshot.Save(w1, ms);
       ms.Position = 0;
       var w2 = new World();
       WorldSnapshot.Load(w2, ms);
       Assert.Equal(w1.CanonicalChecksum(), w2.CanonicalChecksum());
   }
   ```
   注意：用 `CanonicalChecksum`（布局无关），**不要**用 `Checksum`（swap-remove 行序会影响）。
4. **不变量 2（Replay 收敛）**：
   ```csharp
   [Property(MaxTest = 200, QuietOnSuccess = true)]
   public void Two_worlds_replaying_same_delta_sequence_converge(WorldModel model, DeltaSequence seq)
   {
       var a = model.Build();
       var b = model.Build();   // 独立 clone，相同初始状态
       foreach (var delta in seq.Deltas)
       {
           a.Replay(delta);
           b.Replay(delta);
       }
       Assert.Equal(a.CanonicalChecksum(), b.CanonicalChecksum());
   }
   ```
   `DeltaSequence` 生成器需覆盖：placeholder mode（`DeferredEntities=true`）和 real-id mode 两种 wire，混合 Reserve/Create/Add/Set/Remove/AddChild/RemoveChild/Destroy，含同帧回收 id 复用。
5. **如果 PBT 报失败**：FsCheck 会 shrink 到最小反例。**不要**把失败用例改成 `[Fact] 跳过**，而是：
   - 如果是 PBT 测试自身的 generator bug → 修 generator。
   - 如果是库真 bug → **停下来，开新 Task 修库**，把最小反例转成 regression `[Fact]` 加进 `FrameDeltaDeterminismTests.cs` 或 `WorldSnapshotTests.cs`，再让 PBT 通过。
   - 这是本计划最重要的一条规则：PBT 找到的 bug 优先级高于本计划其他所有 Task。
6. 跑：`dotnet test --filter "FullyQualifiedName~PropertyBased"`，再跑全 `dotnet test` 确认无回归。

**DoD**:
- [ ] FsCheck 集成，两条 property 默认通过（500/200 次随机 + shrink）
- [ ] generator 覆盖组件 id 511/512 边界、空 archetype、深层级、id 回收
- [ ] **PBT 报告的每个库 bug 都有对应 regression `[Fact]`**（数量未知，可能是 0 也可能是几个）
- [ ] 全 `dotnet test` 绿
- [ ] 把 shrink 找到的任何反例写进 `.knowledge/kb-test-workflow.md` 的"已知边界"小节

**风险**:
- FsCheck 3.x API 与 xUnit 集成可能有版本坑，预留 0.5 人日调试。
- generator 设计是难点：`WorldModel` 既要可生成又要可 shrink。建议先写一个最小 `WorldModel`（只 Create+Set+Destroy，无层级）跑通 roundtrip property，再逐步加 AddChild/Remove/回收。**先窄后宽**。
- 若发现 5+ 个库 bug，本 Task 自动升级为多日任务，负责人应暂停后续 Phase 直到 PBT 全绿。

---

## Phase 3 — API 契约清理（第 3 周，~3 人日）

> 目的：把"release 静默 UB"和"Entity magic number 重载"两个 footgun 修掉。这两个都改 `src/MiniArch/`，每个 commit 都要跑门禁。

### Task 3 — `#if DEBUG` → `[Conditional("DEBUG")]` 【0.5 人日，**触发门禁**】

**Why**: `World.cs:122` 的 `ThrowIfDisposed` 以及 `Get<T>`/`GetRef<T>`（`World.cs:266`、`:286`）的内联 `#if DEBUG` 检查在 Release 下完全消失，访问已 Dispose 的 World 或过期 Entity 是静默 UB。改用 `[Conditional("DEBUG")]` 后行为不变（Release 仍不检查），但方法体在所有配置下都参与类型检查、分析器能看到调用、代码无 `#if` 噪音。

**Files**:
- `src/MiniArch/Core/World.cs:122`（`ThrowIfDisposed`）
- `src/MiniArch/Core/World.cs:266`（`Get<T>` 内联检查）
- `src/MiniArch/Core/World.cs:286`（`GetRef<T>` 内联检查）
- 全 `src/MiniArch/` grep `#if DEBUG` 找其他同类点：`rg -n "#if DEBUG" src/`

**Steps**:
1. 把 `ThrowIfDisposed` 改为：
   ```csharp
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   [Conditional("DEBUG")]
   private void ThrowIfDisposed()
   {
       if (_disposed) throw new ObjectDisposedException(nameof(World));
   }
   ```
   注意：**不要**加 `[DoesNotReturn]`——在 Release 下该方法会被移除调用，实际会"返回"，`[DoesNotReturn]` 会撒谎。
2. 抽出 `Get<T>`/`GetRef<T>` 的内联检查到 helper：
   ```csharp
   [Conditional("DEBUG")]
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   private void ValidateAlive(Entity entity)
   {
       if ((uint)entity.Id >= (uint)_entitySlotCount)
           throw new InvalidOperationException($"Entity {entity} is not alive.");
       ref var record = ref _records[entity.Id];
       if (!record.IsOccupied || record.Version != entity.Version)
           throw new InvalidOperationException($"Entity {entity} is not alive.");
   }
   ```
   在 `Get<T>`/`GetRef<T>` 入口调用 `ValidateAlive(entity);`，删除原内联 `#if DEBUG` 块。
3. 对 `rg` 找到的其他 `#if DEBUG` 点逐一评估，能转 `[Conditional("DEBUG")]` 的转，不能转的（如纯编译期常量分支）保留并加注释说明。
4. **关键验证**：分别在 `-c Debug` 和 `-c Release` 下 `dotnet test`，确认两份都绿。Debug 下验证逻辑生效（写一个故意访问 disposed world 的测试断言抛 `ObjectDisposedException`），Release 下确认行为不变。

**DoD**:
- [ ] `World.ThrowIfDisposed` / `ValidateAlive` 用 `[Conditional("DEBUG")]`
- [ ] `rg "#if DEBUG" src/` 命中数显著下降，剩余项有注释说明
- [ ] Debug + Release 双配置 `dotnet test` 全绿
- [ ] 新增 1 个 `[Fact]` 验证 Debug 下 disposed 抛异常（用 `#if DEBUG` 包裹测试本身）
- [ ] **门禁通过**（Release 下行为完全不变，吞吐应持平。若吞吐下降说明 `[Conditional]` 没生效，检查是否漏标 `[MethodImpl(AggressiveInlining)]`）

**风险**: 极低。`[Conditional]` 是编译期 call-site 移除，Release IL 行为与原 `#if DEBUG` 等价。门禁主要用来防意外。

---

### Task 4 — `Entity.IsPlaceholder` 属性 + 43 站点分类替换 【2-2.5 人日，**触发门禁**】

**Why**: 当前 `Entity` 有两种"无效"形态共用 Id 域：
- 哨兵 `Entity(-1, -1)`（`World.cs:601` `EnsurePlaceholderMap` 初始化未映射槽）
- placeholder `Entity(-1, seq≥0)`（lockstep 延迟创建）

代码里散布 43 处 `Id < 0` / `Id == -1` / `Id >= 0` 比较（见 `rg "\.Id < 0|\.Id == -1|\.Id >= 0" src/`），**三种语义混用**：
- (a) placeholder 检测：`Id == -1`（lockstep 上下文）
- (b) real-entity 守卫：`Id >= 0`（前面 (a) 的反）
- (c) 边界有效性：`Id < 0 || Id >= capacity`（这跟 placeholder **无关**，是无效 handle 检测）

一处把 (c) 误写成 placeholder 判断就是 bug。引入命名属性消灭歧义。

**Files**（执行者先跑 `rg -n "\.Id < 0|\.Id == -1|\.Id >= 0|Id < 0|Id == -1|Id >= 0" src/` 拿全量 43 站点清单）:
- `src/MiniArch/Core/Entity.cs`（加属性）
- `src/MiniArch/Core/CommandStream.cs`（~15 站点，多为 (a)/(b)）
- `src/MiniArch/Core/World.cs`（~8 站点，混合）
- `src/MiniArch/Core/HierarchyTable.cs`（~13 站点，多为 (c)）
- `src/MiniArch/Core/World.EntityLifecycle.cs`（~3 站点，多为 (c)）

**Steps**:
1. **先分类，后改**。把 43 站点填进一张表（建议在 commit message 或 PR 描述里附上），三列：`文件:行` / `原代码` / `语义类别 (a/b/c)`。这是本 Task 最重要的产出，**没分类完不要动代码**。
2. 在 `Entity.cs` 加属性：
   ```csharp
   public readonly record struct Entity(int Id, int Version) : IComparable<Entity>
   {
       public bool IsValid => Id >= 0 && Version > 0;

       /// <summary>
       /// 是否为 lockstep placeholder（延迟创建占位符）。Id == -1 且 Version >= 0。
       /// 与"未映射哨兵" Entity(-1,-1) 区别开：哨兵的 Version 为负。
       /// </summary>
       public bool IsPlaceholder => Id == -1 && Version >= 0;

       /// <summary>
       /// 是否为未映射哨兵（用于 placeholder→local 表的空槽）。Id == -1 且 Version < 0。
       /// </summary>
       public bool IsUnmappedSentinel => Id == -1 && Version < 0;
       // ... 其余不变
   }
   ```
3. **按类别替换**：
   - 类别 (a) placeholder 检测：`entity.Id < 0`（在 lockstep 上下文）→ `entity.IsPlaceholder`。**注意**：原代码用 `Id < 0` 而非 `Id == -1`，理论上 placeholder 的 Id 一定是 -1，但若存在 `Id < -1` 的路径需先确认。执行者对每个站点判断"这里 Id<0 是否等价于 Id==-1"。
   - 类别 (b) real-entity 守卫：`entity.Id >= 0`（前面有 placeholder 分支的）→ `!entity.IsPlaceholder && !entity.IsUnmappedSentinel`，或更简洁地保留 `entity.Id >= 0` 但**加注释**说明此处语义。建议优先保留 `Id >= 0` 不动，仅在新属性更可读的站点替换。
   - 类别 (c) 边界有效性：`entity.Id < 0 || entity.Id >= capacity` **不动**——这里关心的是数值范围，与 placeholder 无关。但可考虑加 `Entity.IsValid` 复用（仅当语义恰好是"handle 是否可能指向活动 entity"时）。
4. `World.cs:601` 的 `map[i] = new Entity(-1, -1);` 改为用命名构造或加注释引用 `IsUnmappedSentinel`。`World.cs:606` 的 `ResolveReplayEntity` 检查 `map[wireEntity.Version].Id < 0` → 改成 `.IsUnmappedSentinel || .IsPlaceholder`（语义：还没被映射）。
5. **分小 commit**：每改完一个文件单独 commit + 跑 `dotnet test` + 跑门禁。43 站点一次性改完再测一旦出错难定位。

**DoD**:
- [ ] 43 站点分类表附在 PR 描述里
- [ ] `Entity.IsPlaceholder` / `IsUnmappedSentinel` 属性加入并有 XML doc
- [ ] 类别 (a) 站点全部替换为 `IsPlaceholder`
- [ ] 类别 (c) 站点保持原样（或加注释）
- [ ] 全 `dotnet test` 绿
- [ ] **门禁通过**（属性内联与字段比较等价，吞吐应持平）
- [ ] 把分类表和决策同步进 `.knowledge/kb-core-ecs.md` 的 Entity 小节

**风险**:
- 中等。43 站点语义判断是本计划最大的体力活，也是最易出错处。**严禁** `replaceAll` 式批量替换，必须逐站点人工判断。建议执行者每改 5 站点跑一次测试，保持反馈环短。
- 若发现某站点 `Id < 0` 实际允许 `Id < -1`（即 Id 可以是任意负数，不限于 -1），则 `IsPlaceholder` 定义需要重新设计——执行者需先 grep `new Entity(-` 找出所有构造负 Id 的点确认。

---

## Phase 4 — 一致性收尾（第 4 周，~1.5 人日）

### Task 7 — `SpanFeeder` delegate → struct interface 【1-1.5 人日，**触发门禁**】

**Why**: `Archetype.Storage.cs:660` 定义了 `internal delegate void SpanFeeder(ReadOnlySpan<byte> span)`，被 `FeedColumnData`（:662）和 `FeedRowData`（:712）使用。库别处已用 `IChunkForEach` struct 接口实现零分配，但这里用 delegate 不一致。**更关键**：两个调用点 `WorldSnapshot.cs:190` 和 `:247` 传的是 `span => hash.AppendData(span)`，捕获了外层 `hash`（`IncrementalHash` 实例）→ **每次调用都是一次闭包分配**。这在 `ComputeChecksum` / `ComputeCanonicalChecksum` 的热路径上，与库"GC 0/0/0"的承诺直接矛盾。改成 struct 接口后真正零分配。

**Files**:
- `src/MiniArch/Core/Archetype.Storage.cs:660-723`（delegate + 两个方法签名）
- `src/MiniArch/Core/WorldSnapshot.cs:190` 和 `:247`（两个调用点）

**Steps**:
1. 在 `Archetype.Storage.cs` 加 struct 接口：
   ```csharp
   internal interface ISpanFeeder
   {
       void Feed(ReadOnlySpan<byte> span);
   }
   ```
2. 删 `internal delegate void SpanFeeder(ReadOnlySpan<byte> span);`。
3. `FeedColumnData` / `FeedRowData` 改泛型 + `where TFeeder : struct, ISpanFeeder`：
   ```csharp
   internal void FeedColumnData<TFeeder>(int columnIndex, int rowCount, ref TFeeder feeder)
       where TFeeder : struct, ISpanFeeder
   {
       // 方法体内把 `append(...)` 改成 `feeder.Feed(...)`，
       // 并注意 feeder 用 ref 传递避免 struct 拷贝（若 feeder 带字段）
   }
   ```
4. 在 `WorldSnapshot.cs` 定义专用 struct：
   ```csharp
   private readonly struct HashFeeder(IncrementalHash hash) : ISpanFeeder
   {
       public void Feed(ReadOnlySpan<byte> span) => hash.AppendData(span);
   }
   ```
   把 `:190` 和 `:247` 的 `span => hash.AppendData(span)` 改成：
   ```csharp
   var feeder = new HashFeeder(hash);
   arch.FeedColumnData(col, entityCount, ref feeder);
   ```
5. 确认 `IncrementalHash` 是引用类型（它是 class），所以 struct 持有它的字段是对堆对象的引用，struct 本身在栈上零分配。✓
6. 跑 `ComputeChecksum` 路径的零分配测试（库里已有 `Warmed_*_does_not_allocate` 模式，照着写一个 `Checksum_does_not_allocate`）：
   ```csharp
   [Fact]
   public void ComputeCanonicalChecksum_does_not_allocate()
   {
       // warmup
       world.CanonicalChecksum();
       var before = GC.GetAllocatedBytesForCurrentThread();
       world.CanonicalChecksum();
       var after = GC.GetAllocatedBytesForCurrentThread();
       Assert.Equal(0, after - before);
   }
   ```
   （注意：零分配测试需在专用线程跑，参考现有同类测试的线程设置。）

**DoD**:
- [ ] `SpanFeeder` delegate 删除，`ISpanFeeder` struct 接口加入
- [ ] `WorldSnapshot` 的两个 checksum 路径零分配测试通过
- [ ] 全 `dotnet test` 绿
- [ ] **门禁通过**（struct 接口 + `ref` 传递应比 delegate 调用更快或持平，吞吐不应下降）
- [ ] 同步更新 `.knowledge/kb-snapshot-persistence.md` 和 `kb-cache-optimization.md`（若涉及）

**风险**: 低-中。struct 接口 devirtualization 在 JIT 下表现好，但 `ref TFeeder` 传递语义要确认 feeder 在方法内被调用而不是被存储（这里只调用，安全）。若门禁显示吞吐反常下降，检查是否误把 feeder 存进了字段或 lambda。

---

## 推荐执行顺序与里程碑

| 周次 | Task | 累计人日 | 里程碑 |
|---|---|---|---|
| W1 D1 | Task 1 (isqrt) | 0.5 | demo 跨硬件确定性自洽 |
| W1 D2 | Task 2 (wire 预算) | 0.75 | wire OOM 攻击面关闭 |
| W1 D3 | Task 5 (CRC32) | 1.25 | 快照损坏可诊断 |
| W2 | Task 6 (PBT) | 3.25 | **关键里程碑**：随机测试覆盖，可能挖出 bug |
| W3 D1-2 | Task 3 (Conditional DEBUG) | 3.75 | API 契约诚实化 |
| W3 D3 - W4 D2 | Task 4 (Entity 属性) | 6.0 | magic number footgun 消除 |
| W4 D3-4 | Task 7 (SpanFeeder) | 7.25 | checksum 路径零分配自洽 |

**总计 ~7.25 人日**（不含 PBT 可能挖出 bug 的额外修复时间）。

**关键规则**：
1. **Task 6 优先级最高**——它可能反向影响其他 Task。若 W2 发现 3+ 个库 bug，暂停 W3/W4 直到 PBT 全绿。
2. **每个 src/ 改动单独 commit + 单独跑门禁**，绝不批量。
3. **Task 4 必须 43 站点分类表先行**，没分类完不动代码。
4. **门禁失败立即 `git stash` 回退**，按 `AGENTS.md §5b` 流程分析后重试，不要在失败基础上打补丁。

---

## 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| isqrt 改变 1000 帧 checksum 链 | Task 1 看似"破坏" demo | 预期内，commit body 记录新旧 checksum；若仍一致反而是 isqrt 写错的信号 |
| PBT 挖出大量库 bug | W2 工期爆炸 | 升级 Task 6 为多日，暂停后续 Phase；每个 bug 转 regression `[Fact]` |
| Task 4 漏判某站点语义 | 引入难查的 lockstep bug | 每 5 站点测一次，43 站点分多个 commit |
| Task 5 CRC 与现有流式 Save 冲突 | 需重构 Save 内部 buffer | 临时 MemoryStream 收 body，对低频 Save 可接受 |
| `miniArch` 与 `bullet-lockstep` 双仓漂移 | 同名代码两份不同步 | 每 Phase 末由负责人决定是否 cherry-pick 到另一仓 |
| isqrt 跨硬件仅在 CI 多架构矩阵才能真验证 | 本机跑 demo 证明不了跨硬件 | 在风险登记记下，建议加 GitHub Actions matrix（windows/linux/mac, x64/arm64）跑 demo checksum 对比——属"建议"非本计划必做 |

---

## Out of Scope（明确不做，留待后续立项）

以下是把分数从 94 推到 95+ 需要的大工程，**不在本计划范围**，执行者遇到相关诱惑应记录后跳过：

1. **AOT / trimming 适配**：`GenericMethodCache` + `WorldSnapshot.ColumnCodec` 的 `MakeGenericMethod` 反射路径需改为 source generator。这是一个独立的多周项目，需新增 NativeAOT 测试矩阵 + `[DynamicallyAccessedMembers]` 全量标注。
2. **并发模型重写**：`ReserveDeferredEntity` 拿锁但 `MaterializeReservedEntity` 写 `_records` 不拿锁的不一致，需要明确 happens-before 文档 + 可能引入 `Channel<T>` 或无锁队列。属于架构级改动。
3. **512-bit 掩码可生长化**：当前组件 id ≥ 512 走慢路径，改成 `BitSet` 式动态扩容会触及 hot path。需独立 benchmark。
4. **基准 CI / 历史趋势**：BenchmarkDotNet 接 GitHub Actions + 趋势仪表板 + 误差棒。属于工具链建设。
5. **late-join / 断线重连 / 丢包重排**：`kb-lockstep-playbook.md` 明确标 OUT OF SCOPE 的 netcode 完整方案。属产品级功能。

---

## 完成判据（整个计划 Done 的标准）

- [ ] 7 个 Task 全部 DoD 勾完
- [ ] 全仓库 `dotnet test` 在 Debug + Release 双配置下绿
- [ ] `HeroComing.Perf --check-baseline` 全程通过（无回退事件）
- [ ] PBT 两条 property 各跑 ≥500/200 次通过
- [ ] `.knowledge/` 相关页（kb-core-ecs, kb-snapshot-persistence, kb-test-workflow, kb-command-stream）已同步更新，`updated` 字段刷新，`INDEX.md` 仍准确
- [ ] PR 描述附：43 站点分类表、PBT shrink 找到的 bug 清单（若有）、baseline 改动前后对比
- [ ] 在 `.knowledge/INDEX.md` 的"重大变更摘要"加一条 `2026-07-xx 代码硬化`，列出 7 个 Task 一句话总结
