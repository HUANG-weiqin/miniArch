using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Stores a sequence of world operations in a compact self-contained byte buffer.
/// Operations are stored in temporal order, making the delta suitable for
/// deterministic replay, merging, and zero-copy serialization.
/// </summary>
public sealed class FrameDelta
{
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
    /// The returned span is the wire format — send it as-is over the network.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => new(_buffer, 0, _length);

    /// <summary>
    /// Deserializes a FrameDelta from bytes received over the network.
    /// The caller owns the returned delta; the wire bytes are copied into
    /// an independent buffer.
    /// </summary>
    public static FrameDelta Deserialize(ReadOnlySpan<byte> wire)
    {
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
                case DeltaOpKind.Link:
                    decoder.ReadExtraEntity();
                    break;
            }
        }
        return delta;
    }

    // ── Writer API (used by CommandBuffer / CommandStream) ──────────────

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

    internal void AddLink(Entity parent, Entity child)
    {
        WriteTag(DeltaOpKind.Link);
        WriteEntity(child);
        WriteEntity(parent);
        _opCount++;
    }

    internal void AddUnlink(Entity child)
    {
        WriteTag(DeltaOpKind.Unlink);
        WriteEntity(child);
        _opCount++;
    }

    internal void AddAdd(Entity e, ComponentType t, byte[] data, int offset, int size)
    {
        WriteTag(DeltaOpKind.Add);
        WriteEntity(e);
        WriteComponentType(t);
        WriteDataWithSize(data, offset, size);
        _opCount++;
    }

    internal void AddSet(Entity e, ComponentType t, byte[] data, int offset, int size)
    {
        WriteTag(DeltaOpKind.Set);
        WriteEntity(e);
        WriteComponentType(t);
        WriteDataWithSize(data, offset, size);
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
    {
        WriteTag(DeltaOpKind.Add);
        WriteEntity(e);
        WriteComponentType(t);
        WriteDataUnsafe(data, size);
        _opCount++;
    }

    internal unsafe void AddSetUnsafe(Entity e, ComponentType t, void* data, int size)
    {
        WriteTag(DeltaOpKind.Set);
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

        public DeltaOpKind Kind { get; private set; }
        public Entity Entity { get; private set; }

        internal OpDecoder(byte[] buffer, int length)
        {
            _buffer = buffer;
            _end = length;
            _pos = 0;
            Kind = default;
            Entity = default;
        }

        public bool MoveNext()
        {
            if (_pos >= _end) return false;
            Kind = (DeltaOpKind)_buffer[_pos++];
            Entity = new Entity(ReadVarint(), ReadVarint());
            return true;
        }

        /// <summary>
        /// Reads a varint from the buffer at the current position and advances.
        /// </summary>
        public int ReadVarint()
        {
            var buf = _buffer;
            var end = _end;
            int result = 0;
            int shift = 0;
            for (var i = 0; i < 5; i++)
            {
                if (_pos >= end) return result;
                var b = buf[_pos++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            return result;
        }

        /// <summary>
        /// Reads the extra entity (Link parent) from the buffer.
        /// Only valid when <see cref="Kind"/> is <see cref="DeltaOpKind.Link"/>.
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
                throw new InvalidOperationException("Truncated FrameDelta: insufficient data for component payload.");
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
                throw new InvalidOperationException("Truncated FrameDelta: insufficient data bytes.");
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
                throw new InvalidOperationException("Truncated FrameDelta in SkipData.");
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
                    throw new InvalidOperationException("Truncated FrameDelta in SkipCreatePayload.");
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

    public bool HasEntity(Entity entity)
    {
        var decoder = GetDecoder();
        while (decoder.MoveNext())
        {
            if (decoder.Entity.Equals(entity)) return true;
            switch (decoder.Kind)
            {
                case DeltaOpKind.Link:
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
                default:
                    break;
            }
        }
        return false;
    }

    // ── Lazy legacy list access (for test code compatibility) ───────────

    private bool _legacyParsed;
    private List<Entity>? _lazyReserved;
    private List<RawCreatedEntity>? _lazyCreated;
    private List<LinkCommand>? _lazyLink;
    private List<UnlinkCommand>? _lazyUnlink;
    private List<RawComponentCommand>? _lazyAdd;
    private List<RawComponentCommand>? _lazySet;
    private List<RawRemoveCommand>? _lazyRemove;
    private List<Entity>? _lazyDestroyed;
    private List<Entity>? _lazyReleased;

    private void ParseLegacy()
    {
        if (_legacyParsed) return;
        _legacyParsed = true;

        _lazyReserved = new(16);
        _lazyCreated = new(16);
        _lazyLink = new(16);
        _lazyUnlink = new(16);
        _lazyAdd = new(16);
        _lazySet = new(16);
        _lazyRemove = new(16);
        _lazyDestroyed = new(16);
        _lazyReleased = new(16);

        var decoder = GetDecoder();
        while (decoder.MoveNext())
        {
            switch (decoder.Kind)
            {
                case DeltaOpKind.Reserve:
                    _lazyReserved.Add(decoder.Entity);
                    break;
                case DeltaOpKind.Release:
                    _lazyReleased.Add(decoder.Entity);
                    break;
                case DeltaOpKind.Create:
                {
                    var compCount = decoder.ReadVarint();
                    var comps = new RawComponentValue[compCount];
                    for (var i = 0; i < compCount; i++)
                    {
                        var type = new ComponentType(decoder.ReadVarint());
                        var dataSize = decoder.ReadVarint();
                        if (dataSize > 0)
                        {
                            var offset = decoder.CurrentPosition;
                            _ = decoder.ReadBytes(dataSize);
                            comps[i] = new RawComponentValue(type, _buffer, offset, dataSize);
                        }
                        else
                        {
                            comps[i] = new RawComponentValue(type, Array.Empty<byte>(), 0, 0);
                        }
                    }
                    _lazyCreated.Add(new RawCreatedEntity(decoder.Entity, comps));
                    break;
                }
                case DeltaOpKind.Link:
                {
                    var parent = decoder.ReadExtraEntity();
                    _lazyLink.Add(new LinkCommand(parent, decoder.Entity));
                    break;
                }
                case DeltaOpKind.Unlink:
                    _lazyUnlink.Add(new UnlinkCommand(decoder.Entity));
                    break;
                case DeltaOpKind.Add:
                {
                    var comp = decoder.ReadComponentType();
                    var size = decoder.ReadVarint();
                    var offset = decoder.CurrentPosition;
                    _ = decoder.ReadBytes(size);
                    _lazyAdd.Add(new RawComponentCommand(decoder.Entity, comp, offset, size, _buffer));
                    break;
                }
                case DeltaOpKind.Set:
                {
                    var comp = decoder.ReadComponentType();
                    var size = decoder.ReadVarint();
                    var offset = decoder.CurrentPosition;
                    _ = decoder.ReadBytes(size);
                    _lazySet.Add(new RawComponentCommand(decoder.Entity, comp, offset, size, _buffer));
                    break;
                }
                case DeltaOpKind.Remove:
                {
                    var comp = decoder.ReadComponentType();
                    _lazyRemove.Add(new RawRemoveCommand(decoder.Entity, comp));
                    break;
                }
                case DeltaOpKind.Destroy:
                    _lazyDestroyed.Add(decoder.Entity);
                    break;
                default:
                    break;
            }
        }
    }

    internal List<Entity> ReservedEntities { get { ParseLegacy(); return _lazyReserved!; } }
    internal List<RawCreatedEntity> CreatedEntities { get { ParseLegacy(); return _lazyCreated!; } }
    internal List<LinkCommand> LinkCommands { get { ParseLegacy(); return _lazyLink!; } }
    internal List<UnlinkCommand> UnlinkCommands { get { ParseLegacy(); return _lazyUnlink!; } }
    internal List<RawComponentCommand> AddCommands { get { ParseLegacy(); return _lazyAdd!; } }
    internal List<RawComponentCommand> SetCommands { get { ParseLegacy(); return _lazySet!; } }
    internal List<RawRemoveCommand> RemoveCommands { get { ParseLegacy(); return _lazyRemove!; } }
    internal List<Entity> DestroyedEntities { get { ParseLegacy(); return _lazyDestroyed!; } }
    internal List<Entity> ReleasedEntities { get { ParseLegacy(); return _lazyReleased!; } }

    // ── Merge ──────────────────────────────────────────────────────────

    /// <summary>
    /// Merges two deltas in temporal order. Operations from <paramref name="a"/>
    /// appear before those from <paramref name="b"/>, preserving the original
    /// per-delta temporal order. No folding or squashing is performed —
    /// the resulting byte stream is a simple concatenation.
    /// </summary>
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
        if (value < 0) return 5; // negative → bit 31 set, always 5 bytes
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
    Link = 0x04,
    Unlink = 0x05,
    Add = 0x06,
    Set = 0x07,
    Remove = 0x08,
    Destroy = 0x09,
}

// ── Retained record structs (used by CommandBuffer/CommandStream) ──────

internal readonly record struct RawComponentValue(
    ComponentType ComponentType,
    byte[] Data,
    int DataOffset,
    int DataSize);

internal readonly record struct RawCreatedEntity(Entity Entity, RawComponentValue[] Components);

internal readonly record struct RawComponentCommand(
    Entity Entity,
    ComponentType ComponentType,
    int DataOffset,
    int DataSize,
    byte[] Data);

internal readonly record struct RawRemoveCommand(Entity Entity, ComponentType ComponentType);

internal readonly record struct LinkCommand(Entity Parent, Entity Child);

internal readonly record struct UnlinkCommand(Entity Child);
