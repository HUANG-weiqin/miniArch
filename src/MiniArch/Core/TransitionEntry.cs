namespace MiniArch.Core;

internal readonly struct TransitionEntry
{
    public readonly Entity Entity;
    public readonly Archetype? OldArchetype;   // null = created
    public readonly Archetype? NewArchetype;   // null = destroyed

    public TransitionEntry(Entity e, Archetype? old, Archetype? @new)
    {
        Entity = e;
        OldArchetype = old;
        NewArchetype = @new;
    }
}
