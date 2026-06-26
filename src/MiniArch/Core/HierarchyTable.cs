using System.Runtime.CompilerServices;

namespace MiniArch.Core;

internal sealed class HierarchyTable
{
    private const int NoSlot = -1;

    private static readonly Entity NoEntity = default;
    private Entity[] _parentByChild = [];
    private int[] _firstChild = [];
    private Entity[] _childEntity = [];
    private int[] _childNext = [];
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

    public void Link(World world, Entity parent, Entity child)
        => LinkCore(world, parent, child, unlinkFirst: true);

    public void LinkRestored(World world, Entity parent, Entity child)
        => LinkCore(world, parent, child, unlinkFirst: false);

    private void LinkCore(World world, Entity parent, Entity child, bool unlinkFirst)
    {
        ValidateLink(world, parent, child);

        EnsureCapacity(parent.Id);
        EnsureCapacity(child.Id);
        if (unlinkFirst) Unlink(child);

        _parentByChild[child.Id] = parent;
        AddChildToParent(parent.Id, child);
    }

    public void Unlink(Entity child)
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

    public List<Entity> GetChildren(World world, Entity parent)
    {
        var result = new List<Entity>();
        foreach (var child in EnumerateChildren(world, parent))
        {
            result.Add(child);
        }

        return result;
    }

    /// <summary>
    /// Returns a zero-allocation enumerable over the live children of
    /// <paramref name="parent"/>.  Prefer this over <see cref="GetChildren"/>
    /// in hot paths to avoid List allocation.
    /// </summary>
    internal ChildrenEnumerable EnumerateChildren(World world, Entity parent)
    {
        return new ChildrenEnumerable(this, world, parent);
    }

    internal readonly struct ChildrenEnumerable
    {
        private readonly HierarchyTable _table;
        private readonly World _world;
        private readonly int _firstSlot;

        internal ChildrenEnumerable(HierarchyTable table, World world, Entity parent)
        {
            _table = table;
            _world = world;
            _firstSlot = world.IsAlive(parent) && parent.Id >= 0 && parent.Id < table._firstChild.Length
                ? table._firstChild[parent.Id]
                : NoSlot;
        }

        public ChildrenEnumerator GetEnumerator() => new(_table, _world, _firstSlot);
    }

    internal struct ChildrenEnumerator
    {
        private readonly HierarchyTable _table;
        private readonly World _world;
        private int _slot;

        internal ChildrenEnumerator(HierarchyTable table, World world, int firstSlot)
        {
            _table = table;
            _world = world;
            _slot = firstSlot;
            Current = default;
        }

        public Entity Current { get; private set; }

        public bool MoveNext()
        {
            while (_slot >= 0)
            {
                var slot = _slot;
                _slot = _table._childNext[slot];
                var child = _table._childEntity[slot];
                if (!_world.IsAlive(child)) continue;

                Current = child;
                return true;
            }

            return false;
        }
    }

    public bool HasChildren(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _firstChild.Length)
        {
            return false;
        }

        return _firstChild[entity.Id] >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAnyLinks(Entity entity)
    {
        if ((uint)entity.Id >= (uint)_parentByChild.Length)
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
        // don't need to clear it here on entry — except on the very first call,
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
                    var child = _childEntity[slot];
                    slot = _childNext[slot];

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
            var child = _childEntity[slot];
            var next = _childNext[slot];

            if (child.Id >= 0 && child.Id < _parentByChild.Length && _parentByChild[child.Id] == entity)
            {
                _parentByChild[child.Id] = NoEntity;
            }

            FreeChildSlot(slot);
            slot = next;
        }
    }

    public int CountLiveLinks(World world)
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

    public IEnumerable<(Entity Child, Entity Parent)> EnumerateLiveLinks(World world)
    {
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
                yield return (child, parent);
            }
        }
    }

    private int AllocateChildSlot()
    {
        if (_childFreeList >= 0)
        {
            var slot = _childFreeList;
            _childFreeList = _childNext[slot];
            return slot;
        }

        if (_childSlotCount == _childEntity.Length)
        {
            var newCapacity = _childEntity.Length == 0 ? 16 : _childEntity.Length * 2;
            Array.Resize(ref _childEntity, newCapacity);
            Array.Resize(ref _childNext, newCapacity);
        }

        return _childSlotCount++;
    }

    private void FreeChildSlot(int slot)
    {
        _childEntity[slot] = default;
        _childNext[slot] = _childFreeList;
        _childFreeList = slot;
    }

    private void AddChildToParent(int parentId, Entity child)
    {
        var slot = AllocateChildSlot();
        _childEntity[slot] = child;
        _childNext[slot] = _firstChild[parentId];
        _firstChild[parentId] = slot;
    }

    private void RemoveChildFromParent(int parentId, Entity child)
    {
        var prev = NoSlot;
        var slot = _firstChild[parentId];
        while (slot >= 0)
        {
            if (_childEntity[slot] == child)
            {
                if (prev < 0)
                {
                    _firstChild[parentId] = _childNext[slot];
                }
                else
                {
                    _childNext[prev] = _childNext[slot];
                }

                FreeChildSlot(slot);
                return;
            }

            prev = slot;
            slot = _childNext[slot];
        }
    }

    private void ValidateLink(World world, Entity parent, Entity child)
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
                throw new InvalidOperationException("Hierarchy links must not create cycles.");
            }

            if (!TryGetParent(world, current, out current))
            {
                break;
            }
        }
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
        _destroyTraversalStack.EnsureCapacity(newLength);
    }
}
