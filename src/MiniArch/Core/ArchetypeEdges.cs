namespace MiniArch.Core;

public sealed class ArchetypeEdges
{
    private Archetype?[] _addEdges = Array.Empty<Archetype?>();
    private Archetype?[] _removeEdges = Array.Empty<Archetype?>();

    public bool TryGetAdd(ComponentType component, out Archetype? archetype) => TryGet(_addEdges, component, out archetype);

    public bool TryGetRemove(ComponentType component, out Archetype? archetype) => TryGet(_removeEdges, component, out archetype);

    public void CacheAdd(ComponentType component, Archetype archetype) => Cache(ref _addEdges, component, archetype);

    public void CacheRemove(ComponentType component, Archetype archetype) => Cache(ref _removeEdges, component, archetype);

    private static bool TryGet(Archetype?[] edges, ComponentType component, out Archetype? archetype)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)edges.Length)
        {
            archetype = null;
            return false;
        }

        archetype = edges[componentId];
        return archetype is not null;
    }

    private static void Cache(ref Archetype?[] edges, ComponentType component, Archetype archetype)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)edges.Length)
        {
            Array.Resize(ref edges, componentId + 1);
        }

        edges[componentId] = archetype;
    }
}
