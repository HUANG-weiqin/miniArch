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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvalidateFlatEntityCache()
    {
        _flatEntitiesGeneration++;
    }

    /// <summary>
    /// Returns a managed reference to a single cell in any archetype (flat or chunked).
    /// Collapses dual-mode knowledge (IsChunked check) to a single point.
    /// Must be inlined — hot path across archetype migration (CopyComponent).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte GetColumnRef(Archetype arch, int columnIndex, int row, int elementSize)
    {
        if (!arch.IsChunked)
        {
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arch._data),
                arch._columnByteOffsets[columnIndex] + row * elementSize);
        }
        var (segIdx, localRow) = arch.GetSegmentAndLocal(row);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arch._segments[segIdx].Data),
            arch._columnByteOffsets[columnIndex] + localRow * elementSize);
    }

    // ──────────────────────────────────────────────
    //  Conversion & segment growth
    // ──────────────────────────────────────────────

    // Promote non-chunked storage to chunked segments. Guarantees the
    // chunked invariant: every segment owns exactly _segmentCapacity entity
    // slots and column offsets are based on _segmentCapacity.
    //
    // Single path: entities are split across standard segments and column data
    // is rebased onto segment-capacity offsets, regardless of whether the flat
    // buffer happened to match _segmentCapacity. The old flat buffer is
    // abandoned. (Previously a "fast path" wrapped the flat arrays as segment[0]
    // when sizes matched; unifying on the copy path keeps the promotion
    // invariant enforced uniformly and removes a mode branch.)
    private void ConvertToChunked()
    {
        var segOffsets = ComputeColumnLayout(_elementSizes, _segmentCapacity).Offsets;
        var segCount = Math.Max(1, (_count + _segmentCapacity - 1) / _segmentCapacity);
        _segments = new Segment[segCount];
        _segmentCount = segCount;

        for (var s = 0; s < segCount; s++)
        {
            var segStart = s * _segmentCapacity;
            var rowsInSeg = Math.Min(_segmentCapacity, _count - segStart);

            var segEntities = new Entity[_segmentCapacity];
            Array.Copy(_entities, segStart, segEntities, 0, rowsInSeg);

            var segData = CreateStorageBytes(_signature, _componentTypes, _segmentCapacity);
            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = rowsInSeg * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                    _columnByteOffsets[col] + segStart * elemSize);
                ref var dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segData),
                    segOffsets[col]);
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }

            _segments[s] = new Segment
            {
                Entities = segEntities,
                Data = segData,
                Count = rowsInSeg
            };
        }

        _columnByteOffsets = segOffsets;
        _entities = null!;
        _data = null!;

        AssertConvertedInvariants();
        AssertSegmentInvariants();
    }

    [Conditional("DEBUG")]
    private void AssertConvertedInvariants()
    {
        Debug.Assert(_entities is null, "Flat entities array must be null after promotion.");
        Debug.Assert(_data is null, "Flat data buffer must be null after promotion.");
        Debug.Assert(_segments is not null, "Segments must be non-null after promotion.");
        for (var i = 0; i < _segmentCount; i++)
            Debug.Assert(_segments[i].Entities.Length == _segmentCapacity,
                $"Segment {i} entity capacity ({_segments[i].Entities.Length}) != _segmentCapacity ({_segmentCapacity}).");
        var total = 0;
        for (var i = 0; i < _segmentCount; i++)
            total += _segments[i].Count;
        Debug.Assert(total == _count,
            $"Segment count sum ({total}) != _count ({_count}) after promotion.");
    }

    [Conditional("DEBUG")]
    private void AssertSegmentInvariants()
    {
        if (!IsChunked) return;
        var total = 0;
        var seenEmpty = false;
        for (var i = 0; i < _segmentCount; i++)
        {
            ref var seg = ref _segments[i];
            Debug.Assert(seg.Entities.Length == _segmentCapacity,
                $"Segment {i} entity capacity ({seg.Entities.Length}) != _segmentCapacity ({_segmentCapacity}).");
            Debug.Assert((uint)seg.Count <= (uint)seg.Entities.Length,
                $"Segment {i} count ({seg.Count}) exceeds capacity ({seg.Entities.Length}).");
            // Non-empty segments must be front-packed. A hole *inside* a
            // segment (Count < Capacity) is legal — RemoveAt creates them and
            // AllocateRows fills the first non-full segment. Trailing empty
            // segments (Count == 0) are also legal (EnsureCapacity/GrowChunked
            // pre-allocate them). What is NOT legal is an empty segment
            // preceding a non-empty one: AllocateRows assigns globalRow = _count
            // and GetSegmentAndLocal maps globalRow by fixed segCap, so a front
            // gap would desync the row→segment mapping.
            if (seg.Count == 0)
                seenEmpty = true;
            else
                Debug.Assert(!seenEmpty,
                    $"Empty segment precedes non-empty segment {i}; non-empty segments must be front-packed.");
            total += seg.Count;
        }
        Debug.Assert(total == _count,
            $"Sum of segment counts ({total}) != _count ({_count}).");
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
        InvalidateFlatEntityCache();
    }

    // ──────────────────────────────────────────────
    //  EnsureCapacity
    // ──────────────────────────────────────────────

    internal void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= Capacity) return;

        if (!IsChunked && _capacity * 2 > _segmentCapacity)
        {
            ConvertToChunked();
            if (requiredCapacity <= Capacity) return;
            GrowChunked(requiredCapacity - _count);
            return;
        }

        if (!IsChunked)
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
        var row = AllocateRows(1);
        WriteEntityAt(row, entity);
        return row;
    }

    // ──────────────────────────────────────────────
    //  AllocateRows
    // ──────────────────────────────────────────────

    internal int AllocateRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
            return _count;

        EnsureCapacity(_count + count);

        if (!IsChunked)
        {
            var row = _count;
            _count += count;
            return row;
        }

        var startRow = _count;
        var remaining = count;
        while (remaining > 0)
        {
            // Find the first segment with available capacity.
            var segIdx = _segmentCount - 1;
            for (var i = 0; i < _segmentCount; i++)
            {
                if (_segments[i].Count < _segments[i].Entities.Length)
                { segIdx = i; break; }
            }
            ref var seg = ref _segments[segIdx];
            var available = seg.Entities.Length - seg.Count;
            if (available == 0)
            {
                EnsureCapacity(_count + remaining);
                continue;
            }
            var take = Math.Min(remaining, available);
            seg.Count += take;
            _count += take;
            remaining -= take;
        }
        InvalidateFlatEntityCache();
        AssertSegmentInvariants();
        return startRow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteEntityAt(int globalRow, Entity entity)
    {
        if (!IsChunked)
        {
            _entities[globalRow] = entity;
            return;
        }
        var (segIdx, localRow) = GetSegmentAndLocal(globalRow);
        _segments[segIdx].Entities[localRow] = entity;
        InvalidateFlatEntityCache();
        AssertSegmentInvariants();
    }

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
        if (!IsChunked)
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
            InvalidateFlatEntityCache();
            AssertSegmentInvariants();
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
        InvalidateFlatEntityCache();
        AssertSegmentInvariants();
        return true;
    }

    // ──────────────────────────────────────────────
    //  Entity access
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        if (!IsChunked)
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
        if (!IsChunked)
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
        AssertFlatCacheConsistent();
        return _cachedFlatEntities!;
    }

    [Conditional("DEBUG")]
    private void AssertFlatCacheConsistent()
    {
        if (!IsChunked || _cachedFlatEntities is null) return;
        if (_cachedFlatEntitiesGeneration != _flatEntitiesGeneration) return;
        var total = 0;
        for (var i = 0; i < _segmentCount; i++)
            total += _segments[i].Count;
        Debug.Assert(_cachedFlatEntities.Length >= total,
            $"Flat cache size {_cachedFlatEntities.Length} < total segment count {total}.");

        // Spot-check: first N=32 cached entity ids match segment order.
        var check = Math.Min(32, _count);
        var flatIdx = 0;
        var segIdx = 0;
        var localIdx = 0;
        for (var i = 0; i < check; i++)
        {
            while (localIdx >= _segments[segIdx].Count)
            {
                segIdx++;
                localIdx = 0;
                Debug.Assert(segIdx < _segmentCount,
                    "Segment sum ran out before reaching flat cache check length — " +
                    "indicates segment invariant already broken.");
            }
            Debug.Assert(_cachedFlatEntities[flatIdx++].Equals(_segments[segIdx].Entities[localIdx++]));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity GetEntity(int row)
    {
        if (!IsChunked)
            return _entities[row];

        var (segIdx, localRow) = GetSegmentAndLocal(row);
        return _segments[segIdx].Entities[localRow];
    }

    // ──────────────────────────────────────────────
    //  Component access
    // ──────────────────────────────────────────────

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetFlatComponentRef<T>(int columnIndex) where T : unmanaged
    {
        Debug.Assert(!IsChunked, "GetFlatComponentRef requires non-chunked archetype.");
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            _columnByteOffsets[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRefAt<T>(int columnIndex, int row) where T : unmanaged
    {
        if (!IsChunked)
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

    /// <summary>
    /// Bumps the per-column version when tracking is active.
    /// Must be inlineable — called on every write chokepoint hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkColumnWritten(int columnIndex)
    {
        if (_anyTrackingActive)
            _columnVersions![columnIndex] = ++_owner!._writeEpoch;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value) where T : unmanaged
    {
        ref var target = ref GetComponentRefAt<T>(columnIndex, row);
        target = value;
        MarkColumnWritten(columnIndex);
    }

    /// <summary>
    /// Write-only variant with no tracking overhead. Used on the hot path when
    /// <see cref="World.IsChangeTrackingActive"/> is false. No version bump, no field load.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTypedNoTrack<T>(int columnIndex, int row, in T value) where T : unmanaged
    {
        ref var target = ref GetComponentRefAt<T>(columnIndex, row);
        target = value;
    }

    /// <summary>
    /// Returns the byte offset of the component column at the given column index.
    /// Used by callers that cache the offset outside a hot loop to avoid
    /// per-iteration array lookups.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetColumnByteOffset(int columnIndex) => _columnByteOffsets[columnIndex];

    /// <summary>
    /// Sets a component value using a pre-computed byte offset, skipping the
    /// <see cref="IsChunked"/> branch and per-call array lookups for
    /// <c>_columnByteOffsets</c> and <c>_elementSizes</c>.
    /// <para/>
    /// <b>Precondition:</b> the archetype must be non-chunked
    /// (<see cref="IsChunked"/> = false). The caller is responsible for checking
    /// <see cref="IsChunked"/> once and caching the result.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtFlat<T>(int columnIndex, int byteOffset, int row, in T value) where T : unmanaged
    {
        Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            byteOffset + row * Unsafe.SizeOf<T>())) = value;
        MarkColumnWritten(columnIndex);
    }

    /// <summary>
    /// Flat write-only variant with no tracking overhead. Used on the hot path when
    /// <see cref="World.IsChangeTrackingActive"/> is false. No version bump, no field load.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtFlatNoTrack<T>(int columnIndex, int byteOffset, int row, in T value) where T : unmanaged
    {
        Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            byteOffset + row * Unsafe.SizeOf<T>())) = value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row) where T : unmanaged
    {
        return GetComponentRefAt<T>(columnIndex, row);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> GetFlatComponentSpanAt<T>(int columnIndex) where T : unmanaged
    {
        Debug.Assert(!IsChunked, "GetFlatComponentSpanAt requires non-chunked archetype.");
        return MemoryMarshal.CreateSpan(ref GetFlatComponentRef<T>(columnIndex), _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> GetFlatComponentSpan<T>(ComponentType component) where T : unmanaged
    {
        Debug.Assert(!IsChunked, "GetFlatComponentSpan requires non-chunked archetype.");
        var columnIndex = GetComponentIndex(component);
        return GetFlatComponentSpanAt<T>(columnIndex);
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

    /// <summary>
    /// Fixed entity capacity per segment (power of two), computed from
    /// component sizes at archetype creation. All segments share the
    /// same capacity.
    /// </summary>
    internal int SegmentCapacity => _segmentCapacity;

    /// <summary>
    /// Returns the per-entity byte size of the component at the given column index.
    /// </summary>
    internal int ComponentElementSize(int columnIndex) => _elementSizes[columnIndex];

    internal int GetSegmentCount(int segmentIndex) => _segments[segmentIndex].Count;

    internal ReadOnlySpan<Entity> GetSegmentEntities(int segmentIndex) =>
        _segments[segmentIndex].Entities.AsSpan(0, _segments[segmentIndex].Count);

    /// <summary>
    /// Returns a struct enumerator that yields one <see cref="ChunkView"/> per
    /// segment (or a single view for non-chunked archetypes). Zero-allocation.
    /// </summary>
    internal ChunkViewEnumerator AsChunkViews() => new(this);

    /// <summary>
    /// Zero-allocation struct enumerator over <see cref="ChunkView"/> for a single archetype.
    /// Non-chunked: yields one view (segmentIndex = NonChunkedSegmentIndex).
    /// Chunked: yields one view per segment.
    /// </summary>
    internal struct ChunkViewEnumerator
    {
        private readonly Archetype _archetype;
        private int _index; // -1 before first MoveNext

        internal ChunkViewEnumerator(Archetype archetype)
        {
            _archetype = archetype;
            _index = -1;
        }

        public ChunkViewEnumerator GetEnumerator() => this;

        public ChunkView Current => _archetype.IsChunked
            ? new ChunkView(_archetype, _index)
            : new ChunkView(_archetype);

        public bool MoveNext()
        {
            if (!_archetype.IsChunked)
            {
                if (_index == -1) { _index = 0; return true; }
                return false;
            }
            _index++;
            return _index < _archetype._segmentCount;
        }
    }

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
        ref var srcRef = ref GetColumnRef(source, sourceColumnIndex, sourceRow, size);
        ref var dstRef = ref GetColumnRef(this, destinationColumnIndex, destinationRow, size);
        CopySmall(ref dstRef, ref srcRef, size);
    }

    internal unsafe void ReadComponentRaw(int columnIndex, int row, byte* destination)
        => CopyComponentRaw(columnIndex, row, destination, read: true);

    internal unsafe void WriteComponentRaw(int columnIndex, int row, byte* source)
        => CopyComponentRaw(columnIndex, row, source, read: false);

    /// <summary>
    /// Raw write-only variant with no tracking overhead.
    /// </summary>
    internal unsafe void WriteComponentRawNoTrack(int columnIndex, int row, byte* source)
        => CopyComponentRawNoTrack(columnIndex, row, source);

    /// <summary>
    /// Returns a read-only span over the raw bytes of a single component cell.
    /// Zero-allocation; the span points directly into the archetype's backing store.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> GetComponentBytes(int columnIndex, int row)
    {
        var elemSize = _elementSizes[columnIndex];
        if (!IsChunked)
            return _data.AsSpan(_columnByteOffsets[columnIndex] + row * elemSize, elemSize);
        var (segIdx, localRow) = GetSegmentAndLocal(row);
        return _segments[segIdx].Data.AsSpan(
            _columnByteOffsets[columnIndex] + localRow * elemSize, elemSize);
    }

    private unsafe void CopyComponentRaw(int columnIndex, int row, byte* external, bool read)
    {
        ref var storage = ref GetColumnRef(this, columnIndex, row, _elementSizes[columnIndex]);
        var size = (uint)_elementSizes[columnIndex];
        if (read)
            Unsafe.CopyBlockUnaligned(ref *external, ref storage, size);
        else
        {
            Unsafe.CopyBlockUnaligned(ref storage, ref *external, size);
            MarkColumnWritten(columnIndex);
        }
    }

    private unsafe void CopyComponentRawNoTrack(int columnIndex, int row, byte* external)
    {
        ref var storage = ref GetColumnRef(this, columnIndex, row, _elementSizes[columnIndex]);
        var size = (uint)_elementSizes[columnIndex];
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
        if (!IsChunked)
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

    internal Span<Entity> GetFlatReservedEntities(int startRow, int count)
    {
        Debug.Assert(!IsChunked, "GetFlatReservedEntities requires non-chunked archetype.");
        if (startRow < 0 || count < 0 || startRow + count > _count)
            throw new ArgumentOutOfRangeException(nameof(startRow));
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
        if (IsChunked)
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

    internal void SetCount(int count) => _count = count;

    internal ref Segment GetSegmentRef(int index) => ref _segments[index];


    internal void RebuildFlatEntities()
    {
        // Bumping the generation invalidates GetEntityStorage's cache; the
        // next read will rebuild _cachedFlatEntities from current segments.
        InvalidateFlatEntityCache();
        AssertSegmentInvariants();
    }

    internal void ResetCount()
    {
        _count = 0;
        if (IsChunked)
        {
            for (var i = 0; i < _segmentCount; i++)
                _segments[i].Count = 0;
            InvalidateFlatEntityCache();
        }
    }

    /// <summary>
    /// Column byte offsets valid for the current storage layout. Snapshot at
    /// capture time so a restore can translate across a layout change (e.g.
    /// promotion from non-chunked to chunked rebases the offsets).
    /// </summary>
    internal int[] ColumnByteOffsets => _columnByteOffsets;

    /// <summary>
    /// Restores entity and component data from a non-chunked flat backup.
    /// Handles the case where this archetype was promoted to chunked storage
    /// after the backup was taken: entities and per-column data are split
    /// across standard segments, translating from <paramref name="srcOffsets"/>
    /// (backup-time layout) to the current segment-capacity layout.
    /// </summary>
    internal void RestoreFlatBackup(Entity[] srcEntities, byte[] srcData, int[] srcOffsets, int count)
    {
        EnsureCapacity(count);

        if (!IsChunked)
        {
            Debug.Assert((uint)count <= (uint)_capacity,
                $"Backup count ({count}) exceeds archetype capacity ({_capacity}).");
            Array.Copy(srcEntities, _entities, count);
            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = count * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref Unsafe.Add(
                    ref MemoryMarshal.GetArrayDataReference(srcData), srcOffsets[col]);
                ref var dstRef = ref Unsafe.Add(
                    ref MemoryMarshal.GetArrayDataReference(_data), _columnByteOffsets[col]);
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }
            _count = count;
            return;
        }

        // Chunked: zero all existing segment counts, then distribute the flat
        // backup across segments using the current segment-capacity offsets.
        Debug.Assert(count <= _segmentCount * _segmentCapacity,
            $"Backup count ({count}) exceeds chunked capacity ({_segmentCount * _segmentCapacity}).");
        for (var i = 0; i < _segmentCount; i++)
            _segments[i].Count = 0;

        var remaining = count;
        var segIdx = 0;
        while (remaining > 0)
        {
            if (segIdx >= _segmentCount)
                GrowChunked(remaining);

            ref var seg = ref _segments[segIdx];
            var take = Math.Min(_segmentCapacity, remaining);
            var segStart = count - remaining;

            Array.Copy(srcEntities, segStart, seg.Entities, 0, take);

            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = take * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref Unsafe.Add(
                    ref MemoryMarshal.GetArrayDataReference(srcData),
                    srcOffsets[col] + segStart * elemSize);
                ref var dstRef = ref Unsafe.Add(
                    ref MemoryMarshal.GetArrayDataReference(seg.Data),
                    _columnByteOffsets[col]);
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }

            seg.Count = take;
            remaining -= take;
            segIdx++;
        }
        _count = count;
        InvalidateFlatEntityCache();
        AssertSegmentInvariants();
    }

    internal void FeedRowData<TFeeder>(int columnIndex, int globalRow, ref TFeeder feed)
        where TFeeder : struct, ISpanFeeder
    {
        var elemSize = _elementSizes[columnIndex];
        if (IsChunked)
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

        // Fast path: both flat — single CopyBlock for the whole column.
        // NOTE: cannot use GetColumnRef here because this is a batch column copy
        // that computes segment-aware batch offsets. GetColumnRef is per-row and
        // would destroy the CopyBlock batching advantage.
        if (!source.IsChunked && !IsChunked)
        {
            var byteCount = checked((uint)(count * elemSize));
            ref var srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._data), source._columnByteOffsets[columnIndex]);
            ref var dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), _columnByteOffsets[columnIndex]);
            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, byteCount);
            return;
        }

        // Hybrid/chunked path: iterate in segment-sized batches.
        var remaining = count;
        var srcSegIdx = 0;
        var srcConsumed = 0;
        var dstSegIdx = 0;
        var dstConsumed = 0;

        while (remaining > 0)
        {
            var srcAvailable = source.IsChunked
                ? source._segments[srcSegIdx].Count - srcConsumed
                : remaining;
            var dstAvailable = IsChunked
                ? _segments[dstSegIdx].Count - dstConsumed
                : remaining;
            var take = Math.Min(remaining, Math.Min(srcAvailable, dstAvailable));
            var consumedTotal = count - remaining;

            ref var srcRef = ref source.IsChunked
                ? ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._segments[srcSegIdx].Data),
                    source._columnByteOffsets[columnIndex] + srcConsumed * elemSize)
                : ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source._data),
                    source._columnByteOffsets[columnIndex] + consumedTotal * elemSize);

            ref var dstRef = ref IsChunked
                ? ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_segments[dstSegIdx].Data),
                    _columnByteOffsets[columnIndex] + dstConsumed * elemSize)
                : ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                    _columnByteOffsets[columnIndex] + consumedTotal * elemSize);

            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, checked((uint)(take * elemSize)));

            remaining -= take;
            if (source.IsChunked)
            {
                srcConsumed += take;
                if (srcConsumed >= source._segments[srcSegIdx].Count) { srcSegIdx++; srcConsumed = 0; }
            }
            if (IsChunked)
            {
                dstConsumed += take;
                if (dstConsumed >= _segments[dstSegIdx].Count) { dstSegIdx++; dstConsumed = 0; }
            }
        }
    }

    private static (byte[] Data, int[] ColumnByteOffsets, int[] ElementSizes) CreateStorage(
        Signature signature, Type[] componentTypes, int capacity)
    {
        var componentCount = signature.Count;
        if (componentCount == 0)
            return (Array.Empty<byte>(), Array.Empty<int>(), Array.Empty<int>());

        if (componentTypes.Length != componentCount)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        var elementSizes = new int[componentCount];
        for (var index = 0; index < componentCount; index++)
        {
            ThrowIfManagedComponent(componentTypes[index]);
            var elementSize = ComponentSizeCache.GetSize(componentTypes[index]);
            AssertPositiveElementSize(elementSize, componentTypes[index]);
            elementSizes[index] = elementSize;
        }

        var (columnByteOffsets, totalBytes) = ComputeColumnLayout(elementSizes, capacity);
        var data = GC.AllocateArray<byte>(totalBytes);
        return (data, columnByteOffsets, elementSizes);
    }

    // Computes per-column byte offsets and total buffer size for a given
    // per-entity capacity. Pure function over element sizes + capacity; the
    // same offsets are valid for every segment of a chunked archetype.
    private static (int[] Offsets, int TotalBytes) ComputeColumnLayout(int[] elementSizes, int capacity)
    {
        var count = elementSizes.Length;
        var offsets = new int[count];
        var totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            var elementSize = elementSizes[index];
            totalBytes = AlignUp(totalBytes, Math.Min(elementSize, 8));
            offsets[index] = totalBytes;
            totalBytes += elementSize * capacity;
        }
        return (offsets, totalBytes);
    }

    private static byte[] CreateStorageBytes(
        Signature signature, Type[] componentTypes, int capacity)
    {
        var (data, _, _) = CreateStorage(signature, componentTypes, capacity);
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

    private static void AssertPositiveElementSize(int elementSize, Type componentType)
    {
        if (elementSize <= 0)
            throw new InvalidOperationException($"Component '{componentType.Name}' has non-positive size {elementSize}.");
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

}
