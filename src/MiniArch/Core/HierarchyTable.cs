using System.Runtime.CompilerServices;

namespace MiniArch.Core;

internal sealed class HierarchyTable
{
    private const int NoSlot = -1;

    private static readonly Entity NoEntity = default;
    private Entity[] _parentByChild = [];
    private int[] _firstChild = [];
    private ChildSlot[] _childSlots = [];
    private int _childSlotCount;
    private int _childFreeList = NoSlot;
    private readonly List<(Entity Entity, bool Expanded)> _destroyTraversalStack = new(4);

    public void Reset()
    {
        Array.Fill(_parentByChild, NoEntity);
        Array.Fill(_firstChild, NoSlot);
        _childSlotCount = 0;
        _childFreeList = NoSlot;
    }

    public void AddChild(World world, Entity parent, Entity child)
        => AddChildCore(world, parent, child, removeFirst: true);

    public void AddChildRestored(World world, Entity parent, Entity child)
        => AddChildCore(world, parent, child, removeFirst: false);

    private void AddChildCore(World world, Entity parent, Entity child, bool removeFirst)
    {
        ValidateAddChild(world, parent, child);

        EnsureCapacity(parent.Id);
        EnsureCapacity(child.Id);
        if (removeFirst) RemoveChild(child);

        _parentByChild[child.Id] = parent;
        AddChildToParent(parent.Id, child);
    }

    public void RemoveChild(Entity child)
    {
        if (child.Id < 0 || child.Id >= _parentByChild.Length)
        {
            return;
        }

        var parent = _parentByChild[child.Id];
        if (parent == NoEntity)
        {
            return;
        }

        if (parent.Id >= 0 && parent.Id < _firstChild.Length)
        {
            RemoveChildFromParent(parent.Id, child);
        }

        _parentByChild[child.Id] = NoEntity;
    }

    public bool TryGetParent(World world, Entity child, out Entity parent)
    {
        if (!world.IsAlive(child) || child.Id < 0 || child.Id >= _parentByChild.Length)
        {
            parent = NoEntity;
            return false;
        }

        parent = _parentByChild[child.Id];
        if (parent == NoEntity || !world.IsAlive(parent))
        {
            parent = NoEntity;
            return false;
        }

        return true;
    }

    internal ChildrenEnumerable EnumerateChildren(World world, Entity parent)
    {
        var firstSlot = world.IsAlive(parent) && parent.Id >= 0 && parent.Id < _firstChild.Length
            ? _firstChild[parent.Id]
            : NoSlot;
        return new ChildrenEnumerable(_childSlots, world, firstSlot);
    }

