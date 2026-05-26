namespace MiniArch.Core;

internal sealed class HierarchyTable
{
    private static readonly Entity NoEntity = default;
    private Entity[] _parentByChild = [];
    private HashSet<Entity>?[] _childrenByParent = [];
    private readonly List<(Entity Entity, bool Expanded)> _destroyTraversalStack = new(4);

    public void Reset()
    {
        Array.Fill(_parentByChild, NoEntity);
        Array.Clear(_childrenByParent);
    }

    public void Link(World world, Entity parent, Entity child)
    {
        ValidateLink(world, parent, child);

        EnsureCapacity(parent.Id);
        EnsureCapacity(child.Id);
        Unlink(child);

        _parentByChild[child.Id] = parent;
        var children = _childrenByParent[parent.Id] ??= new HashSet<Entity>(4);
        children.Add(child);
    }

    public void LinkRestored(Entity parent, Entity child)
    {
        EnsureCapacity(parent.Id);
        EnsureCapacity(child.Id);

        _parentByChild[child.Id] = parent;
        var children = _childrenByParent[parent.Id] ??= new HashSet<Entity>(4);
        children.Add(child);
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

        if (parent.Id >= 0 && parent.Id < _childrenByParent.Length)
        {
            _childrenByParent[parent.Id]?.Remove(child);
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
        if (!world.IsAlive(parent) || parent.Id < 0 || parent.Id >= _childrenByParent.Length)
        {
            return [];
        }

        var children = _childrenByParent[parent.Id];
        if (children is null || children.Count == 0)
        {
            return [];
        }

        var result = new List<Entity>(children.Count);
        foreach (var child in children)
        {
            if (world.IsAlive(child))
            {
                result.Add(child);
            }
        }

        result.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return result;
    }

    public bool HasChildren(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _childrenByParent.Length)
        {
            return false;
        }

        var children = _childrenByParent[entity.Id];
        return children is not null && children.Count > 0;
    }

    public void CollectDestroySubtree(World world, Entity root, HashSet<Entity> visited, List<Entity> destroyOrder)
    {
        if (!world.IsAlive(root) || !visited.Add(root))
        {
            return;
        }

        _destroyTraversalStack.Clear();
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
                if (entity.Id < 0 || entity.Id >= _childrenByParent.Length)
                {
                    continue;
                }

                var children = _childrenByParent[entity.Id];
                if (children is null)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (world.IsAlive(child) && visited.Add(child))
                    {
                        _destroyTraversalStack.Add((child, false));
                    }
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
        if (parent != NoEntity && parent.Id >= 0 && parent.Id < _childrenByParent.Length)
        {
            _childrenByParent[parent.Id]?.Remove(entity);
        }

        _parentByChild[entity.Id] = NoEntity;

        var children = _childrenByParent[entity.Id];
        if (children is null)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child.Id >= 0 && child.Id < _parentByChild.Length && _parentByChild[child.Id] == entity)
            {
                _parentByChild[child.Id] = NoEntity;
            }
        }

        children.Clear();
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
        Array.Resize(ref _childrenByParent, newLength);
        Array.Fill(_parentByChild, NoEntity, previousLength, newLength - previousLength);
        _destroyTraversalStack.EnsureCapacity(newLength);
    }
}
