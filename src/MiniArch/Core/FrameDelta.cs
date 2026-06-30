using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Stores a sequence of world operations in a compact self-contained byte buffer.
/// Operations are stored in temporal order, making the delta suitable for
/// deterministic replay, merging, and zero-copy serialization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two entity-id modes:</b> a <c>FrameDelta</c> can carry either
/// <b>placeholder</b> entities (<c>Entity(-1, seq)</c>) or <b>real</b>
/// entities. The wire format is identical —both encode entity id and
/// version as signed LEB128 varints —so the consumer must know which
/// mode to expect.
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
/// since frame 0). <c>World.EnsureReplayReservation</c> enforces this
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
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum number of operations per frame delta (1 million).
    /// Prevents runaway op-count from exhausting CPU in the decoder loop.
    /// Callers may wrap the transport layer with a smaller budget.
    /// </summary>
    public const int MaxOpsPerFrame = 1_000_000;

    internal byte[] _buffer = Array.Empty<byte>();
    internal int _length;
    internal int _opCount;

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
    /// otherwise component ids will be silently misinterpreted. Within a
    /// single process this is automatic; for multi-process scenarios add a
    /// type-mapping header at the transport layer.
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
        var pos = _length;
        Grow(1);
        _buffer[pos] = (byte)kind;
        _length = pos + 1;
    }

    private void WriteEntity(Entity e)
    {
        var pos = _length;
        var size = VarintSize(e.Id) + VarintSize(e.Version);
        Grow(size);
        WriteVarintAt(ref pos, e.Id);
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

    private void WriteDataWithSize(byte[] data, int offset, int size)
    {
        var pos = _length;
        var headerSize = VarintSize(size);
        Grow(headerSize + size);
        WriteVarintAt(ref pos, size);
        if (size > 0)
            Buffer.BlockCopy(data, offset, _buffer, pos, size);
        _length = pos + size;
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

    internal void AddAdd(Entity e, ComponentType t, byte[] data, int offset, int size)
        => AddComponentData(DeltaOpKind.Add, e, t, data, offset, size);

    internal void AddSet(Entity e, ComponentType t, byte[] data, int offset, int size)
        => AddComponentData(DeltaOpKind.Set, e, t, data, offset, size);

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

    private void AddComponentData(DeltaOpKind kind, Entity e, ComponentType t, byte[] data, int offset, int size)
    {
        WriteTag(kind);
        WriteEntity(e);
        WriteComponentType(t);
        WriteDataWithSize(data, offset, size);
        _opCount++;
    }

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

    internal OpDecoder GetDecoder() => new(_buffer, _length);

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

        public DeltaOpKind Kind { get; private set; }
        public Entity Entity { get; private set; }

        internal OpDecoder(byte[] buffer, int length)
        {
            _buffer = buffer;
            _end = length;
            _pos = 0;
            _opCount = 0;
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
            Entity = new Entity(ReadVarint(), ReadVarint());
            return true;
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
        /// Reads a length-prefixed byte span (component payload).
        /// </summary>
        public ReadOnlySpan<byte> ReadData()
        {
            var size = ReadVarint();
            if (size <= 0) return ReadOnlySpan<byte>.Empty;
            if (_pos + size > _end)
                throw new InvalidOperationException(
                    $"Truncated FrameDelta: insufficient data for component payload at offset {_pos} " +
                    $"(need {size} bytes, {_end - _pos} remaining).");
            var span = new ReadOnlySpan<byte>(_buffer, _pos, size);
            _pos += size;
            return span;
        }

        /// <summary>
        /// Reads length bytes from the buffer without a length prefix.
        /// </summary>
        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length <= 0) return ReadOnlySpan<byte>.Empty;
            if (_pos + length > _end)
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
            if (_pos + size > _end)
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
            for (var i = 0; i < count; i++)
            {
                ReadVarint(); // type
                var size = ReadVarint();
                if (_pos + size > _end)
                    throw new InvalidOperationException(
                        $"Truncated FrameDelta in SkipCreatePayload at offset {_pos} " +
                        $"(need {size} bytes, {_end - _pos} remaining).");
                _pos += size;
            }
        }

        /// <summary>
        /// Returns the buffer backing this decoder (for direct access in Add/Set).
        /// </summary>
        public byte[] BackingBuffer => _buffer;

        /// <summary>
        /// The current read position within the backing buffer.
        /// </summary>
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

    // ── Merge ──────────────────────────────────────────────────────────

    /// <summary>
    /// Merges two deltas in temporal order. Operations from <paramref name="a"/>
    /// appear before those from <paramref name="b"/>, preserving the original
    /// per-delta temporal order. No folding or squashing is performed —
    /// the resulting byte stream is a simple concatenation.
    /// </summary>
    /// <remarks>
    /// <b>Real-id deltas only.</b> Merge is pure byte concatenation and does
    /// not remap placeholder seq namespaces. It is therefore correct only for
    /// <b>real-id</b> deltas (those produced by
    /// <see cref="CommandStream.Snapshot"/> with
    /// <see cref="CommandStream.DeferredEntities"/> = <c>false</c>, or by
    /// <see cref="CommandStream.SubmitAndSnapshotAsync"/>). For those, the
    /// merged delta is observationally equivalent to replaying
    /// <paramref name="a"/> and <paramref name="b"/> sequentially.
    /// <para>
    /// Merging <b>placeholder</b> deltas (DeferredEntities = <c>true</c>) is
    /// unsafe and silently produces wrong results: each
    /// <see cref="CommandStream"/> with DeferredEntities starts its own seq
    /// counter at 0, so two independent streams both emit
    /// <c>Reserve(seq=0)/Create(seq=0)</c>. During replay
    /// (<see cref="World.Replay"/>) a single per-replay map[seq]→local id is
    /// used, and the second <c>Reserve</c> overwrites the first's entry —so
    /// any later op that references the first stream's placeholder resolves to
    /// the wrong entity. The canonical lockstep pattern replays each peer's
    /// placeholder delta as a separate <see cref="World.Replay"/> call (which
    /// resets the map each time), avoiding this issue entirely; do not Merge
    /// placeholder deltas across streams.
    /// </para>
    /// </remarks>
    public static FrameDelta Merge(FrameDelta a, FrameDelta b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var result = new FrameDelta();
        var totalLength = a._length + b._length;
        result._buffer = new byte[totalLength];
        if (a._length > 0)
            Array.Copy(a._buffer, 0, result._buffer, 0, a._length);
        if (b._length > 0)
            Array.Copy(b._buffer, 0, result._buffer, a._length, b._length);
        result._length = totalLength;
        result._opCount = a._opCount + b._opCount;
        return result;
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