    public bool HasChildren(World world, Entity entity)
    {
        if (!world.IsAlive(entity))
            return false;
        if (entity.Id < 0 || entity.Id >= _firstChild.Length)
            return false;
        return _firstChild[entity.Id] >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAnyRelations(Entity entity)
    {
        if ((uint)entity.Id >= (uint)_parentByChild.Length ||
            (uint)entity.Id >= (uint)_firstChild.Length)
        {
            return false;
        }

        return _parentByChild[entity.Id] != NoEntity || _firstChild[entity.Id] != NoSlot;
    }

    public void CollectDestroySubtree(World world, Entity root, int[] visitedGen, int currentGen, List<Entity> destroyOrder)
    {
        if (!world.IsAlive(root))
        {
            return;
        }

        if (root.Id >= 0 && root.Id < visitedGen.Length && visitedGen[root.Id] == currentGen)
        {
            return;
        }

        if (root.Id >= 0 && root.Id < visitedGen.Length)
        {
            visitedGen[root.Id] = currentGen;
        }

        // The finally block below guarantees the stack is empty on exit, so we
        // don't need to clear it here on entry —except on the very first call,
        // when the stack is already empty by construction.
        _destroyTraversalStack.Add((root, false));

        try
        {
            while (_destroyTraversalStack.Count > 0)
            {
                var lastIndex = _destroyTraversalStack.Count - 1;
                var (entity, expanded) = _destroyTraversalStack[lastIndex];
                _destroyTraversalStack.RemoveAt(lastIndex);
                if (expanded)
                {
                    destroyOrder.Add(entity);
                    continue;
                }

                _destroyTraversalStack.Add((entity, true));
                if (entity.Id < 0 || entity.Id >= _firstChild.Length)
                {
                    continue;
                }

                var slot = _firstChild[entity.Id];
                while (slot >= 0)
                {
                    var child = _childSlots[slot].Entity;
                    slot = _childSlots[slot].Next;

                    if (!world.IsAlive(child))
                    {
                        continue;
                    }

                    if (child.Id >= 0 && child.Id < visitedGen.Length)
                    {
                        if (visitedGen[child.Id] == currentGen)
                        {
                            continue;
                        }

                        visitedGen[child.Id] = currentGen;
                    }

                    _destroyTraversalStack.Add((child, false));
                }
            }
        }
        finally
        {
            _destroyTraversalStack.Clear();
        }
    }

    public void RemoveDestroyed(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _parentByChild.Length)
        {
            return;
        }

        var parent = _parentByChild[entity.Id];
        if (parent != NoEntity && parent.Id >= 0 && parent.Id < _firstChild.Length)
        {
            RemoveChildFromParent(parent.Id, entity);
        }

        _parentByChild[entity.Id] = NoEntity;

        var slot = _firstChild[entity.Id];
        _firstChild[entity.Id] = NoSlot;

        while (slot >= 0)
        {
            var child = _childSlots[slot].Entity;
            var next = _childSlots[slot].Next;

            if (child.Id >= 0 && child.Id < _parentByChild.Length && _parentByChild[child.Id] == entity)
            {
                _parentByChild[child.Id] = NoEntity;
            }

            FreeChildSlot(slot);
            slot = next;
        }
    }

    public int CountLiveRelations(World world)
    {
        var count = 0;
        var slotCount = world.EntitySlotCount;
        for (var childId = 0; childId < slotCount && childId < _parentByChild.Length; childId++)
        {
            var parent = _parentByChild[childId];
            if (parent == NoEntity)
            {
                continue;
            }

            var child = new Entity(childId, world.GetEntityVersion(childId));
            if (world.IsAlive(child) && world.IsAlive(parent))
            {
                count++;
            }
        }

        return count;
    }

    public LiveRelationEnumerable EnumerateLiveRelations(World world)
        => new(this, world);

    public readonly struct LiveRelationEnumerable(HierarchyTable table, World world)
    {
        private readonly HierarchyTable _table = table;
        private readonly int _limit = Math.Min(world.EntitySlotCount, table._parentByChild.Length);

        public Enumerator GetEnumerator() => new(_table, _limit, world);

        public struct Enumerator
        {
            private readonly HierarchyTable _table;
            private readonly World _world;
            private readonly int _limit;
            private int _childId;

            internal Enumerator(HierarchyTable table, int limit, World world)
            {
                _table = table;
                _limit = limit;
                _world = world;
                _childId = -1;
            }

            public (Entity Child, Entity Parent) Current { get; private set; }

            public bool MoveNext()
            {
                _childId++;
                while (_childId < _limit)
                {
                    var parent = _table._parentByChild[_childId];
                    if (parent != NoEntity)
                    {
                        var child = new Entity(_childId, _world.GetEntityVersion(_childId));
                        if (_world.IsAlive(child) && _world.IsAlive(parent))
                        {
                            Current = (child, parent);
                            return true;
                        }
                    }
                    _childId++;
                }
                return false;
            }
        }
    }

    private int AllocateChildSlot()
    {
        if (_childFreeList >= 0)
        {
            var slot = _childFreeList;
            _childFreeList = _childSlots[slot].Next;
            return slot;
        }

        if (_childSlotCount == _childSlots.Length)
        {
            var newCapacity = _childSlots.Length == 0 ? 16 : _childSlots.Length * 2;
            Array.Resize(ref _childSlots, newCapacity);
        }

        return _childSlotCount++;
    }

    private void FreeChildSlot(int slot)
    {
        _childSlots[slot].Entity = default;
        _childSlots[slot].Next = _childFreeList;
        _childFreeList = slot;
    }

