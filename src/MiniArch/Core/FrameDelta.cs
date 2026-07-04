using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Stores a sequence of world operations in a compact self-contained byte buffer.
/// Operations are stored in temporal order, making the delta suitable for
/// deterministic replay, concatenation, and zero-copy serialization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two entity-id modes:</b> a <c>FrameDelta</c> can carry either
/// <b>placeholder</b> entities (<c>Entity(-1, seq)</c>) or <b>real</b>
/// entities. The wire format is identical —entity Id is encoded with a
/// +1 bias (placeholder -1 → 0, real n → n+1), Version as a standard
/// unsigned LEB128 varint —so the consumer must know which mode to
/// expect.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Placeholder delta</b> (multi-host lockstep): produced by
/// <see cref="CommandStream.Snapshot"/> when
/// <see cref="CommandStream.DeferredEntities"/> is <c>true</c>. Each
/// replaying <see cref="World"/> assigns its own local ids by mapping
/// <c>seq→local real</c>. Two hosts replaying the same delta will
/// converge to identical world state even though entity ids may differ.
/// </item>
/// <item>
/// <b>Real-id delta</b>: produced by
/// <see cref="CommandStream.SubmitAndSnapshotAsync"/> (always) or by
/// <see cref="CommandStream.Snapshot"/> when
/// <see cref="CommandStream.DeferredEntities"/> is <c>false</c>
/// (default). Ids are already resolved by the producer. Mirror clients
/// must have synchronized id allocators (e.g. by replaying every frame
/// since frame 0). The replay system enforces this
/// invariant and throws if the allocator has diverged.
/// </item>
/// </list>
/// </remarks>
public sealed class FrameDelta
{
    /// <summary>
    /// Maximum wire size for a single frame delta (16 MiB).
    /// Prevents OOM from oversized or malicious wire data before allocation.
    /// Callers may wrap the transport layer with a smaller budget.
    /// </summary>
    public static readonly int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum number of operations per frame delta (1 million).
    /// Prevents runaway op-count from exhausting CPU in the decoder loop.
    /// Callers may wrap the transport layer with a smaller budget.
    /// </summary>
    public static readonly int MaxOpsPerFrame = 1_000_000;

    // ── Wire format header ─────────────────────────────────────────────
    // Non-empty deltas carry a 4-byte header at the start of _buffer:
    //   [0-1] Magic "MF" (0x4D46) — format identification.
    //   [2]   Flags: bit 7 = endianness (1=little, 0=big);
    //         bits 0-6 = format version (currently 2).
    //   [3]   Reserved (0x00).
    // Op data starts immediately after the header (offset 4).
    // Empty deltas (_opCount == 0) have _length 0 and _buffer may be empty;
    // AsSpan() returns an empty span with no header.
    //
    // Format version history:
    //   1 — initial (signed-varint entity Id)
    //   2 — entity Id bias +1 (placeholder -1 → 0, real n → n+1); backward-compat decoder.
    //
    // Backward compatibility: buffers where byte[0] is not 'M' (i.e. legacy
    // format starting with a DeltaOpKind tag) are detected and read without
    // header offset as v1. All newly produced non-empty deltas always include
    // the header; empty deltas are an empty span.
    internal const int HeaderSize = 4;
    internal const byte FormatVersion = 0x02;

    internal byte[] _buffer = Array.Empty<byte>();
    internal int _length;
    internal int _opCount;

    /// <summary>
    /// Returns the offset at which op data starts (HeaderSize for new format,
    /// 0 for legacy format without header).
    /// </summary>
    internal int DataStart => _length >= 2 && _buffer[0] == 'M' && _buffer[1] == 'F' ? HeaderSize : 0;

