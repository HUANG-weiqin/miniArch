using System.IO;
using System.Text;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Component registry introspection, portable schema export/import, and
/// version-compatibility checks for cross-process scenarios.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Fingerprint"/> captures a SHA-256 hash of the current
/// registry state — useful as a build-level sanity check during development.
/// </para>
/// <para>
/// <see cref="Export"/> / <see cref="Import"/> provide a lightweight
/// cross-process handshake: the authoritative peer exports the ordered
/// list of its component types as a portable byte blob, and the joining
/// peer imports it to build a schema-index → type mapping. All subsequent
/// wire messages reference components by <b>schema index</b> (position in
/// this list) instead of the process-local <c>ComponentType.Value</c>,
/// which can differ between peers.
/// </para>
/// <para>
/// <b>Limitations:</b>
/// <list type="bullet">
///   <item><description>
///     This API does <b>not</b> make <see cref="Core.FrameDelta"/> order-independent.
///     FrameDelta still encodes raw <c>ComponentType.Value</c> and requires both
///     peers to have identical registration order (same binary, deterministic startup).
///   </description></item>
///   <item><description>
///     <see cref="Export"/> captures whatever is currently registered in the
///     shared <see cref="ComponentRegistry"/>. Types registered lazily after the
///     call will need a new export to be visible.
///   </description></item>
///   <item><description>
///     <see cref="Import"/> registers types via <c>Type.GetType()</c>, which
///     resolves assembly-qualified names. If the importing process has a different
///     assembly version or type layout, type resolution may fail.
///   </description></item>
///   <item><description>
///     <see cref="Import"/> validates the same unmanaged component type boundary
///     used by MiniArch's public component APIs. The returned <c>Type[]</c> is a
///     pure schema index mapping; binary payload compatibility is still enforced
///     by <c>WorldSnapshot</c> at save/load time.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public static class ComponentSchema
{
    /// <summary>Magic header bytes for the schema blob format: "CSCM" (Component SChema Magic).</summary>
    private const int SchemaMagic = 0x4D435343;

    /// <summary>Current format version. Increment when the wire layout changes.</summary>
    private const int FormatVersion = 1;

    /// <summary>Maximum reasonable number of component types in a schema.</summary>
    private const int MaxSchemaCount = 65536;

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

    /// <summary>
    /// Exports the current component type schema from the shared
    /// <see cref="ComponentRegistry"/> as a portable byte blob.
    /// <para/>
    /// <b>Pure schema only</b>: no entity data, no world state — just the
    /// ordered list of registered component type names. Use it when two
    /// peers need to agree on schema indices before exchanging messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exported blob has the following layout:
    /// <code>
    /// [4B magic] [4B version] [4B count] [count × UTF-8 length-prefixed type-name strings]
    /// </code>
    /// Each type name is the assembly-qualified full name
    /// (<see cref="Type.AssemblyQualifiedName"/>).
    /// </para>
    /// <para>
    /// <b>Handshake example:</b>
    /// <code>
    /// // Server (authoritative)
    /// var schemaBytes = ComponentSchema.Export();
    /// SendToClient(schemaBytes);
    ///
    /// // Client (joining)
    /// var schemaTypes = ComponentSchema.Import(schemaBytes);
    /// // schemaTypes[0] = typeof(Position) → schema index 0 = Position
    /// // schemaTypes[1] = typeof(Velocity) → schema index 1 = Velocity
    /// </code>
    /// </para>
    /// </remarks>
    /// <returns>
    /// A portable byte blob. Format: magic (4 bytes) + version (4 bytes) +
    /// count (int32) + that many assembly-qualified type name strings
    /// (UTF-8, length-prefixed).
    /// </returns>
    public static byte[] Export()
    {
        var types = ComponentRegistry.Shared.GetRegisteredTypes();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(SchemaMagic);
        writer.Write(FormatVersion);
        writer.Write(types.Length);
        foreach (var type in types)
            ComponentSchemaCodec.WriteSchemaName(writer, type);
        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Imports a schema previously exported by <see cref="Export"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each type name in the blob:
    /// <list type="number">
    ///   <item><description>
    ///     Resolve the assembly-qualified name via <see cref="Type.GetType(string,bool)"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Validate it is usable as a component (no generic parameters, no duplicate names).
    ///   </description></item>
    ///   <item><description>
    ///     Register it with <see cref="ComponentRegistry.Shared"/> via
    ///     <c>GetOrCreate(Type)</c> (idempotent).
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The returned <c>Type[]</c> maps <b>schema index</b> (position in the
    /// exported list) to the resolved <see cref="Type"/>. Build a local lookup
    /// table from this array to translate wire messages that reference components
    /// by schema index:
    /// <code>
    /// var schemaTypes = ComponentSchema.Import(schemaBytes);
    /// // schemaIndex 3 in a network message → schemaTypes[3] = typeof(Health)
    /// </code>
    /// </para>
    /// <para>
    /// If the local registry has already registered some or all of the types,
    /// <c>GetOrCreate</c> returns the existing id — no duplicates are created.
    /// The method is safe to call even after local gameplay has started; unknown
    /// types from the schema are simply registered with the next available id.
    /// </para>
    /// <para>
    /// <b>Input validation:</b>
    /// <list type="bullet">
    ///   <item><description>
    ///     Magic header mismatch → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Format version unsupported → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Count out of range (negative or &gt; <c>65536</c>) → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Duplicate type name → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Duplicate resolved type → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Type name cannot be resolved → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Type is not an unmanaged component type → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Schema name length is over the bounded UTF-8 limit → <see cref="InvalidDataException"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Truncated data or unexpected trailing bytes → <see cref="InvalidDataException"/>.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="data">Schema blob previously returned by <see cref="Export"/>.</param>
    /// <returns>
    /// Array of resolved <see cref="Type"/> in schema order.
    /// Index 0 corresponds to the first type registered on the exporter side.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The schema blob is malformed, truncated, has an unsupported version,
    /// or contains unresolvable/non-component types.
    /// </exception>
    public static Type[] Import(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8);

            var magic = reader.ReadInt32();
            if (magic != SchemaMagic)
            {
                throw new InvalidDataException(
                    $"ComponentSchema magic mismatch: expected 0x{SchemaMagic:X8}, got 0x{magic:X8}.");
            }

            var version = reader.ReadInt32();
            if (version != FormatVersion)
            {
                throw new InvalidDataException(
                    $"ComponentSchema format version {version} is not supported. " +
                    $"This version only supports version {FormatVersion}.");
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > MaxSchemaCount)
            {
                throw new InvalidDataException(
                    $"ComponentSchema count ({count}) is out of range [0, {MaxSchemaCount}].");
            }

            var types = new Type[count];
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var seenTypes = new HashSet<Type>();

            for (var i = 0; i < count; i++)
            {
                var name = ComponentSchemaCodec.ReadSchemaName(reader, nameof(ComponentSchema));

                // Reject duplicate type names
                if (!seenNames.Add(name))
                {
                    throw new InvalidDataException(
                        $"Duplicate type name in ComponentSchema: '{name}'.");
                }

                var type = ComponentSchemaCodec.ResolveSchemaType(name, nameof(ComponentSchema));

                if (!seenTypes.Add(type))
                {
                    throw new InvalidDataException(
                        $"Duplicate component type in ComponentSchema after resolution: " +
                        $"'{name}' resolves to '{ComponentSchemaCodec.GetDisplayName(type)}'.");
                }

                ComponentSchemaCodec.EnsureImportableComponentType(type, name, nameof(ComponentSchema));

                types[i] = type;
            }

            // Reject trailing bytes after the expected schema content
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException(
                    $"ComponentSchema has {reader.BaseStream.Length - reader.BaseStream.Position} " +
                    $"unexpected trailing byte(s).");
            }

            // Register all types with the shared registry so they get local ids.
            // Schema index (array position) is the stable identifier on the wire;
            // the local ComponentType.Value may differ between peers.
            for (var i = 0; i < count; i++)
                ComponentRegistry.Shared.GetOrCreate(types[i]);

            return types;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                "ComponentSchema data is truncated or malformed.", ex);
        }
    }
}
