namespace MiniArch.Core;

public readonly record struct Entity(int Id, int Version)
{
    public bool IsValid => Id >= 0 && Version > 0;

    public bool MatchesVersion(int version) => Version == version;

    public override string ToString() => $"Entity({Id}, v{Version})";
}
