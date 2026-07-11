// Layout presets for FrameReadModel experiments.
// Each entry describes a combination of component model + query pattern.
// Full layout definitions will be populated in later tasks.
// This file is a placeholder — types exist to satisfy the skeleton contract.

namespace FrameReadModels.ValueLab;

/// <summary>
/// Identifies a component layout preset used in benchmark scenarios.
/// </summary>
internal enum FrameReadModelLayout
{
    /// <summary>Single component (Position only).</summary>
    SinglePosition,

    /// <summary>Two components (Position + Velocity).</summary>
    PositionVelocity,

    /// <summary>Three or more components (Position + Velocity + …).</summary>
    Mixed,

    /// <summary>One-hot: many component types with a single entity each.</summary>
    OneHot,
}

/// <summary>
/// Describes the shape of a layout: entity count, component model, archetype count.
/// </summary>
internal readonly record struct LayoutDescription(
    FrameReadModelLayout Layout,
    int EntityCount,
    int ArchetypeCount);
