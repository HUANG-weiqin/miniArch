# FrameLookup Public API Design Draft

## 结论

发布候选 API 只做一件事：**在一个稳定 Snapshot 内，把 query rows 按 key 建成只读 lookup，然后按 key 直接消费 component rows**。

v1 不做 live cache，不做 key 枚举，不做 entity list API，不暴露 row/span/enumerator view。用户能拿到的是 `Count/Any` 和 `ForEach(key, ref structConsumer)`。这让生命周期契约最小化：`FrameLookup<TKey>` 每次 build 都替换上一帧派生结果；任何未来如果暴露 view，都必须带 generation 校验，但 v1 先不暴露。

## API 一句话定位

`FrameLookup<TKey>` = **key -> component rows**。

它不是 `key -> Entity list`，也不是 `World.Query` 的缓存层。它只是从 `Query.GetChunks()` 得到的当前 Snapshot 派生出一个本帧可复用的 row-ref 索引。

## 推荐 v1 形状

### Owned lookup

```csharp
public sealed class FrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    public int Version { get; }
    public int KeyCount { get; }
    public int RowCount { get; }

    public void Clear();
    public bool Any(TKey key);
    public int Count(TKey key);

    public void ForEach<T1, TConsumer>(TKey key, ref TConsumer consumer)
        where T1 : unmanaged
        where TConsumer : struct, IFrameRowConsumer<T1>;

    public void ForEach<T1, T2, TConsumer>(TKey key, ref TConsumer consumer)
        where T1 : unmanaged
        where T2 : unmanaged
        where TConsumer : struct, IFrameRowConsumer<T1, T2>;

    public void ForEach<T1, T2, T3, TConsumer>(TKey key, ref TConsumer consumer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TConsumer : struct, IFrameRowConsumer<T1, T2, T3>;
}
```

`FrameLookup<TKey>` 应是 class，而不是 struct：它持有多组可复用数组，高水位复用是核心价值；struct copy 会制造隐蔽别名和生命周期错误。builder 因此应接收 `FrameLookup<TKey> lookup`，不接收 `ref FrameLookup<TKey>`。

### Operators

```csharp
public interface IFrameKeySelector<TKey, T1>
    where TKey : unmanaged, IEquatable<TKey>
    where T1 : unmanaged
{
    TKey Select(Entity entity, in T1 c1);
}

public interface IFramePredicate<T1>
    where T1 : unmanaged
{
    bool Match(Entity entity, in T1 c1);
}

public interface IFrameRowConsumer<T1>
    where T1 : unmanaged
{
    void Accept(Entity entity, in T1 c1);
}
```

同名接口提供 2/3 component arity。所有 operator 都是 struct generic 参数，经 constrained generic call 调用；不存 interface 字段，不捕获 lambda。

### Builder

```csharp
Rows<T1>.From(query)
    .Where<TPredicate>()
    .KeyBy<TKey, TSelector>()
    .BuildAutoGrow(lookup);

bool ok = Rows<T1, T2>.From(query)
    .Where<TPredicate>()
    .KeyBy<TKey, TSelector>()
    .TryBuildNoGrow(lookup, out FrameLookupBuildResult result);
```

`From(query)` 接收 `MiniArch.Query`，不接收 `World`：query 已经封装 world 和 `QueryDescription`，这是当前 public batch API 的真实入口。builder 内部从 `query.GetChunks()` 读取 `ChunkView` snapshot，并把必要的 `ChunkView` 元数据复制到 lookup 自有数组；不复制组件 payload。

`Where<TPredicate>()` 可选；无 where 使用 `FramePassAll` 内部 struct。v1 候选 arity 为 `Rows<T1>`、`Rows<T1,T2>`、`Rows<T1,T2,T3>`，不超过 3。原因：ValueLab 已测 1-3，且足以覆盖 key component + 1/2 个读取组件；4+ 属于未证实扩展。

## Build 与失败语义

- `BuildAutoGrow(lookup)`：容量不足时增长 scratch/owned arrays；成功后发布完整结果。
- `TryBuildNoGrow(lookup, out result)`：容量不足返回 `false`，并把 lookup 置为空结果；不得发布部分 key 或部分 rows。
- `Clear()`：清空发布结果，保留容量，`Version++`。
- 每次成功 build 都 `Version++`，上一帧派生结果被替换。
- `FrameLookupBuildResult` 至少包含：`MatchedRows`、`StoredRows`、`DistinctKeys`、`MaxBucketSize`、`Resized`。

实现必须两阶段发布：先在 scratch/counts 中完成容量判断和填充，再原子地更新 published counts/version。NoGrow early/late fail 都走同一条 empty publish 路径。

## Consumption contract

- `ForEach` 按 Snapshot 扫描顺序消费同 key rows。
- consumer 参数使用 `in T`，表达只读；v1 不承诺 mutation 安全。
- `Entity` 作为 row 附带身份传给 consumer，方便诊断或少量外部映射；但 API 不提供 `CopyEntities`、`GetEntities` 或 entity-only lookup。
- `Any/Count` 可以发布：它们只读 key table，不暴露内部存储，不把 API 变成 entity list。

## 生命周期与禁区

`FrameLookup<TKey>` 的生命周期是：

1. World 进入一个调用方保证稳定的 Snapshot。
2. 调用方准备 `Query`。
3. `Rows<...>.From(query)...Build...` 扫描 chunks 并发布 lookup。
4. 本 Snapshot 内多次 `Any/Count/ForEach(key, ...)`。
5. Snapshot 结束后调用方可以 `Clear` 或下一帧 rebuild。

明确禁区：

- hot bucket：单 key 巨桶被重复查询会被大桶遍历吞掉收益。
- entity-only：v1 不优化纯 entity list；不要用它替代 `Dictionary<TKey,List<Entity>>` 的所有场景。
- low-Q：查询次数很小，build 成本摊不平。
- mutable World：build 后查询阶段修改 World 会让 row refs 指向旧 Snapshot，行为未定义。
- cross-frame freshness：lookup 不监听 World mutation，不自动刷新。

## 不进入生产的 ValueLab 原型

- `LinkedRowLookup<TKey>`：Build 快但读取跳跃，未找到发布胜出区间。
- `EntityArrayLookup<TKey>`：适合 entity-only/hot baseline，但不满足 component row 读取目标。
- `DenseIntCompactLookup`：保留为后续有界 int key 特化候选；不进通用 v1。
- 完整关系代数：GroupBy / Join / TopK / OrderBy / key enumeration / parallel build 全部不进 v1。

## 正确性测试草案

生产实现前必须至少覆盖：

- empty world。
- single/multi/missing/default key。
- multi archetype 与 chunked storage。
- Where 0/partial/100。
- 同 key scan order。
- `ForEach` component read 与 entity + `World.Get` 结果一致。
- Clear rebuild。
- AutoGrow 多次 resize。
- NoGrow early/late fail 均不发布部分结果。
- 1M smoke。

如果 v1 继续不暴露 view/enumerator/span，则 invalidation 测试应证明：Clear/Rebuild 后 `Any/Count/ForEach` 只看到新发布结果；没有旧 view 可被用户持有。

## 性能验收

进入 `src/MiniArch/` 前必须同时满足：

- Direct `ForEach` 形态不慢于 ValueLab `CompactRowDsl + CopyRowRefs + consume`。
- 相对 Dictionary component baseline 仍有明确收益。
- 高水位稳定后 build 与 query hot path 0B 分配。
- `--full` 覆盖 realistic、full-1m、hot 三类边界。
- `HeroComing.Perf --check-baseline` 在 production touched 后通过。
