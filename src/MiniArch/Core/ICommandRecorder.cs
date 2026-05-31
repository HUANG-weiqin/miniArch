namespace MiniArch.Core;

/// <summary>Recording-only interface shared by command buffer implementations.</summary>
public interface ICommandRecorder
{
    /// <summary>Records an entity creation.</summary>
    Entity Create();
    /// <summary>Records an add command.</summary>
    void Add<T>(Entity entity, T component);
    /// <summary>Records a set command.</summary>
    void Set<T>(Entity entity, T component);
    /// <summary>Records a remove command.</summary>
    void Remove<T>(Entity entity);
    /// <summary>Records a destroy command.</summary>
    void Destroy(Entity entity);
    /// <summary>Records a deep clone of an entity and its entire child subtree. Command buffers snapshot the source at record time.</summary>
    Entity Clone(Entity source);
    /// <summary>Records a parent link.</summary>
    void Link(Entity parent, Entity child);
    /// <summary>Records a parent unlink.</summary>
    void Unlink(Entity child);
}
