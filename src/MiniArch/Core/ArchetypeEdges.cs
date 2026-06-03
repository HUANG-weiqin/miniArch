namespace MiniArch.Core;

/// <summary>
/// Caches adjacent archetypes.
/// </summary>
internal sealed class ArchetypeEdges
{
    private MigrationPlan?[] _addEdges = Array.Empty<MigrationPlan?>();
    private MigrationPlan?[] _removeEdges = Array.Empty<MigrationPlan?>();

    /// <summary>
    /// Tries to get the add edge for a component.
    /// </summary>
    internal bool TryGetAdd(ComponentType component, out MigrationPlan? plan) => TryGet(_addEdges, component, out plan);

    /// <summary>
    /// Tries to get the remove edge for a component.
    /// </summary>
    internal bool TryGetRemove(ComponentType component, out MigrationPlan? plan) => TryGet(_removeEdges, component, out plan);

    /// <summary>
    /// Caches an add edge.
    /// </summary>
    internal void CacheAdd(ComponentType component, MigrationPlan plan) => Cache(ref _addEdges, component, plan);

    /// <summary>
    /// Caches a remove edge.
    /// </summary>
    internal void CacheRemove(ComponentType component, MigrationPlan plan) => Cache(ref _removeEdges, component, plan);

    private static bool TryGet(MigrationPlan?[] edges, ComponentType component, out MigrationPlan? plan)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)edges.Length)
        {
            plan = null;
            return false;
        }

        plan = edges[componentId];
        return plan is not null;
    }

    private static void Cache(ref MigrationPlan?[] edges, ComponentType component, MigrationPlan plan)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)edges.Length)
        {
            Array.Resize(ref edges, componentId + 1);
        }

        edges[componentId] = plan;
    }
}
