using System.Collections.ObjectModel;

namespace MiniArch.Diagnostics;

/// <summary>
/// Outcome of <see cref="WorldValidator.Validate"/>. Check <see cref="IsValid"/> first,
/// then iterate <see cref="Issues"/> for details.
/// </summary>
public readonly struct ValidationResult
{
    /// <summary>True when no issues were found.</summary>
    public bool IsValid { get; }

    /// <summary>All detected issues (empty when <see cref="IsValid"/> is true).</summary>
    public ReadOnlyCollection<ValidationIssue> Issues { get; }

    internal ValidationResult(List<ValidationIssue> issues)
    {
        Issues = Array.AsReadOnly(issues.ToArray());
        IsValid = issues.Count == 0;
    }
}

/// <summary>Severity of a validation issue.</summary>
public enum ValidationSeverity { Error, Warning }

/// <summary>Which subsystem the issue belongs to.</summary>
public enum ValidationCategory { EntitySlot, FreeList, Hierarchy, Archetype }

/// <summary>Specific invariant that was violated.</summary>
public enum ValidationCode
{
    OrphanedSlot,
    SlotCollision,
    FreeListOccupied,
    FreeListDuplicate,
    AsymmetricParent,
    OrphanedChild,
    ArchetypeEntityCount,
    DuplicateEntityId,
    SlotCapacityWarning,

    /// <summary>A parent-child cycle was detected in the hierarchy.</summary>
    HierarchyCycle,
}

/// <summary>A single validation finding.</summary>
public readonly struct ValidationIssue
{
    public ValidationSeverity Severity { get; }
    public ValidationCategory Category { get; }
    public ValidationCode Code { get; }
    public string Description { get; }

    internal ValidationIssue(ValidationSeverity severity, ValidationCategory category,
        ValidationCode code, string description)
    {
        Severity = severity;
        Category = category;
        Code = code;
        Description = description;
    }

    public override string ToString() => $"[{Severity}] {Category}.{Code}: {Description}";
}