    private void AddChildToParent(int parentId, Entity child)
    {
        var slot = AllocateChildSlot();
        _childSlots[slot].Entity = child;
        _childSlots[slot].Next = _firstChild[parentId];
        _firstChild[parentId] = slot;
    }

    private void RemoveChildFromParent(int parentId, Entity child)
    {
        var prev = NoSlot;
        var slot = _firstChild[parentId];
        while (slot >= 0)
        {
            if (_childSlots[slot].Entity == child)
            {
                if (prev < 0)
                {
                    _firstChild[parentId] = _childSlots[slot].Next;
                }
                else
                {
                    _childSlots[prev].Next = _childSlots[slot].Next;
                }

                FreeChildSlot(slot);
                return;
            }

            prev = slot;
            slot = _childSlots[slot].Next;
        }
    }

    private void ValidateAddChild(World world, Entity parent, Entity child)
    {
        if (!world.IsAlive(parent))
        {
            throw new InvalidOperationException($"Parent {parent} is stale or unknown.");
        }

        if (!world.IsAlive(child))
        {
            throw new InvalidOperationException($"Child {child} is stale or unknown.");
        }

        if (parent == child)
        {
            throw new InvalidOperationException("Parent and child must be different entities.");
        }

        var current = parent;
        while (current != NoEntity)
        {
            if (current == child)
            {
                throw new InvalidOperationException("Parent-child relations must not create cycles.");
            }

            if (!TryGetParent(world, current, out current))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Directly assigns a parent to a child without validation.
    /// TEST-ONLY: used to inject cycles for diagnostics validation.
    /// </summary>
    internal void SetParentForTest(Entity child, Entity parent)
    {
        EnsureCapacity(child.Id);
        EnsureCapacity(parent.Id);
        _parentByChild[child.Id] = parent;
    }

    internal void CaptureState(WorldStateSnapshot snapshot)
    {
        snapshot.EnsureHierarchyCapacity(_parentByChild.Length, _childSlotCount);

        Array.Copy(_parentByChild, snapshot.HierarchyParentByChild, _parentByChild.Length);
        Array.Copy(_firstChild, snapshot.HierarchyFirstChild, _firstChild.Length);
        Array.Copy(_childSlots, snapshot.HierarchyChildSlots, _childSlotCount);

        snapshot.HierarchyChildSlotCount = _childSlotCount;
        snapshot.HierarchyChildFreeList = _childFreeList;
    }

    internal void RestoreState(WorldStateSnapshot snapshot)
    {
        if (_parentByChild.Length != snapshot.HierarchyParentByChild.Length)
        {
            _parentByChild = new Entity[snapshot.HierarchyParentByChild.Length];
            _firstChild = new int[snapshot.HierarchyFirstChild.Length];
        }
        Array.Copy(snapshot.HierarchyParentByChild, _parentByChild, snapshot.HierarchyParentByChild.Length);
        Array.Copy(snapshot.HierarchyFirstChild, _firstChild, snapshot.HierarchyFirstChild.Length);

        if (_childSlots.Length < snapshot.HierarchyChildSlotCount)
            _childSlots = new ChildSlot[snapshot.HierarchyChildSlotCount];
        if (snapshot.HierarchyChildSlotCount > 0)
            Array.Copy(snapshot.HierarchyChildSlots, _childSlots, snapshot.HierarchyChildSlotCount);
        _childSlotCount = snapshot.HierarchyChildSlotCount;
        _childFreeList = snapshot.HierarchyChildFreeList;
    }

    private void EnsureCapacity(int entityId)
    {
        if (entityId < _parentByChild.Length)
        {
            return;
        }

        var newLength = Math.Max(entityId + 1, Math.Max(4, _parentByChild.Length * 2));
        var previousLength = _parentByChild.Length;
        Array.Resize(ref _parentByChild, newLength);
        Array.Resize(ref _firstChild, newLength);
        Array.Fill(_parentByChild, NoEntity, previousLength, newLength - previousLength);
        Array.Fill(_firstChild, NoSlot, previousLength, newLength - previousLength);
    }
}
