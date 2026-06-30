namespace MiniArch.Core;

internal readonly struct EntityInfo
{
    public int Version { get; }
    internal Archetype Archetype { get; }
    internal int RowIndex { get; }

    internal EntityInfo(int version, Archetype? archetype, int rowIndex)
    {
        Version = version;
        Archetype = archetype!;
        RowIndex = rowIndex;
    }
}
