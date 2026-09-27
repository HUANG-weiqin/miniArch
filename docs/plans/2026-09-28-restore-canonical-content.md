# 回退后 canonical 内容一致与零分配（M1、M2）

**状态：** 待执行（2026-09-28 作者确认两项都做）
**建议版本：** MiniArch 6.1.0。含空原型的 World，其 `CanonicalChecksum` 数值和存档字节会变；最终版本号由作者定。
**下游：** Roug2 2.0 计划的 P5 依赖本版本（`E:\godot\roug2\docs\plans\2026-09-28-roug2-2.0-world-only.md`）。

## 结论

两处小改动，目的相同：让 `RestoreState` 退回后的 World，在“内容”和“稳态分配”上都与从未回退过的 World 一样。

- **M1**：canonical 内容只描述非空原型，与 `Checksum()` 一致。
- **M2**：`RestoreState` 不再让“创建实体用的原型缓存”失效。

## M1：canonical 内容排除空原型

### 现状

- `Checksum()` 跳过空原型：`WorldSnapshot.cs` 的 `BuildChecksumArchetypes`，`if (archetype.EntityCount == 0) continue;`。
- `Save` 的 v5 payload 与 `CanonicalChecksum()` 所用的 canonical layout 不跳过空原型：`WorldSnapshot.cs` 约 222–271 行遍历全部 `world.Archetypes`，schema 也按全部原型的组件类型收集。
- `RestoreState` 只把各原型的实体数清零，不删除原型（`World.cs` `RestoreState`）。

因此，快照之后才出现的原型在退回后会以空原型的形式留在 World 里。两个内容完全相同的 World，只因为其中一个“曾经有过”某种组件组合，`CanonicalChecksum` 就不同。不经过回退也会出现这种情况，比如创建某种组合的实体后又把它们全部销毁。

`.knowledge/kb-snapshot-persistence.md` 第 145 行把这种现象写成了预期行为，并建议回退对拍改用 `Checksum()`。作者已裁定这不是有意的设计。

### 改法

- canonical layout 只收录 `EntityCount > 0` 的原型；schema 只收录这些原型实际用到的组件类型；payload 与 `CanonicalChecksum()` 保持一致。
- 检查 checksum breakdown（PerArchetype / PerComponent / Total 等）的口径是否与此一致，不一致就一起改。
- 读旧存档必须仍然可用：旧版 v5 payload 里带有空原型，照常加载，不报错。

### 执行时必须核实（有疑问就停下上报）

1. **读档后的查询顺序。** 4.1.0 起库级保证了读档后查询迭代顺序的确定性。如果这个保证依赖 payload 里的空原型，就停下上报，不要硬改。
2. **其他存储形状字段。** canonical 内容里还有别的字段可能被回退历史改变，比如 chunk capacity、只增不减的缓冲容量。逐项确认它们不会因为“曾经扩容过”而让回退后的 World 和从未回退的 World 不同。如果会，列出来上报，不要顺手扩大范围。

### 测试

- 拍快照 → 用一种新的组件组合创建实体 → 退回：`CanonicalChecksum` 等于拍快照前的值，也等于一个从未见过该组合的新 World 的值。
- 两个 World 内容相同，但其中一个曾经创建又销毁过某种组合：两者的 `CanonicalChecksum` 相同，`Save` 出的字节也相同。
- 带空原型的旧 payload 能正常加载。
- 既有的存档往返、读档后查询顺序、跨注册顺序等测试全部保持通过。

## M2：`RestoreState` 不再让创建缓存失效

### 现状

`World.cs` 的 `RestoreState` 末尾执行了 `_createArchetypeCacheGeneration++`（约 1472 行）。

原型对象在退回时都还留在 `_archetypes` 里，缓存指向的原型依然有效。但缓存被整体作废以后，退回后每种组件组合第一次 `Create` 都会重新分配一个 `CachedCreateArchetype`，外加两个 `WeakReference`（`World.Create.Generated.cs` 约 474–480 行）。于是悔棋、预览、AI 推演这类“拍快照 → 走几步 → 退回”的循环，每一轮都会产生垃圾。

World 整体清空的路径（约 1521–1539 行，执行了 `_archetypes.Clear()`）确实需要让缓存失效，保持不变。

### 改法

删除 `RestoreState` 中那一处缓存代数的递增。只要原型对象会被删除或替换，其余失效点一律保留。

### 测试

- 预热后，“拍快照 → 用多种组合 Create/Destroy → 退回 → 再 Create”循环的稳态分配为 0 字节（`GC.GetAllocatedBytesForCurrentThread`）。
- 退回后，用快照之后才出现的组合 Create，实体会进入正确的原型，数据正确。
- World 整体清空之后，缓存仍然会失效，不会指向已删除的原型。
- 如果零分配测试暴露出 `RestoreState` 链路上的其他分配，列出来上报，不在本计划里顺手修。

## 文档

- `.knowledge/kb-snapshot-persistence.md`：删除“canonical 包含空原型，回退对拍用 `Checksum()`”的说法，改为“canonical 只含非空原型，回退后与从未回退的 World 一致”；在 M2 相关位置注明退回后创建缓存仍然有效；更新 `updated`，确认 `.knowledge/INDEX.md` 仍然准确。
- 同步修改 `docs/api.md` 中 `CanonicalChecksum` 的说明和 Rollback 段落，以及 README 里对应的说法。
- `CHANGELOG.md` 写清楚：含空原型的 World，其 canonical 校验和数值与存档字节会变化；旧存档仍可读取。

## 门禁

- `dotnet build` 与 `dotnet test` 全绿（Release 与 Debug）。
- 这次改动涉及 `src/MiniArch/`，属于架构变更，必须跑 `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`，不能走豁免。
- 按仓库既有的发布流程把新版本打包到 `artifacts/nuget`。Roug2 的 package smoke 从这里取包。

## 停止条件

- 读档后的查询顺序或其他库级保证依赖空原型。
- 发现 canonical 内容里还有别的字段会被回退历史改变。
- 零分配测试暴露出 M2 之外的分配来源。
- 需要改动公开 API。
