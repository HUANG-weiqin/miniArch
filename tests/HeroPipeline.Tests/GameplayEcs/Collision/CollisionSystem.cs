using System;
using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using MiniArch;
using MiniArch.Core;

namespace Hero.GameplayEcs.Collision;

/// <summary>
/// Batch collision system: groups collision requests by (targetQ, targetR, shape, range, filter),
/// scans candidates once per group, and creates hit requests for all requests in the group.
/// </summary>
public sealed class CollisionSystem : ISystem
{
    private static readonly MiniArch.QueryDescription CollisionRequestDescription = new MiniArch.QueryDescription()
        .With<CollisionRequest>()
        .With<Validated>();

    private static readonly MiniArch.QueryDescription CollisionTargetDescription = new MiniArch.QueryDescription()
        .With<PositionQValue>()
        .With<PositionRValue>();

    private readonly Dictionary<GroupKey, List<CollisionRequestData>> _groups = new(4);
    private readonly List<List<CollisionRequestData>> _groupListPool = new(4);

    public void Execute(in FrameContext context)
    {
        FrameView frame = context.Frame;
        CommandBuffer commands = context.Commands;

        ResetGroups();
        CollectGroups(frame);

        // Pass 2: scan candidates once per group, create hit requests.
        foreach (var (key, requests) in _groups)
        {
            ProcessGroup(frame, commands, in key, requests);
        }

        // Pass 3: destroy all processed collision requests.
        DestroyAll(frame, commands);
    }

    private void CollectGroups(FrameView frame)
    {
        foreach (var chunk in frame.ChunkQuery(CollisionRequestDescription).GetChunks())
        {
            ReadOnlySpan<Entity> entities = chunk.GetEntities();
            ReadOnlySpan<RequestTarget> targets = chunk.GetSpan<RequestTarget>();
            ReadOnlySpan<CollisionShape> shapes = chunk.GetSpan<CollisionShape>();
            ReadOnlySpan<CollisionTargetQValue> targetQs = chunk.GetSpan<CollisionTargetQValue>();
            ReadOnlySpan<CollisionTargetRValue> targetRs = chunk.GetSpan<CollisionTargetRValue>();
            ReadOnlySpan<CollisionRange> ranges = chunk.GetSpan<CollisionRange>();
            ReadOnlySpan<CollisionFilter> filters = chunk.GetSpan<CollisionFilter>();
            ReadOnlySpan<HitConsequenceRuleId> consequenceIds = chunk.GetSpan<HitConsequenceRuleId>();

            for (int i = 0; i < chunk.Count; i++)
            {
                var data = new CollisionRequestData(
                    entities[i],
                    targets[i],
                    shapes[i],
                    targetQs[i],
                    targetRs[i],
                    ranges[i],
                    filters[i],
                    consequenceIds[i]);

                var key = new GroupKey(data.TargetQ.Value, data.TargetR.Value, data.Shape.Value, data.Range.Value, data.Filter.Value);
                if (!_groups.TryGetValue(key, out var list))
                {
                    list = RentGroupList();
                    _groups[key] = list;
                }
                list.Add(data);
            }
        }
    }

    private void ResetGroups()
    {
        foreach (var list in _groups.Values)
        {
            list.Clear();
            _groupListPool.Add(list);
        }

        _groups.Clear();
    }

    private List<CollisionRequestData> RentGroupList()
    {
        int count = _groupListPool.Count;
        if (count == 0)
            return new List<CollisionRequestData>(16);

        var list = _groupListPool[count - 1];
        _groupListPool.RemoveAt(count - 1);
        return list;
    }

    private static void ProcessGroup(FrameView frame, CommandBuffer commands, in GroupKey key, List<CollisionRequestData> requests)
    {
        foreach (var chunk in frame.ChunkQuery(CollisionTargetDescription).GetChunks())
        {
            ReadOnlySpan<Entity> candidates = chunk.GetEntities();
            ReadOnlySpan<PositionQValue> qs = chunk.GetSpan<PositionQValue>();
            ReadOnlySpan<PositionRValue> rs = chunk.GetSpan<PositionRValue>();

            for (int i = 0; i < chunk.Count; i++)
            {
                if (!MatchesShape(key.Shape, key.TargetQ, key.TargetR, key.Range, qs[i].Value, rs[i].Value))
                    continue;

                Entity candidate = candidates[i];

                if (!MatchesFilter(frame, candidate, key.Filter))
                    continue;

                foreach (CollisionRequestData req in requests)
                {
                    CreateHitRequest(commands, in req, candidate);
                }
            }
        }
    }

    private static void DestroyAll(FrameView frame, CommandBuffer commands)
    {
        foreach (var chunk in frame.ChunkQuery(CollisionRequestDescription).GetChunks())
        {
            ReadOnlySpan<Entity> entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                commands.Destroy(entities[i]);
            }
        }
    }

    private static void CreateHitRequest(CommandBuffer commands, in CollisionRequestData req, Entity candidate)
    {
        Entity hitRequest = commands.Create();
        commands.Add(hitRequest, new Request());
        commands.Add(hitRequest, new CollisionHitRequest());
        commands.Add(hitRequest, new RequestTarget(candidate));
        commands.Add(hitRequest, req.ConsequenceRuleId.Value);
        commands.Add(hitRequest, RuleTier.Normal);
        commands.Add(hitRequest, new CollisionSource(req.RequestTarget.Target));
        commands.Add(hitRequest, new CollisionTarget(candidate));
        commands.Add(hitRequest, req.ConsequenceRuleId);
    }

    private static bool MatchesShape(CollisionShapeKind shape, int targetQ, int targetR, int range, int positionQ, int positionR)
    {
        return shape switch
        {
            CollisionShapeKind.Tile => positionQ == targetQ && positionR == targetR,
            CollisionShapeKind.Area => HexGeometry.Distance(targetQ, targetR, positionQ, positionR) <= range,
            _ => throw new InvalidOperationException($"Unsupported collision shape '{shape}'."),
        };
    }

    private static bool MatchesFilter(FrameView frame, Entity candidate, CollisionFilterKind filter)
    {
        return filter switch
        {
            CollisionFilterKind.Any => true,
            CollisionFilterKind.CurrentHp => frame.TryGet(candidate, out CurrentHpValue _),
            _ => throw new InvalidOperationException($"Unsupported collision filter '{filter}'."),
        };
    }

    private readonly record struct GroupKey(int TargetQ, int TargetR, CollisionShapeKind Shape, int Range, CollisionFilterKind Filter);

    private readonly record struct CollisionRequestData(
        Entity Entity,
        RequestTarget RequestTarget,
        CollisionShape Shape,
        CollisionTargetQValue TargetQ,
        CollisionTargetRValue TargetR,
        CollisionRange Range,
        CollisionFilter Filter,
        HitConsequenceRuleId ConsequenceRuleId);
}