    /// <summary>
    /// Writes the 4-byte format header into <see cref="_buffer"/> at offset 0
    /// and sets <see cref="_length"/> to <see cref="HeaderSize"/>. Called once
    /// before the first op is written.
    /// </summary>
    private void EnsureHeader()
    {
        if (_length >= HeaderSize)
            return;
        if (_buffer.Length < HeaderSize)
            Array.Resize(ref _buffer, HeaderSize);
        // Magic: "MF"
        _buffer[0] = (byte)'M';
        _buffer[1] = (byte)'F';
        // Flags: version | (LE ? 0x80 : 0)
        _buffer[2] = (byte)(FormatVersion | (BitConverter.IsLittleEndian ? 0x80 : 0));
        _buffer[3] = 0;
        _length = HeaderSize;
    }

    /// <summary>
    /// Validates the header bytes at the start of a received delta. Throws on
    /// magic mismatch, unsupported version, or endianness mismatch.
    /// </summary>
    private static void ValidateHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2 || buf[0] != 'M' || buf[1] != 'F')
            return; // legacy format — skip validation

        if (buf.Length < HeaderSize)
            throw new InvalidOperationException(
                "FrameDelta header truncated: expected 4 bytes.");

        var flags = buf[2];
        var version = flags & 0x7F;
        if (version > FormatVersion)
            throw new InvalidOperationException(
                $"FrameDelta format version {version} is newer than the current version {FormatVersion}. " +
                "Update the MiniArch library to read this delta.");

        var isLittleEndian = (flags & 0x80) != 0;
        if (isLittleEndian != BitConverter.IsLittleEndian)
            throw new InvalidOperationException(
                $"FrameDelta endianness mismatch: producer={(isLittleEndian ? "LE" : "BE")}, " +
                $"consumer={(BitConverter.IsLittleEndian ? "LE" : "BE")}. " +
                "Cross-endian replay is not supported because component data is encoded " +
                "in the producer's native memory layout.");
    }

    /// <summary>
    /// Gets the total number of operations in this delta.
    /// </summary>
    public int DeltaCount => _opCount;

    /// <summary>
    /// Gets whether this delta has no operations.
    /// </summary>
    public bool IsEmpty => _opCount == 0;

    // ── Network serialization ──────────────────────────────────────────

    /// <summary>
    /// Exposes the packed buffer for zero-copy transmission.
    /// The returned span is the wire format —send it as-is over the network.
    /// </summary>
    /// <remarks>
    /// <b>Cross-process contract:</b> the wire format encodes
    /// <see cref="Core.ComponentType"/> values as process-local integer ids.
    /// Both peers must have identical <see cref="ComponentRegistry"/> state
    /// (same types registered in the same order) before exchanging deltas,
    /// otherwise component ids will be silently misinterpreted. For same-binary
    /// lockstep this holds automatically (deterministic code paths). For
    /// cross-version scenarios, use <see cref="MiniArch.ComponentSchema.Fingerprint"/>
    /// as a debugging aid to verify registry state.
    /// </remarks>
    public ReadOnlySpan<byte> AsSpan() => new(_buffer, 0, _length);

    /// <summary>
    /// Deserializes a FrameDelta from bytes received over the network.
    /// The caller owns the returned delta; the wire bytes are copied into
    /// an independent buffer.
    /// </summary>
    /// <remarks>
    /// See <see cref="AsSpan"/> for the cross-process
    /// <see cref="ComponentRegistry"/> synchronization contract —the same
    /// constraint applies on receive.
    /// </remarks>
    public static FrameDelta Deserialize(ReadOnlySpan<byte> wire)
    {
        if (wire.Length > MaxFrameBytes)
            throw new ArgumentException(
                $"FrameDelta exceeds MaxFrameBytes budget ({wire.Length} > {MaxFrameBytes}). " +
                "Increase MaxFrameBytes or reduce frame payload.");

        var delta = new FrameDelta();
        delta._buffer = wire.ToArray();
        delta._length = wire.Length;
        ValidateHeader(wire);

        var decoder = delta.GetDecoder();
        while (decoder.MoveNext())
        {
            delta._opCount++;
            switch (decoder.Kind)
            {
                case DeltaOpKind.Add:
                case DeltaOpKind.Set:
                    decoder.SkipData();
                    break;
                case DeltaOpKind.Remove:
                    decoder.ReadComponentType();
                    break;
                case DeltaOpKind.Create:
                    decoder.SkipCreatePayload();
                    break;
                case DeltaOpKind.AddChild:
                    decoder.ReadExtraEntity();
                    break;
            }
        }
        return delta;
    }

    // ── Validation (defense-in-depth for untrusted deltas) ─────────────

    /// <summary>
    /// Maximum placeholder sequence number allowed in <see cref="Validate"/>.
    /// Placeholder seq values above this are rejected as malformed.
    /// Equal to <see cref="MaxOpsPerFrame"/> since a single delta cannot
    /// produce more unique placeholders than operations.
    /// </summary>
    internal static readonly int MaxPlaceholderSeq = MaxOpsPerFrame;

    /// <summary>
    /// Validates structural integrity of this FrameDelta. Throws if the delta
    /// is malformed —missing Reserve for Create, component data size mismatch,
    /// negative counts, duplicate component types, unknown type ids, or
    /// invalid entity shapes.
    ///
    /// Call before <see cref="CommandStream.Replay(FrameDelta, Boolean)"/> for deltas
    /// received over the network. Deltas produced locally by
    /// <see cref="CommandStream"/> are always valid and can skip this step.
    /// </summary>
    /// <remarks>
    /// Walks the entire delta once (same cost as deserialization). Uses the
    /// global <see cref="ComponentRegistry"/> to resolve type sizes.
    /// Per-entity state machine: Reserve → Create|Release (terminal per entity).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The delta is structurally invalid.</exception>
    public void Validate()
    {
        var decoder = GetDecoder();
        // Per-entity state machine: state is implied by which set the entity
        // belongs to. "reserved" = reserved but not yet created/released.
        // "terminal" = created or released (can no longer be reserved again).
        var reserved = new HashSet<Entity>();
        var terminal = new HashSet<Entity>();

        while (decoder.MoveNext())
        {
            ValidateEntityShape(decoder.Entity);

            switch (decoder.Kind)
            {
                case DeltaOpKind.Reserve:
                {
                    if (terminal.Contains(decoder.Entity))
                        throw new InvalidOperationException(
                            $"Reserve for entity {decoder.Entity} after its terminal operation.");
                    if (!reserved.Add(decoder.Entity))
                        throw new InvalidOperationException(
                            $"Duplicate Reserve for entity {decoder.Entity}.");
                    break;
                }

                case DeltaOpKind.Release:
                {
                    if (!reserved.Remove(decoder.Entity))
                        throw new InvalidOperationException(
                            $"Release for entity {decoder.Entity} without preceding Reserve.");
                    terminal.Add(decoder.Entity);
                    break;
                }

                case DeltaOpKind.Create:
                {
                    if (!reserved.Remove(decoder.Entity))
                        throw new InvalidOperationException(
                            $"Create for entity {decoder.Entity} without preceding Reserve.");
                    terminal.Add(decoder.Entity);
                    ValidateCreatePayload(ref decoder);
                    break;
                }

                case DeltaOpKind.Add:
                case DeltaOpKind.Set:
                    ValidateComponentData(ref decoder);
                    break;

                case DeltaOpKind.Remove:
                    decoder.ReadComponentType();
                    break;

                case DeltaOpKind.AddChild:
                {
                    var parent = decoder.ReadExtraEntity();
                    ValidateEntityShape(parent);
                    break;
                }

                case DeltaOpKind.RemoveChild:
                case DeltaOpKind.Destroy:
                    // No extra payload beyond what MoveNext consumed.
                    break;

                default:
                    // MoveNext already rejects unknown op kinds.
                    break;
            }
        }
    }

    /// <summary>
    /// Validates entity handle shape: placeholder entities must have exactly
    /// <c>Id == -1</c> and <c>Version</c> within <see cref="MaxPlaceholderSeq"/>.
    /// Real entities must have <c>Id >= 0</c>. Rejects <c>Id &lt; -1</c>.
    /// </summary>
    private static void ValidateEntityShape(Entity entity)
    {
        if (entity.Id == -1)
        {
            if ((uint)entity.Version > MaxPlaceholderSeq)
                throw new InvalidOperationException(
                    $"Placeholder entity seq={entity.Version} exceeds max ({MaxPlaceholderSeq}).");
            return;
        }

        if (entity.Id < -1)
            throw new InvalidOperationException(
                $"Invalid entity id {entity.Id}: only -1 (placeholder) and >= 0 are valid.");
    }

    private static void ValidateCreatePayload(ref OpDecoder decoder)
    {
        var compCount = decoder.ReadVarint();
        if (compCount < 0)
            throw new InvalidOperationException("Negative component count in Create op.");

        if (compCount == 0) return;

        var seenTypes = new HashSet<int>();
        for (var i = 0; i < compCount; i++)
        {
            var typeId = decoder.ReadVarint();
            if (!seenTypes.Add(typeId))
                throw new InvalidOperationException(
                    $"Duplicate component type id {typeId} in Create op.");

            var dataSize = decoder.ReadVarint();
            if (dataSize < 0)
                throw new InvalidOperationException(
                    $"Negative data size for component type id {typeId}.");

            ValidateComponentSize(typeId, dataSize);
            decoder.ReadBytes(dataSize);
        }
    }

    private static void ValidateComponentData(ref OpDecoder decoder)
    {
        var typeId = decoder.ReadVarint();
        var dataSize = decoder.ReadVarint();
        ValidateComponentSize(typeId, dataSize);
        decoder.ReadBytes(dataSize);
    }

    private static void ValidateComponentSize(int typeId, int dataSize)
    {
        var compType = new ComponentType(typeId);
        if (!ComponentRegistry.Shared.TryGetType(compType, out var clrType))
            throw new InvalidOperationException($"Unknown component type id {typeId}.");

        var expected = ComponentSizeCache.GetSize(clrType);
        if (dataSize != expected)
            throw new InvalidOperationException(
                $"Component type id {typeId} has size {expected} bytes, but delta declares {dataSize} bytes.");
    }

    // ── Writer API (used by CommandStream) ─────────────────────────────

    private void Grow(int additionalBytes)
    {
        var needed = _length + additionalBytes;
        if (needed <= _buffer.Length) return;
        var newSize = Math.Max(needed, Math.Max(_buffer.Length * 2, 256));
        Array.Resize(ref _buffer, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteTag(DeltaOpKind kind)
    {
        EnsureHeader();
        var pos = _length;
        Grow(1);
        _buffer[pos] = (byte)kind;
        _length = pos + 1;
    }

    // ── Entity Id encoding (v2+) ───────────────────────────────────
    // Entity.Id in the wire format uses a +1 bias so that the common
    // placeholder value -1 encodes as 0 (1 varint byte) instead of the
    // 5 bytes that signed-varint would require. Real ids (>= 0) become
    // n+1, which is at most 1 byte larger than the raw id when the +1
    // pushes the value past a varint size boundary (e.g. 127 → 128).
    //
    // Version is always >= 0 and stored as a plain unsigned varint.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EncodeEntityIdV2(int id) => (uint)(id + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeEntityIdV2(uint raw) => raw == 0 ? -1 : (int)(raw - 1);

    /// <summary>
    /// Writes an entity handle with the current wire format (v2+: Id bias +1).
    /// </summary>
    private void WriteEntity(Entity e)
    {
        var pos = _length;
        var encId = EncodeEntityIdV2(e.Id);
        var size = VarintSize((int)encId) + VarintSize(e.Version);
        Grow(size);
        WriteVarintAt(ref pos, (int)encId);
        WriteVarintAt(ref pos, e.Version);
        _length = pos;
    }

    private void WriteComponentType(ComponentType t)
    {
        var pos = _length;
        Grow(VarintSize(t.Value));
        WriteVarintAt(ref pos, t.Value);
        _length = pos;
    }

    internal void AddReserve(Entity e)
    {
        WriteTag(DeltaOpKind.Reserve);
        WriteEntity(e);
        _opCount++;
    }

    internal void AddRelease(Entity e)
    {
        WriteTag(DeltaOpKind.Release);
        WriteEntity(e);
        _opCount++;
    }

    internal void AddCreate(Entity e, RawComponentValue[] components)
    {
        WriteTag(DeltaOpKind.Create);
        WriteEntity(e);
        var pos = _length;
        var compCount = components.Length;
        var size = VarintSize(compCount);
        for (var i = 0; i < compCount; i++)
        {
            ref var c = ref components[i];
            size += VarintSize(c.ComponentType.Value) + VarintSize(c.DataSize) + c.DataSize;
        }
        Grow(size);
        WriteVarintAt(ref pos, compCount);
        for (var i = 0; i < compCount; i++)
        {
            ref var c = ref components[i];
            WriteVarintAt(ref pos, c.ComponentType.Value);
            WriteVarintAt(ref pos, c.DataSize);
            if (c.DataSize > 0)
                Buffer.BlockCopy(c.Data, c.DataOffset, _buffer, pos, c.DataSize);
            pos += c.DataSize;
        }
        _length = pos;
        _opCount++;
    }

    internal void AddDestroy(Entity e)
    {
        WriteTag(DeltaOpKind.Destroy);
        WriteEntity(e);
        _opCount++;
    }

    internal void AddAddChild(Entity parent, Entity child)
    {
        WriteTag(DeltaOpKind.AddChild);
        WriteEntity(child);
        WriteEntity(parent);
        _opCount++;
    }

    internal void AddRemoveChild(Entity child)
    {
        WriteTag(DeltaOpKind.RemoveChild);
        WriteEntity(child);
        _opCount++;
    }

    internal void AddRemove(Entity e, ComponentType t)
    {
        WriteTag(DeltaOpKind.Remove);
        WriteEntity(e);
        WriteComponentType(t);
        _opCount++;
    }

    internal unsafe void AddAddUnsafe(Entity e, ComponentType t, void* data, int size)
        => AddComponentDataUnsafe(DeltaOpKind.Add, e, t, data, size);

    internal unsafe void AddSetUnsafe(Entity e, ComponentType t, void* data, int size)
        => AddComponentDataUnsafe(DeltaOpKind.Set, e, t, data, size);

    private unsafe void AddComponentDataUnsafe(DeltaOpKind kind, Entity e, ComponentType t, void* data, int size)
    {
        WriteTag(kind);
        WriteEntity(e);
        WriteComponentType(t);
        WriteDataUnsafe(data, size);
        _opCount++;
    }

    private unsafe void WriteDataUnsafe(void* data, int size)
    {
        var pos = _length;
        Grow(VarintSize(size) + size);
        WriteVarintAt(ref pos, size);
        if (size > 0)
            Unsafe.CopyBlockUnaligned(ref _buffer[pos], ref *(byte*)data, (uint)size);
        _length = pos + size;
    }

    // ── Decoder API (used by ReplayCore, HasEntity) ────────────────────

    internal OpDecoder GetDecoder()
    {
        var fmtVer = 1;
        if (_length >= HeaderSize && _buffer[0] == 'M' && _buffer[1] == 'F')
            fmtVer = _buffer[2] & 0x7F;
        return new OpDecoder(_buffer, _length, DataStart, fmtVer);
    }

    /// <summary>
    /// Cursor-based decoder that reads operations from the packed buffer.
    /// Call <see cref="MoveNext"/> to advance, then read the current
    /// operation's payload using the type-specific read methods.
    /// </summary>
    internal struct OpDecoder
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _pos;
        private int _opCount;
        private readonly int _formatVersion;

        public DeltaOpKind Kind { get; private set; }
        public Entity Entity { get; private set; }

        internal OpDecoder(byte[] buffer, int length, int startPos, int formatVersion = 1)
        {
            _buffer = buffer;
            _end = length;
            _pos = startPos;
            _opCount = 0;
            _formatVersion = formatVersion;
            Kind = default;
            Entity = default;
        }

        public bool MoveNext()
        {
            if (_pos >= _end) return false;
            if (++_opCount > MaxOpsPerFrame)
                throw new InvalidOperationException(
                    $"FrameDelta exceeds MaxOpsPerFrame budget ({MaxOpsPerFrame} ops). " +
                    "Increase MaxOpsPerFrame or reduce frame complexity.");
            var opByte = _buffer[_pos++];
            // Reject unknown op kinds loudly. For lockstep/multiplayer, a peer
            // that silently skips an op it does not understand will desync.
            // Failing fast surfaces version mismatches at the first unknown op
            // instead of producing a subtly corrupted world.
            if (opByte < 0x01 || opByte > 0x09)
                throw new InvalidOperationException(
                    $"Unknown FrameDelta op kind 0x{opByte:X2} at offset {_pos - 1}. " +
                    "This typically indicates a version mismatch between the delta producer and consumer.");
            Kind = (DeltaOpKind)opByte;
            Entity = ReadEntityFromBuffer();
            return true;
        }

        /// <summary>
        /// Reads one entity handle from the buffer using the delta's format version.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Entity ReadEntityFromBuffer()
        {
            if (_formatVersion >= 2)
            {
                var id = DecodeEntityIdV2((uint)ReadVarint());
                var ver = ReadVarint();
                return new Entity(id, ver);
            }
            // v1: raw signed-varint entity Id.
            return new Entity(ReadVarint(), ReadVarint());
        }

        /// <summary>
        /// Reads a varint from the buffer at the current position and advances.
        /// Throws if the varint is truncated (extends past end of buffer) or
        /// malformed (exceeds 5 bytes / 32-bit range).
        /// </summary>
        public int ReadVarint()
        {
            var buf = _buffer;
            var end = _end;
            var startPos = _pos;
            int result = 0;
            int shift = 0;
            for (var i = 0; i < 5; i++)
            {
                if (_pos >= end)
                    throw new InvalidOperationException(
                        $"Truncated FrameDelta: varint extends past end of buffer at offset {startPos}.");
                var b = buf[_pos++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            // 5 bytes consumed but continuation bit still set: the producer is
            // encoding a value wider than 32 bits, or the stream is corrupt.
            throw new InvalidOperationException(
                $"Malformed FrameDelta: varint exceeds 32-bit range at offset {startPos}.");
        }

        /// <summary>
        /// Reads the extra entity (AddChild parent) from the buffer.
        /// Only valid when <see cref="Kind"/> is <see cref="DeltaOpKind.AddChild"/>.
        /// </summary>
        public Entity ReadExtraEntity()
        {
            if (_formatVersion >= 2)
            {
                var id = DecodeEntityIdV2((uint)ReadVarint());
                var ver = ReadVarint();
                return new Entity(id, ver);
            }
            return new Entity(ReadVarint(), ReadVarint());
        }

        /// <summary>
        /// Reads the component type for Add/Set/Remove operations.
        /// </summary>
        public ComponentType ReadComponentType()
        {
            return new ComponentType(ReadVarint());
        }

        /// <summary>
        /// Reads length bytes from the buffer without a length prefix.
        /// </summary>
        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0)
                throw new InvalidOperationException(
                    $"Invalid FrameDelta: negative byte length ({length}) at offset {_pos}.");
            if (length == 0) return ReadOnlySpan<byte>.Empty;
            if ((uint)length > (uint)(_end - _pos))
                throw new InvalidOperationException(
                    $"Truncated FrameDelta: insufficient data bytes at offset {_pos} " +
                    $"(need {length} bytes, {_end - _pos} remaining).");
            var span = new ReadOnlySpan<byte>(_buffer, _pos, length);
            _pos += length;
            return span;
        }

        /// <summary>
        /// Skips over the data payload for the current Add/Set operation.
        /// </summary>
        public void SkipData()
        {
            ReadVarint(); // ComponentType
            var size = ReadVarint();
            if (size < 0)
                throw new InvalidOperationException(
                    $"Invalid FrameDelta: negative data size ({size}) at offset {_pos}.");
            if ((uint)size > (uint)(_end - _pos))
                throw new InvalidOperationException(
                    $"Truncated FrameDelta in SkipData at offset {_pos} " +
                    $"(need {size} bytes, {_end - _pos} remaining).");
            _pos += size;
        }

        /// <summary>
        /// Skips past a Create operation's component payload.
        /// </summary>
        public void SkipCreatePayload()
        {
            var count = ReadVarint();
            if (count < 0)
                throw new InvalidOperationException(
                    $"Invalid FrameDelta: negative component count ({count}) at offset {_pos}.");
            for (var i = 0; i < count; i++)
            {
                ReadVarint(); // type
                var size = ReadVarint();
                if (size < 0)
                    throw new InvalidOperationException(
                        $"Invalid FrameDelta: negative data size ({size}) in Create component at offset {_pos}.");
                if ((uint)size > (uint)(_end - _pos))
                    throw new InvalidOperationException(
                        $"Truncated FrameDelta in SkipCreatePayload at offset {_pos} " +
                        $"(need {size} bytes, {_end - _pos} remaining).");
                _pos += size;
            }
        }

        public int CurrentPosition => _pos;
    }

    // ── Entity scan ────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether this delta references <paramref name="entity"/>.
    /// O(n) linear scan over every operation —use for debugging only, not in hot paths.
    /// </summary>
    public bool HasEntity(Entity entity)
    {
        var decoder = GetDecoder();
        while (decoder.MoveNext())
        {
            if (decoder.Entity.Equals(entity)) return true;
            switch (decoder.Kind)
            {
                case DeltaOpKind.AddChild:
                    if (decoder.ReadExtraEntity().Equals(entity)) return true;
                    break;
                case DeltaOpKind.Add:
                case DeltaOpKind.Set:
                    decoder.SkipData();
                    break;
                case DeltaOpKind.Remove:
                    decoder.ReadComponentType();
                    break;
                case DeltaOpKind.Create:
                    decoder.SkipCreatePayload();
                    break;
                case DeltaOpKind.Reserve:
                case DeltaOpKind.Release:
                case DeltaOpKind.RemoveChild:
                case DeltaOpKind.Destroy:
                    // No extra payload beyond the entity read by MoveNext.
                    break;
                default:
                    // MoveNext already rejects unknown op kinds, so reaching here
                    // would indicate a logic bug rather than a corrupt stream.
                    throw new InvalidOperationException(
                        $"Unhandled DeltaOpKind {decoder.Kind} in HasEntity scan.");
            }
        }
        return false;
    }

    // ── Varint helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of bytes needed to encode a LEB128 varint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarintSize(int value)
    {
        if (value < 0) return 5; // negative —bit 31 set, always 5 bytes
        if (value < 128) return 1;
        if (value < 16384) return 2;
        if (value < 2097152) return 3;
        if (value < 268435456) return 4;
        return 5;
    }

    /// <summary>
    /// Writes a LEB128 varint into this instance's buffer at pos and advances pos.
    /// </summary>
    private void WriteVarintAt(ref int pos, int value)
    {
        var buf = _buffer;
        while (value >= 0x80 || value < 0)
        {
            buf[pos++] = (byte)((uint)value | 0x80);
            value = (int)((uint)value >> 7);
        }
        buf[pos++] = (byte)value;
    }
}

// ── Op kind enum ───────────────────────────────────────────────────────

internal enum DeltaOpKind : byte
{
    Reserve = 0x01,
    Release = 0x02,
    Create = 0x03,
    AddChild = 0x04,
    RemoveChild = 0x05,
    Add = 0x06,
    Set = 0x07,
    Remove = 0x08,
    Destroy = 0x09,
}

// ── Retained record struct (used by CommandStream) ───────────────────

internal readonly record struct RawComponentValue(
    ComponentType ComponentType,
    byte[] Data,
    int DataOffset,
    int DataSize);
