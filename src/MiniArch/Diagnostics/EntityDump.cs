using MiniArch.Core;

namespace MiniArch.Diagnostics;

/// <summary>
/// Inspects a single entity's full state: alive status, version, archetype
/// membership, component values, hierarchy relations.
/// </summary>
public static class EntityDump
{
    /// <summary>
    /// Produces a full state report for <paramref name="entity"/> in <paramref name="world"/>.
    /// </summary>
    public static EntityReport Describe(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);

        var id = entity.Id;
        var records = world.EntityRecords;

        if ((uint)id >= (uint)records.Length)
        {
            return new EntityReport(false, id, entity.Version, null,
                Array.Empty<ComponentInfo>(), null, Array.Empty<Entity>());
        }

        var record = records[id];
        var version = record.Version;
        var isAlive = record.IsOccupied;

        if (!isAlive)
        {
            return new EntityReport(false, id, version, null,
                Array.Empty<ComponentInfo>(), null, Array.Empty<Entity>());
        }

        // Alive — read archetype info.
        var arch = record.Archetype!;
        var row = record.RowIndex;
        var archInfo = new ArchetypeInfo(arch.EntityCount, (IList<Type>)arch.ComponentTypes);

        // Component values.
        var components = new List<ComponentInfo>();
        var types = arch.ComponentTypes;
        for (var col = 0; col < types.Count; col++)
        {
            var bytes = arch.GetComponentBytes(col, row).ToArray();
            components.Add(new ComponentInfo(types[col], bytes.Length, bytes));
        }

        // Hierarchy.
        Entity? parent = null;
        if (world.TryGetParent(entity, out var p))
            parent = p;

        var children = new List<Entity>();
        foreach (var child in world.EnumerateChildren(entity))
            children.Add(child);

        return new EntityReport(true, id, version, archInfo, components, parent, children);
    }
}
