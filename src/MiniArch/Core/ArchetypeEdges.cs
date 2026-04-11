namespace MiniArch.Core;

public sealed class ArchetypeEdges
{
    private readonly Dictionary<ComponentType, Archetype> _addEdges = new();
    private readonly Dictionary<ComponentType, Archetype> _removeEdges = new();

    public bool TryGetAdd(ComponentType component, out Archetype? archetype) => _addEdges.TryGetValue(component, out archetype);

    public bool TryGetRemove(ComponentType component, out Archetype? archetype) => _removeEdges.TryGetValue(component, out archetype);

    public void CacheAdd(ComponentType component, Archetype archetype) => _addEdges[component] = archetype;

    public void CacheRemove(ComponentType component, Archetype archetype) => _removeEdges[component] = archetype;
}
