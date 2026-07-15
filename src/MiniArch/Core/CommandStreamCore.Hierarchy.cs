using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

public abstract partial class CommandStreamCore
{
    /// <summary>
    /// Core AddChild logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>AddChild</c> method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void AddChildCore(Entity parent, Entity child)
        => _frozen.HierarchyByChild[child] = new HierarchyIntent(true, parent);

    /// <summary>
    /// Core RemoveChild logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>RemoveChild</c> method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void RemoveChildCore(Entity child)
        => _frozen.HierarchyByChild[child] = new HierarchyIntent(false, default);

    private void ApplyHierarchy()
    {
        ApplyHierarchyToWorld(_world, _frozen);
    }

    private static void PreflightHierarchyOverlay(World world, FrozenState frozen)
    {
        if (frozen.HierarchyByChild.Count == 0)
            return;

        var maxSteps = Math.Max(
            1,
            world.EntitySlotCount + frozen.PendingBatchCount + frozen.HierarchyByChild.Count + 1);

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (IsDestroyedThisFrame(child, frozen))
                continue;

            if (!WillExistAfterMaterialization(world, frozen, child))
                throw new InvalidOperationException($"Child {child} is stale or unknown.");

            if (!intent.IsAdd)
                continue;

            var parent = intent.Parent;
            if (IsDestroyedThisFrame(parent, frozen))
                continue;

            if (!WillExistAfterMaterialization(world, frozen, parent))
                throw new InvalidOperationException($"Parent {parent} is stale or unknown.");
            if (parent == child)
                throw new InvalidOperationException("Parent and child must be different entities.");

            var current = parent;
            for (var steps = 0; ; steps++)
            {
                if (current == child)
                {
                    throw new InvalidOperationException(
                        "Parent-child relations must not create cycles.");
                }
                if (steps >= maxSteps)
                {
                    throw new InvalidOperationException(
                        "Hierarchy cycle detected while validating the final command overlay.");
                }

                if (!TryGetOverlayParent(world, frozen, current, out current))
                    break;
            }
        }
    }

    private static bool TryGetOverlayParent(
        World world,
        FrozenState frozen,
        Entity child,
        out Entity parent)
    {
        if (frozen.HierarchyByChild.TryGetValue(child, out var intent) &&
            !IsDestroyedThisFrame(child, frozen))
        {
            if (!intent.IsAdd)
            {
                parent = default;
                return false;
            }

            if (!IsDestroyedThisFrame(intent.Parent, frozen))
            {
                parent = intent.Parent;
                return true;
            }
        }

        return world.TryGetParent(child, out parent);
    }

    private static bool WillExistAfterMaterialization(
        World world,
        FrozenState frozen,
        Entity entity)
    {
        if (world.IsAlive(entity))
            return true;

        for (var i = 0; i < frozen.PendingBatchCount; i++)
        {
            if (!frozen.BatchCanceled[i] && frozen.BatchEntities[i] == entity)
                return true;
        }

        return false;
    }

    // An entity is excluded from hierarchy application when it is scheduled for
    // destruction this frame. Two sources cover all cases:
    //   1. DestroyEntities[]  — non-pending entities that had Destroy() called.
    //   2. BatchCanceled[]    — pending/placeholder entities cancelled by Destroy().
    // CancelledBatchCount provides a fast-path: when no batches were cancelled
    // (the common case), the BatchCanceled scan is skipped entirely.
    // Additionally, CancelPendingDescendants removes hierarchy entries for
    // cancelled parents at record time, so surviving entries are rare.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDestroyedThisFrame(Entity entity, FrozenState frozen)
    {
        for (var i = 0; i < frozen.DestroyCount; i++)
            if (frozen.DestroyEntities[i] == entity) return true;
        if (frozen.CancelledBatchCount == 0) return false;
        for (var i = 0; i < frozen.PendingBatchCount; i++)
            if (frozen.BatchCanceled[i] && frozen.BatchEntities[i] == entity) return true;
        return false;
    }

    private static void EmitHierarchyToDelta(FrameDelta delta, FrozenState frozen)
    {
        var hierarchyByChild = frozen.HierarchyByChild;
        if (hierarchyByChild.Count == 0) return;

        var count = hierarchyByChild.Count;
        var sorted = ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Rent(count);
        try
        {
            ((ICollection<KeyValuePair<Entity, HierarchyIntent>>)hierarchyByChild).CopyTo(sorted, 0);
            Array.Sort(sorted, 0, count, HierarchyComparer.Instance);

            for (var i = 0; i < count; i++)
            {
                ref readonly var entry = ref sorted[i];
                var (child, intent) = (entry.Key, entry.Value);
                if (IsDestroyedThisFrame(child, frozen)) continue;
                if (intent.IsAdd)
                {
                    if (IsDestroyedThisFrame(intent.Parent, frozen)) continue;
                    delta.AddAddChild(intent.Parent, child);
                }
                else
                {
                    delta.AddRemoveChild(child);
                }
            }
        }
        finally
        {
            Array.Clear(sorted, 0, count); // clear refs before returning to pool
            ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Return(sorted);
        }
    }

    private sealed class HierarchyComparer : IComparer<KeyValuePair<Entity, HierarchyIntent>>
    {
        public static readonly HierarchyComparer Instance = new();
        public int Compare(KeyValuePair<Entity, HierarchyIntent> x, KeyValuePair<Entity, HierarchyIntent> y)
            => x.Key.Id.CompareTo(y.Key.Id);
    }

    private static void ApplyHierarchyToWorld(World world, FrozenState frozen)
    {
        if (frozen.HierarchyByChild.Count == 0) return;

        var count = frozen.HierarchyByChild.Count;
        var sorted = ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Rent(count);
        try
        {
            ((ICollection<KeyValuePair<Entity, HierarchyIntent>>)frozen.HierarchyByChild).CopyTo(sorted, 0);
            Array.Sort(sorted, 0, count, HierarchyComparer.Instance);

            for (var i = 0; i < count; i++)
            {
                ref readonly var entry = ref sorted[i];
                var (child, intent) = (entry.Key, entry.Value);
                if (IsDestroyedThisFrame(child, frozen)) continue;

                if (intent.IsAdd)
                {
                    if (IsDestroyedThisFrame(intent.Parent, frozen)) continue;
                    world.AddChild(intent.Parent, child);
                }
                else
                {
                    world.RemoveChild(child);
                }
            }
        }
        finally
        {
            ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Return(sorted);
        }
    }

    // When a pending entity is destroyed before Submit, all hierarchy entries
    // referencing it as a parent must be cleaned up — the entity will never be
    // materialized. Pending children are cancelled recursively; existing children
    // simply have their AddChild intent removed.
    private void CancelPendingDescendants(Entity root)
    {
        if (_frozen.HierarchyByChild.Count == 0) return;

        // BFS through ALL descendants. We must snapshot children before
        // calling CancelPendingEntity because that mutates _frozen.HierarchyByChild.
        var queue = ArrayPool<Entity>.Shared.Rent(16);
        var queueCount = 0;
        try
        {
            EnqueueAllChildren(root, ref queue, ref queueCount);

            var head = 0;
            while (head < queueCount)
            {
                var current = queue[head++];
                if (TryGetPendingBatch(current, out _))
                {
                    CancelPendingEntity(current);
                    EnqueueAllChildren(current, ref queue, ref queueCount);
                }
                else
                {
                    // Existing child of a cancelled pending parent: remove the
                    // AddChild intent since the parent will never materialize.
                    _frozen.HierarchyByChild.Remove(current);
                }
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(queue);
        }
    }

    private void EnqueueAllChildren(Entity parent, ref Entity[] queue, ref int queueCount)
    {
        foreach (var (child, intent) in _frozen.HierarchyByChild)
        {
            if (!intent.IsAdd || intent.Parent != parent) continue;
            if (queueCount == queue.Length)
                GrowPooled(ref queue, queueCount);
            queue[queueCount++] = child;
        }
    }

    private void ReplaceHierarchyPlaceholders(Entity[] resolveMap)
    {
        if (_frozen.HierarchyByChild.Count == 0)
            return;

        var count = _frozen.HierarchyByChild.Count;
        var replacements = ArrayPool<HierarchyReplacement>.Shared.Rent(count);
        var repCount = 0;
        try
        {
            foreach (var (child, intent) in _frozen.HierarchyByChild)
            {
                var newChild = child;
                if (child.IsPlaceholder)
                {
                    var resolved = resolveMap[child.Version];
                    if (resolved.Id >= 0) newChild = resolved;
                }
                var newParent = intent.Parent;
                if (intent.IsAdd && intent.Parent.IsPlaceholder)
                {
                    var resolved = resolveMap[intent.Parent.Version];
                    if (resolved.Id >= 0) newParent = resolved;
                }

                if (newChild != child || (intent.IsAdd && newParent != intent.Parent))
                {
                    replacements[repCount++] = new HierarchyReplacement(
                        child, newChild, intent.IsAdd, newParent);
                }
            }

            for (var i = 0; i < repCount; i++)
            {
                ref var r = ref replacements[i];
                _frozen.HierarchyByChild.Remove(r.OldChild);
                if (r.IsAdd)
                    _frozen.HierarchyByChild[r.NewChild] = new HierarchyIntent(true, r.Parent);
                else
                    _frozen.HierarchyByChild[r.NewChild] = new HierarchyIntent(false, default);
            }
        }
        finally
        {
            ArrayPool<HierarchyReplacement>.Shared.Return(replacements);
        }
    }

    private struct HierarchyReplacement
    {
        public Entity OldChild;
        public Entity NewChild;
        public bool IsAdd;
        public Entity Parent;

        public HierarchyReplacement(Entity oldChild, Entity newChild, bool isAdd, Entity parent)
        {
            OldChild = oldChild;
            NewChild = newChild;
            IsAdd = isAdd;
            Parent = parent;
        }
    }

    internal readonly record struct HierarchyIntent(bool IsAdd, Entity Parent);
}
