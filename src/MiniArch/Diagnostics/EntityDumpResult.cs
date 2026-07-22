using System.Collections.ObjectModel;

namespace MiniArch.Diagnostics;

/// <summary>
/// Full state report for a single entity. Returned by <see cref="EntityDump.Describe"/>.
/// </summary>
public readonly struct EntityReport
{
    /// <summary>Whether the entity is currently alive.</summary>
    public bool IsAlive { get; }

    /// <summary>The entity's ID (slot index).</summary>
    public int Id { get; }

    /// <summary>The entity's version (incremented on destroy/recycle).</summary>
    public int Version { get; }

    /// <summary>Archetype info, null when the entity is dead.</summary>
    public ArchetypeInfo? Archetype { get; }

    /// <summary>Component list, empty when the entity is dead.</summary>
    public ReadOnlyCollection<ComponentInfo> Components { get; }

    /// <summary>Parent entity, null when no parent or entity is dead.</summary>
    public Entity? Parent { get; }

    /// <summary>Child entities, empty when no children or entity is dead.</summary>
    public ReadOnlyCollection<Entity> Children { get; }

    internal EntityReport(
        bool isAlive, int id, int version,
        ArchetypeInfo? archetype,
        IList<ComponentInfo> components,
        Entity? parent,
        IList<Entity> children)
    {
        IsAlive = isAlive;
        Id = id;
        Version = version;
        Archetype = archetype;
        Components = new ReadOnlyCollection<ComponentInfo>(components);
        Parent = parent;
        Children = new ReadOnlyCollection<Entity>(children);
    }

    /// <summary>
    /// Returns a human-readable multi-line dump of the entity state.
    /// Component values are shown as hex bytes; use <see cref="System.Runtime.InteropServices.MemoryMarshal"/>
    /// to reinterpret where the type is known.
    /// </summary>
    public override string ToString()
    {
        if (!IsAlive)
            return $"Entity #{Id} (v{Version}) — DEAD";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Entity #{Id} (v{Version}) — ALIVE");
        if (Archetype.HasValue)
        {
            var a = Archetype.Value;
            sb.AppendLine($"  Archetype: [{string.Join(", ", a.ComponentTypes)}] ({a.EntityCount} entities)");
        }
        if (Components.Count > 0)
        {
            sb.AppendLine("  Components:");
            foreach (var c in Components)
            {
                var rawBytes = c.RawBytes;
                var hex = rawBytes is not null
                    ? BitConverter.ToString(rawBytes).Replace("-", " ")
                    : "(unreadable)";
                sb.AppendLine($"    {c.Type.Name} ({c.SizeBytes} B): {hex}");
            }
        }
        if (Parent.HasValue)
            sb.AppendLine($"  Parent: {Parent.Value}");
        if (Children.Count > 0)
            sb.AppendLine($"  Children: {string.Join(", ", Children)}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Summary of an archetype's state.</summary>
public readonly struct ArchetypeInfo
{
    /// <summary>Number of living entities in this archetype.</summary>
    public int EntityCount { get; }

    /// <summary>Component types that define this archetype's signature.</summary>
    public ReadOnlyCollection<Type> ComponentTypes { get; }

    internal ArchetypeInfo(int entityCount, IReadOnlyList<Type> componentTypes)
    {
        EntityCount = entityCount;
        ComponentTypes = new ReadOnlyCollection<Type>(new List<Type>(componentTypes));
    }
}

/// <summary>Single component value on an entity.</summary>
public readonly struct ComponentInfo
{
    private readonly byte[]? _rawBytes;

    /// <summary>The component type.</summary>
    public Type Type { get; }

    /// <summary>Size in bytes (0 for zero-sized components).</summary>
    public int SizeBytes { get; }

    /// <summary>Gets a defensive copy of the component value bytes. Null when unreadable.</summary>
    public byte[]? RawBytes => _rawBytes is null ? null : (byte[])_rawBytes.Clone();

    internal ComponentInfo(Type type, int sizeBytes, byte[]? rawBytes)
    {
        Type = type;
        SizeBytes = sizeBytes;
        _rawBytes = rawBytes;
    }
}
