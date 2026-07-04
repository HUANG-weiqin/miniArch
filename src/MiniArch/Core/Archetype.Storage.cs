using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

[SkipLocalsInit]
internal sealed partial class Archetype
{
    // .NET maximum array element count; avoids overflow in _capacity * 2.
    private const int ArrayMaxLength = 0x7FFFFFC7; // Array.MaxLength

    // ──────────────────────────────────────────────
    //  Row-mapping helpers (chunked mode)
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int SegmentIndex, int LocalRow) GetSegmentAndLocal(int globalRow) => (globalRow >> _segmentBitShift, globalRow & _segmentMask);

    // ──────────────────────────────────────────────
    //  Conversion & segment growth
    // ──────────────────────────────────────────────

    private void NormalizeForChunked()
    {
        if (_capacity < _segmentCapacity)
        {
            var newEntities = new Entity[_segmentCapacity];
            Array.Copy(_entities, newEntities, _count);
            var (newData, newOffsets, _) = CreateStorage(_signature, _componentTypes, _segmentCapacity, pinned: true);
            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = _count * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref _data[_columnByteOffsets[col]];
                ref var dstRef = ref newData[newOffsets[col]];
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }
            _entities = newEntities;
            _data = newData;
            _columnByteOffsets = newOffsets;
            _capacity = _segmentCapacity;
        }
    }

    private void ConvertToChunked()
    {
        _segments = new Segment[1];
        _segments[0] = new Segment
        {
            Entities = _entities,
            Data = _data,
            Count = _count
        };
        _segmentCount = 1;
        _entities = null!;
        _data = null!;
        _isChunked = true;
    }

    private void GrowChunked(int need)
    {
        while (need > 0)
        {
            var seg = new Segment
            {
                Entities = new Entity[_segmentCapacity],
                Data = CreateStorageBytes(_signature, _componentTypes, _segmentCapacity),
                Count = 0
            };
            var newIdx = _segmentCount;
            if (_segments.Length == newIdx)
                Array.Resize(ref _segments, Math.Max(newIdx * 2, 4));
            _segments[newIdx] = seg;
            _segmentCount++;
            need -= _segmentCapacity;
        }
        _flatEntitiesGeneration++;
    }

    // ──────────────────────────────────────────────
    //  EnsureCapacity
    // ──────────────────────────────────────────────

    internal void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= Capacity) return;

        if (!_isChunked && _capacity * 2 > _segmentCapacity)
        {
            NormalizeForChunked();
            ConvertToChunked();
            GrowChunked(requiredCapacity - _count);
            return;
        }

        if (!_isChunked)
        {
            var doubleCapacity = Math.Min(_capacity * 2, ArrayMaxLength);
            var newCapacity = Math.Max(requiredCapacity, doubleCapacity);

            var newEntities = new Entity[newCapacity];
            Array.Copy(_entities, newEntities, _count);

            var (newData, newOffsets, _) = CreateStorage(_signature, _componentTypes, newCapacity);

            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = _count * elemSize;
                if (columnBytes <= 0) continue;

                ref var srcRef = ref _data[_columnByteOffsets[col]];
                ref var dstRef = ref newData[newOffsets[col]];
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }

            _entities = newEntities;
            _data = newData;
            _columnByteOffsets = newOffsets;
            _capacity = newCapacity;
        }
        else
        {
            GrowChunked(requiredCapacity - Capacity);
        }
    }

    // ──────────────────────────────────────────────
    //  AddEntity
    // ──────────────────────────────────────────────

    internal int AddEntity(Entity entity)
    {
        if (_isChunked)
            return AddEntityChunked(entity);

        EnsureCapacity(_count + 1);
        if (!_isChunked)
        {
            var row = _count;
            _entities[row] = entity;
            _count++;
            return row;
        }
        return AddEntityChunked(entity);
    }

    private int AddEntityChunked(Entity entity)
    {
        var lastSegIdx = _segmentCount - 1;
        ref var lastSeg = ref _segments[lastSegIdx];
        while (lastSeg.Count >= lastSeg.Entities.Length)
        {
            GrowChunked(1);
            lastSegIdx = _segmentCount - 1;
            lastSeg = ref _segments[lastSegIdx];
        }
        var localRow = lastSeg.Count;
        var globalRow = lastSegIdx * _segmentCapacity + localRow;
        lastSeg.Entities[localRow] = entity;
        lastSeg.Count++;
        _count++;
        _flatEntitiesGeneration++;
        return globalRow;
    }

    // ──────────────────────────────────────────────
    //  ReserveRows
    // ──────────────────────────────────────────────

    internal int ReserveRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
            return _count;

        EnsureCapacity(_count + count);

        if (!_isChunked)
        {
            var row = _count;
            _count += count;
            return row;
        }

        var startRow = _count;
        var remaining = count;
        while (remaining > 0)
        {
            var lastSegIdx = _segmentCount - 1;
            ref var lastSeg = ref _segments[lastSegIdx];
            var available = lastSeg.Entities.Length - lastSeg.Count;
            if (available == 0)
            {
                EnsureCapacity(_count + remaining);
                continue;
            }
            var take = Math.Min(remaining, available);
            lastSeg.Count += take;
            _count += take;
            remaining -= take;
        }
        _flatEntitiesGeneration++;
        return startRow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteEntityAt(int globalRow, Entity entity)
    {
        if (!_isChunked)
        {
            _entities[globalRow] = entity;
            return;
        }
        var (segIdx, localRow) = GetSegmentAndLocal(globalRow);
        _segments[segIdx].Entities[localRow] = entity;
        _flatEntitiesGeneration++;
    }

    [Conditional("DEBUG")]
    private void AssertValidRow(int row)
    {
        if ((uint)row >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(row));
    }

    // ──────────────────────────────────────────────
    //  RemoveAt (cross-segment swap-remove)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Removes the entity at <paramref name="row"/> using swap-remove.
    /// </summary>
    /// <param name="row">Row index to remove.</param>
    /// <param name="movedEntity">The entity that was moved into <paramref name="row"/>
    /// from the last position, if any; <c>default</c> if the removed entity was already
    /// the last row.</param>
    /// <returns><c>true</c> if a different entity was moved into the removed row
    /// (swap occurred); <c>false</c> if the removed entity was the last row
    /// (no swap needed).</returns>
    internal bool RemoveAt(int row, out Entity movedEntity)
    {
        AssertValidRow(row);
        if (!_isChunked)
        {
            var last = _count - 1;
            if (row != last)
            {
                movedEntity = _entities[last];
                _entities[row] = movedEntity;
                CopyRemovedRow(row, last);
            }
            else
            {
                movedEntity = default;
            }
            _entities[last] = default;
            _count--;
            return row != last;
        }

        var lastSegIdx = _segmentCount - 1;
        while (lastSegIdx >= 0 && _segments[lastSegIdx].Count == 0)
            lastSegIdx--;
        if (lastSegIdx < 0)
        {
            movedEntity = default;
            _count--;
            return false;
        }

        var lastSegCount = _segments[lastSegIdx].Count;
        var lastLocalRow = lastSegCount - 1;
        var lastGlobalRow = lastSegIdx * _segmentCapacity + lastLocalRow;

        if (row == lastGlobalRow)
        {
            _segments[lastSegIdx].Entities[lastLocalRow] = default;
            _segments[lastSegIdx].Count--;
            _count--;
            _flatEntitiesGeneration++;
            movedEntity = default;
            return false;
        }

        var (delSegIdx, delLocalRow) = GetSegmentAndLocal(row);

        movedEntity = _segments[lastSegIdx].Entities[lastLocalRow];
        CopySegmentColumn(lastSegIdx, lastLocalRow, delSegIdx, delLocalRow);

        _segments[delSegIdx].Entities[delLocalRow] = movedEntity;
        _segments[lastSegIdx].Entities[lastLocalRow] = default;
        _segments[lastSegIdx].Count--;
        _count--;
        _flatEntitiesGeneration++;
        return true;
    }

    // ──────────────────────────────────────────────
    //  Entity access
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        if (!_isChunked)
            return _entities.AsSpan(0, _count);

        return GetEntityStorageUnsafe().AsSpan(0, _count);
    }

    /// <summary>
    /// Returns the internal entity array. <b>Unsafe:</b> the returned array is
    /// cached internal storage — callers must not mutate it. For read-only access
    /// prefer <see cref="GetEntities"/> which returns <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity[] GetEntityStorageUnsafe()
    {
        if (!_isChunked)
            return _entities;

        if (_cachedFlatEntitiesGeneration != _flatEntitiesGeneration)
        {
            if (_cachedFlatEntities is null || _cachedFlatEntities.Length < _count)
                _cachedFlatEntities = new Entity[_count];

            var off = 0;
            for (var i = 0; i < _segmentCount; i++)
            {
                var seg = _segments[i];
                Array.Copy(seg.Entities, 0, _cachedFlatEntities, off, seg.Count);
                off += seg.Count;
            }
            _cachedFlatEntitiesGeneration = _flatEntitiesGeneration;
        }
        return _cachedFlatEntities!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity GetEntity(int row)
    {
        if (!_isChunked)
            return _entities[row];

        var (segIdx, localRow) = GetSegmentAndLocal(row);
        return _segments[segIdx].Entities[localRow];
    }

    // ──────────────────────────────────────────────
    //  Component access
    // ──────────────────────────────────────────────

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex) where T : unmanaged
    {
        ThrowIfChunked();
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            _columnByteOffsets[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRefAt<T>(int columnIndex, int row) where T : unmanaged
    {
        if (!_isChunked)
        {
            return ref Unsafe.As<byte, T>(ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_data),
                _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex]));
        }
        var (segIdx, localRow) = GetSegmentAndLocal(row);
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_segments[segIdx].Data),
            _columnByteOffsets[columnIndex] + localRow * _elementSizes[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value) where T : unmanaged
    {
        ref var target = ref GetComponentRefAt<T>(columnIndex, row);
        target = value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row) where T : unmanaged
    {
        return GetComponentRefAt<T>(columnIndex, row);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpanAt<T>(int columnIndex) where T : unmanaged
    {
        return MemoryMarshal.CreateSpan(ref GetComponentRef<T>(columnIndex), _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpan<T>(ComponentType component) where T : unmanaged
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentSpanAt<T>(columnIndex);
    }

    // ──────────────────────────────────────────────
    //  Per-segment component span (for ChunkView)
    // ──────────────────────────────────────────────

    [SkipLocalsInit]
    internal Span<T> GetSegmentComponentSpan<T>(int segmentIndex, int columnIndex) where T : unmanaged
    {
        ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_segments[segmentIndex].Data);
        ref var start = ref Unsafe.As<byte, T>(ref Unsafe.Add(ref dataRef, _columnByteOffsets[columnIndex]));
        return MemoryMarshal.CreateSpan(ref start, _segments[segmentIndex].Count);
    }

    internal int SegmentCount => _segmentCount;

    internal int GetSegmentCount(int segmentIndex) => _segments[segmentIndex].Count;

    internal ReadOnlySpan<Entity> GetSegmentEntities(int segmentIndex) =>
        _segments[segmentIndex].Entities.AsSpan(0, _segments[segmentIndex].Count);

    // ──────────────────────────────────────────────
    //  Copy helpers
    // ──────────────────────────────────────────────

    internal void CopySharedComponentsFrom(Archetype source, int sourceRow, int destinationRow)
    {
        if (ReferenceEquals(this, source))
        {
            CopyAllColumnsFrom(source, sourceRow, destinationRow);
            return;
        }

        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
                continue;

            CopyComponent(source, sourceColumnIndex, sourceRow, index, destinationRow);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyAllColumnsFrom(Archetype source, int sourceRow, int destinationRow)
    {
        var columnCount = _elementSizes.Length;
        for (var index = 0; index < columnCount; index++)
            CopyComponent(source, index, sourceRow, index, destinationRow);
    }

    // NOTE: stays in managed-ref space on purpose. The backing byte[] for both
    // the non-chunked (_data) and chunked (_segments[i].Data) cases is NOT pinned
    // for the chunked path, so taking a raw pointer via Unsafe.AsPointer would
    // become a dangling pointer across a compacting GC. Managed refs are tracked
    // by the GC and remain valid across compaction. See CopyColumnFrom for the
    // same pattern.
    [SkipLocalsInit]
    private void CopyComponent(Archetype source, int sourceColumnIndex, int sourceRow,
        int destinationColumnIndex, int destinationRow)
    {
        var size = _elementSizes[destinationColumnIndex];

        ref byte srcRef = ref Unsafe.NullRef<byte>();
        if (!source._isChunked)
        {
            srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._data),
                source._columnByteOffsets[sourceColumnIndex] + sourceRow * size);
        }
        else
        {
            var (srcSegIdx, srcLocal) = source.GetSegmentAndLocal(sourceRow);
            srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._segments[srcSegIdx].Data),
                source._columnByteOffsets[sourceColumnIndex] + srcLocal * size);
        }

        ref byte dstRef = ref Unsafe.NullRef<byte>();
        if (!_isChunked)
        {
            dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                _columnByteOffsets[destinationColumnIndex] + destinationRow * size);
        }
        else
        {
            var (dstSegIdx, dstLocal) = GetSegmentAndLocal(destinationRow);
            dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_segments[dstSegIdx].Data),
                _columnByteOffsets[destinationColumnIndex] + dstLocal * size);
        }

        CopySmall(ref dstRef, ref srcRef, size);
    }

    internal unsafe void ReadComponentRaw(int columnIndex, int row, byte* destination)
        => CopyComponentRaw(columnIndex, row, destination, read: true);

    internal unsafe void WriteComponentRaw(int columnIndex, int row, byte* source)
        => CopyComponentRaw(columnIndex, row, source, read: false);

    private unsafe void CopyComponentRaw(int columnIndex, int row, byte* external, bool read)
    {
        int byteOff;
        ref var dataRef = ref Unsafe.NullRef<byte>();
        if (!_isChunked)
        {
            byteOff = _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex];
            dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
        }
        else
        {
            var (segIdx, localRow) = GetSegmentAndLocal(row);
            byteOff = _columnByteOffsets[columnIndex] + localRow * _elementSizes[columnIndex];
            dataRef = ref MemoryMarshal.GetArrayDataReference(_segments[segIdx].Data);
        }

        ref var storage = ref Unsafe.Add(ref dataRef, byteOff);
        var size = (uint)_elementSizes[columnIndex];

        if (read)
            Unsafe.CopyBlockUnaligned(ref *external, ref storage, size);
        else
            Unsafe.CopyBlockUnaligned(ref storage, ref *external, size);
    }

    internal void CopyColumnsFrom(Archetype source, int count)
    {
        if (!_signature.Equals(source._signature))
            throw new ArgumentException("Source archetype signature must match.", nameof(source));

        if (count < 0 || count > _count || count > source._count)
            throw new ArgumentOutOfRangeException(nameof(count));

        var componentCount = _signature.Count;
        if (componentCount == 0) return;

        for (var columnIndex = 0; columnIndex < componentCount; columnIndex++)
            CopyColumnFrom(source, columnIndex, count);
    }

    /// <summary>
    /// Writes column payload for the rows in <paramref name="sortedRows"/> order.
    /// Used by WorldSnapshot.Save to produce canonical byte layout independent
    /// of archetype's internal row order (which is affected by swap-remove).
    /// </summary>
    internal unsafe void WriteColumnOrderedTo(BinaryWriter writer, int columnIndex, ReadOnlySpan<int> sortedRows)
    {
        var size = _elementSizes[columnIndex];
        var count = sortedRows.Length;
        if (count == 0) return;
        var buf = new byte[count * size];
        fixed (byte* ptr = buf)
        {
            for (var i = 0; i < count; i++)
                ReadComponentRaw(columnIndex, sortedRows[i], ptr + i * size);
        }
        writer.Write(buf);
    }

    internal void ReadColumnFrom(BinaryReader reader, int columnIndex, int count)
    {
        if (!_isChunked)
        {
            reader.BaseStream.ReadExactly(GetColumnBytes(columnIndex, count));
            return;
        }
        var size = _elementSizes[columnIndex];
        var segIdx = 0;
        var remaining = count;
        while (remaining > 0)
        {
            var seg = _segments[segIdx];
            var take = Math.Min(remaining, seg.Count);
            var span = seg.Data.AsSpan(_columnByteOffsets[columnIndex], take * size);
            reader.BaseStream.ReadExactly(span);
            remaining -= take;
            segIdx++;
        }
    }

    internal Span<Entity> GetReservedEntities(int startRow, int count)
    {
        if (startRow < 0 || count < 0 || startRow + count > _count)
            throw new ArgumentOutOfRangeException(nameof(startRow));

        if (_isChunked)
            throw new InvalidOperationException(
                "GetReservedEntities is not supported for chunked archetypes. Use WriteEntityAt instead.");

        return _entities.AsSpan(startRow, count);
    }

    // ──────────────────────────────────────────────
    //  Internal helpers
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
    {
        ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
        var offsets = _columnByteOffsets;
        var sizes = _elementSizes;
        for (var index = 0; index < sizes.Length; index++)
        {
            var off = offsets[index];
            var size = sizes[index];
            ref var sourceRef = ref Unsafe.Add(ref dataRef, off + last * size);
            ref var destRef = ref Unsafe.Add(ref dataRef, off + row * size);
            CopySmall(ref destRef, ref sourceRef, size);
        }
    }

    private void CopySegmentColumn(int srcSegIdx, int srcLocalRow, int destSegIdx, int destLocalRow)
    {
        var offsets = _columnByteOffsets;
        var sizes = _elementSizes;
        ref var srcDataRef = ref MemoryMarshal.GetArrayDataReference(_segments[srcSegIdx].Data);
        ref var dstDataRef = ref MemoryMarshal.GetArrayDataReference(_segments[destSegIdx].Data);
        for (var index = 0; index < sizes.Length; index++)
        {
            var off = offsets[index];
            var size = sizes[index];
            ref var sourceRef = ref Unsafe.Add(ref srcDataRef, off + srcLocalRow * size);
            ref var destRef = ref Unsafe.Add(ref dstDataRef, off + destLocalRow * size);
            CopySmall(ref destRef, ref sourceRef, size);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopySmall(ref byte destination, ref byte source, int size)
    {
        switch (size)
        {
            case 1:
                destination = source;
                return;
            case 2:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<short>(ref source));
                return;
            case 4:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<int>(ref source));
                return;
            case 8:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                return;
            case 12:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8),
                    Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, 8)));
                return;
            case 16:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8),
                    Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref source, 8)));
                return;
            default:
                Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)size);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetColumnBytes(int columnIndex, int count)
    {
        return _data.AsSpan(_columnByteOffsets[columnIndex], count * _elementSizes[columnIndex]);
    }

    internal interface ISpanFeeder
    {
        void Feed(ReadOnlySpan<byte> span);
    }

    internal void FeedColumnData<TFeeder>(int columnIndex, int rowCount, ref TFeeder append)
        where TFeeder : struct, ISpanFeeder
    {
        var elemSize = _elementSizes[columnIndex];
        if (_isChunked)
        {
            var remaining = rowCount;
            for (var s = 0; s < _segmentCount && remaining > 0; s++)
            {
                var take = Math.Min(remaining, _segments[s].Count);
                append.Feed(_segments[s].Data.AsSpan(_columnByteOffsets[columnIndex], take * elemSize));
                remaining -= take;
            }
        }
        else
        {
            append.Feed(_data.AsSpan(_columnByteOffsets[columnIndex], rowCount * elemSize));
        }
    }

    // ──────────────────────────────────────────────
    //  Snapshot/restore helpers (Tier 1 rollback)
    // ──────────────────────────────────────────────

    internal int TotalDataBytes => _data?.Length ?? 0;

    internal void CopyDataTo(byte[] dest) => Array.Copy(_data, dest, _data.Length);

    internal void CopyDataFrom(byte[] src) => Array.Copy(src, _data, src.Length);

    internal void SetCount(int count) => _count = count;

    internal ref Segment GetSegmentRef(int index) => ref _segments[index];
    internal Segment GetSegment(int index) => _segments[index];

    internal void RebuildFlatEntities()
    {
        // Bumping the generation invalidates GetEntityStorage's cache; the
        // next read will rebuild _cachedFlatEntities from current segments.
        _flatEntitiesGeneration++;
    }

    internal void ResetCount()
    {
        _count = 0;
        if (_isChunked)
        {
            for (var i = 0; i < _segmentCount; i++)
                _segments[i].Count = 0;
        }
    }

    internal void FeedRowData<TFeeder>(int columnIndex, int globalRow, ref TFeeder feed)
        where TFeeder : struct, ISpanFeeder
    {
        var elemSize = _elementSizes[columnIndex];
        if (_isChunked)
        {
            var (segIdx, localRow) = GetSegmentAndLocal(globalRow);
            feed.Feed(_segments[segIdx].Data.AsSpan(_columnByteOffsets[columnIndex] + localRow * elemSize, elemSize));
        }
        else
        {
            feed.Feed(_data.AsSpan(_columnByteOffsets[columnIndex] + globalRow * elemSize, elemSize));
        }
    }

    private unsafe void CopyColumnFrom(Archetype source, int columnIndex, int count)
    {
        var elemSize = _elementSizes[columnIndex];

        if (!source._isChunked && !_isChunked)
        {
            var byteCount = checked((uint)(count * elemSize));
            ref var srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._data), source._columnByteOffsets[columnIndex]);
            ref var dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), _columnByteOffsets[columnIndex]);
            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, byteCount);
            return;
        }

        var remaining = count;
        var srcSegIdx = 0;
        var srcConsumed = 0;
        var dstSegIdx = 0;
        var dstConsumed = 0;

        while (remaining > 0)
        {
            var srcAvailable = source._isChunked
                ? source._segments[srcSegIdx].Count - srcConsumed
                : remaining;
            var dstAvailable = _isChunked
                ? _segments[dstSegIdx].Count - dstConsumed
                : remaining;
            var take = Math.Min(remaining, Math.Min(srcAvailable, dstAvailable));

            ref byte srcRef = ref Unsafe.NullRef<byte>();
            if (!source._isChunked)
            {
                var consumedTotal = count - remaining;
                srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._data),
                    source._columnByteOffsets[columnIndex] + consumedTotal * elemSize);
            }
            else
            {
                srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._segments[srcSegIdx].Data),
                    source._columnByteOffsets[columnIndex] + srcConsumed * elemSize);
            }

            ref byte dstRef = ref Unsafe.NullRef<byte>();
            if (!_isChunked)
            {
                var consumedTotal = count - remaining;
                dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                    _columnByteOffsets[columnIndex] + consumedTotal * elemSize);
            }
            else
            {
                dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_segments[dstSegIdx].Data),
                    _columnByteOffsets[columnIndex] + dstConsumed * elemSize);
            }

            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, checked((uint)(take * elemSize)));

            remaining -= take;
            if (source._isChunked)
            {
                srcConsumed += take;
                if (srcConsumed >= source._segments[srcSegIdx].Count) { srcSegIdx++; srcConsumed = 0; }
            }
            if (_isChunked)
            {
                dstConsumed += take;
                if (dstConsumed >= _segments[dstSegIdx].Count) { dstSegIdx++; dstConsumed = 0; }
            }
        }
    }

    private static (byte[] Data, int[] ColumnByteOffsets, int[] ElementSizes) CreateStorage(
        Signature signature, Type[] componentTypes, int capacity, bool pinned = true)
    {
        var componentCount = signature.Count;
        var columnByteOffsets = new int[componentCount];
        var elementSizes = new int[componentCount];

        if (componentCount == 0)
            return (Array.Empty<byte>(), columnByteOffsets, elementSizes);

        if (componentTypes.Length != componentCount)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        var totalBytes = 0;
        for (var index = 0; index < componentCount; index++)
        {
            ThrowIfManagedComponent(componentTypes[index]);
            var elementSize = ComponentSizeCache.GetSize(componentTypes[index]);
            AssertPositiveElementSize(elementSize, componentTypes[index]);
            totalBytes = AlignUp(totalBytes, Math.Min(elementSize, 8));
            columnByteOffsets[index] = totalBytes;
            elementSizes[index] = elementSize;
            totalBytes += elementSize * capacity;
        }

        var data = pinned
            ? GC.AllocateArray<byte>(totalBytes, pinned: true)
            : GC.AllocateArray<byte>(totalBytes);
        return (data, columnByteOffsets, elementSizes);
    }

    private static byte[] CreateStorageBytes(
        Signature signature, Type[] componentTypes, int capacity)
    {
        var (data, _, _) = CreateStorage(signature, componentTypes, capacity, pinned: false);
        return data;
    }

    private static void ThrowIfManagedComponent(Type type)
    {
        if (!ManagedReferenceCheck.IsManaged(type))
            return;

        throw new NotSupportedException(
            $"Component {type.FullName ?? type.Name} contains managed references " +
            "and cannot be stored in flat byte chunks.");
    }

    [Conditional("DEBUG")]
    private static void AssertPositiveElementSize(int elementSize, Type componentType)
    {
        if (elementSize <= 0)
            throw new InvalidOperationException($"Component '{componentType.Name}' has non-positive size {elementSize}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfChunked()
    {
        if (_isChunked)
            throw new InvalidOperationException(
                "This operation is not supported for chunked archetypes. Use the per-segment API via ChunkView instead.");
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

}
