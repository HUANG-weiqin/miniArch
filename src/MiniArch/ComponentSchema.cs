using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Component registry snapshot for debugging and version-compatibility checks.
/// </summary>
/// <remarks>
/// <see cref="Fingerprint"/> captures a SHA-256 hash of the current
/// <see cref="ComponentRegistry"/> state (the id→type mapping at call time).
/// Its primary use is as a <b>development/debugging tool</b> to detect when
/// two processes are running incompatible code —not a required part of the
/// lockstep protocol.
/// <para/>
/// For deterministic lockstep (same binary), both peers execute the same code
/// paths, so component registration is inherently identical at every logical
/// frame. No registry check is needed at connection time; runtime divergence
/// is detected by per-frame <c>World.Checksum()</c>.
/// <para/>
/// When debugging cross-version compatibility, <see cref="Fingerprint"/> can
/// confirm that two processes share the same registry state —but only
/// captures types registered up to the call point. Conditional or lazy-loaded
/// component types will not appear in the fingerprint until they are first
/// used.
/// </remarks>
public static class ComponentSchema
{
    /// <summary>
    /// Computes a SHA-256 fingerprint of the global component registry,
    /// capturing the current id→type assignment at call time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fingerprint is <b>order-dependent</b>: it captures not just which
    /// types are registered but in which order, because
    /// <see cref="Core.FrameDelta"/> encodes component ids as process-local
    /// integers.
    /// </para>
    /// <para>
    /// Call this only when you need to verify registry compatibility between
    /// two processes (e.g., during development when investigating a divergence,
    /// or as a build-version sanity check). It reflects whatever has been
    /// registered so far —types registered lazily after the call won't be
    /// included.
    /// </para>
    /// <code>
    /// var mine = ComponentSchema.Fingerprint();
    /// // compare with another process's fingerprint
    /// if (!mine.AsSpan().SequenceEqual(theirs))
    ///     Console.WriteLine("Registry mismatch —different builds?");
    /// </code>
    /// </remarks>
    /// <returns>32-byte SHA-256 fingerprint.</returns>
    public static byte[] Fingerprint() => ComponentRegistry.Shared.GetFingerprint();
}
