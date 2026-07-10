# ManagedEntityMap ValueLab

## 结论先行

本实验默认从 **No-Go** 出发：`Entity -> managed object` sidecar 只有在正确性、性能、API/序列化价值上明显优于合理用户手写实现时，才值得进入 miniArch。后续 benchmark/harness 必须同时比较 naive、competent、unsafe upper-bound 与候选库原型，不能只打败 `Dictionary<Entity,T>`。

## 实现模型

| 实现 | 核心形态 | 正确性策略 | 预期性能 | 代码复杂度 | 用途 |
|---|---|---|---|---|---|
| Naive Dictionary | `Dictionary<Entity,T>` | 依赖完整 entity handle 作 key，不主动清理 zombie | 最慢：hash + 16B key + stale entry 保留 | 最低：约 5-10 行 | strawman，只证明最常见写法的问题 |
| Competent Dictionary | `Dictionary<int, Entry<T>>`，Entry 保存 version/value | `world.IsAlive(entity)` + id/version 校验 + remove/align | 中等：hash int key，安全检查成本可接受 | 中等：约 35-50 行 | 必须击败的合理用户基线 |
| Raw Dense Array Unsafe | `T?[] values + int[] versions` | 只按 id/version 命中，不完整查 `world.IsAlive` | 最高或接近最高：数组直索引 | 中低：约 25-35 行 | 性能上界/错误示例，不是安全 API |
| Competent Dense User | `T?[] values + int[] versions + World` | `world.IsAlive(entity)` 是唯一 liveness oracle；支持 `Align/Remove` | 接近候选库；安全检查成本清晰 | 高：约 50-80 行，隐含坑多 | 真正需要证明“库有价值”的对手 |
| Proposed Library Map | dense arrays + `World` + 最小 API | 与 competent dense 相同，但把坑封装进库 | 应接近 competent dense，明显快于 dictionary | 用户侧最低；库侧承担复杂度 | 候选 Go 方案 |

## 正确性矩阵

| 场景 | Naive Dictionary | Competent Dictionary | Raw Dense Unsafe | Competent Dense User | Proposed Library Map |
|---|---:|---:|---:|---:|---:|
| Destroy 后、Align 前不能读 zombie | ❌ 保留旧 key/value | ✅ `IsAlive` false | ❌ 若只看 version 可能误读 | ✅ | ✅ |
| Destroy + slot reuse 后旧 handle 不能读旧/新对象 | ✅ 旧 handle key 不等于新 handle，但旧对象滞留 | ✅ | ✅/⚠️ 取决于 version 检查 | ✅ | ✅ |
| 新 handle 未 Set 前不能误读旧 slot 对象 | ✅ | ✅ | ⚠️ 若 versions 未清/未比对会错 | ✅ | ✅ |
| `Remove(entity)` 只删 sidecar，不影响 World | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Align()` 后 zombie 引用可被 GC | ❌ 无 Align | ✅ 枚举清理 | ✅/⚠️ 取决于实现 | ✅ | ✅ |
| `Set(entity, null)` 策略明确 | ⚠️ 容易把 null 当 value | ✅ 可禁止 | ⚠️ 容易混淆 absence | ✅ 可禁止 | ✅ 禁止 |
| placeholder / negative id / out-of-range 安全失败 | ✅ hash key 安全但语义无效 | ✅ 依赖 `IsAlive` | ❌ 容易数组越界 | ✅ | ✅ |
| 跨 World entity handle | ❌ 无法检测 | ⚠️ `IsAlive` false，但错误来源不清 | ❌/⚠️ | ⚠️ 文档定义为调用方错误 | ⚠️ 文档定义为调用方错误 |

## 默认 Go / No-Go 门槛

1. `Get/TryGet/Set` 在 100k live entities 下必须明显快于 `Dictionary<int, Entry<T>>` 合理实现；如果性能不明显，必须有更强正确性或序列化价值补足。
2. 安全库版相对 raw dense unsafe 的慢幅应可解释；若 >20%，默认 No-Go 或缩窄 API。
3. 如果 competent dense user 在 <50 行内就能达到同等正确性与性能，默认 No-Go。
4. 如果 serialization 不能成为清晰价值主张，v1 不纳入 serialization，只评估 runtime host-local map。

## API/语义边界

- host-local / optional / non-deterministic / non-thread-safe。
- 不参与 lockstep、FrameDelta、Snapshot、Checksum、Replay。
- `world.IsAlive(entity)` 是唯一 liveness oracle；sidecar 自己的 version 只表示 map slot 绑定状态。
- `Align()` 只清理 zombie，不补齐、不扫描 World 生成内容。
- `Set(entity, null)` 禁止；null 表示 absence。
- 跨 World handle 定义为调用方错误；v1 不承诺检测。
