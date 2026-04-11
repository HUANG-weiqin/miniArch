namespace MiniArch.Core;

public sealed class Query
{
    private readonly World _world;
    private readonly Signature _requiredSignature;
    private readonly List<Archetype> _matchingArchetypes = new();
    private int _cachedWorldGeneration = -1;

    internal Query(World world, Signature requiredSignature)
    {
        _world = world;
        _requiredSignature = requiredSignature;
    }

    public Signature RequiredSignature => _requiredSignature;

    public int RefreshCount { get; private set; }

    public IReadOnlyList<Archetype> MatchedArchetypes
    {
        get
        {
            RefreshIfNeeded();
            return _matchingArchetypes;
        }
    }

    public ChunkEnumerable Chunks => new(this);

    internal IReadOnlyList<Archetype> EnsureMatchingArchetypes()
    {
        RefreshIfNeeded();
        return _matchingArchetypes;
    }

    private void RefreshIfNeeded()
    {
        if (_cachedWorldGeneration == _world.ArchetypeGeneration)
        {
            return;
        }

        _matchingArchetypes.Clear();
        foreach (var archetype in _world.Archetypes)
        {
            if (Matches(archetype))
            {
                _matchingArchetypes.Add(archetype);
            }
        }

        _cachedWorldGeneration = _world.ArchetypeGeneration;
        RefreshCount++;
    }

    private bool Matches(Archetype archetype)
    {
        foreach (var component in _requiredSignature)
        {
            if (!archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        return true;
    }
}
