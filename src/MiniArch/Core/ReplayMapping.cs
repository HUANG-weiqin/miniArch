namespace MiniArch;

public readonly struct ReplayMapping
{
    private readonly Entity[] _map;
    private readonly int _count;

    internal ReplayMapping(Entity[] map, int count)
    {
        _map = map;
        _count = count;
    }

    public Entity Resolve(Entity placeholder)
    {
        if (!placeholder.IsPlaceholder)
            return placeholder;
        var seq = placeholder.Version;
        if ((uint)seq >= (uint)_count)
            return default;
        var mapped = _map[seq];
        return mapped.IsUnmappedSentinel ? default : mapped;
    }

    public ReplayMapping Frozen()
    {
        var copy = new Entity[_count];
        Array.Copy(_map, copy, _count);
        return new ReplayMapping(copy, _count);
    }
}
