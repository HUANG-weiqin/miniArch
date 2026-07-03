using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Cached discovery of <see cref="Entity"/> field byte offsets within
/// <c>unmanaged</c> component types, plus in-place placeholder resolution.
/// </summary>
/// <remarks>
/// Component types must use sequential or explicit layout (the default for
/// blittable structs in .NET). Auto-layout types with <see cref="Entity"/>
/// fields throw <see cref="InvalidOperationException"/> —apply
/// <c>[StructLayout(LayoutKind.Sequential)]</c> to the type.
///
/// Nested structs containing <see cref="Entity"/> fields are NOT scanned
/// recursively; only direct fields of type <see cref="Entity"/> are found.
/// </remarks>
internal static class EntityFieldResolver
{
    // Per-type-id cache: offsets[id] = int[] of byte offsets, or null if
    // the type has no Entity fields (or has been checked).
    // All access is done under s_gate (reads outside the lock use
    // Volatile.Read to ensure publication visibility).
    private static int[][]? s_offsetsByTypeId;

    private static readonly object s_gate = new();

    /// <summary>
    /// Returns the byte offsets of all <see cref="Entity"/> fields within
    /// the component type identified by <paramref name="typeId"/>.
    /// </summary>
    internal static ReadOnlySpan<int> GetOffsets(ComponentType typeId)
    {
        var id = typeId.Value;
        var arr = Volatile.Read(ref s_offsetsByTypeId);
        if (arr != null && (uint)id < (uint)arr.Length)
        {
            var result = arr[id];
            if (result is not null)
                return result;
        }
        return ScanAndCache(typeId);
    }

    private static int[]? ScanAndCache(ComponentType typeId)
    {
        var id = typeId.Value;
        var clrType = ComponentRegistry.Shared.GetType(typeId);
        var offsets = ScanType(clrType);

        lock (s_gate)
        {
            var arr = Volatile.Read(ref s_offsetsByTypeId);
            if (arr is null || id >= arr.Length)
            {
                var newLen = arr is null ? Math.Max(id + 1, 32) : Math.Max(id + 1, arr.Length * 2);
                Array.Resize(ref arr, newLen);
                Volatile.Write(ref s_offsetsByTypeId, arr);
            }
            // If another thread already populated this slot, return its result.
            if (arr[id] is null)
                arr[id] = offsets ?? [];
            return arr[id];
        }
    }

    private static int[]? ScanType(Type type)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var entityFieldCount = CountEntityFields(fields);

        if (entityFieldCount == 0)
            return []; // no Entity fields found

        // Auto-layout: field ordering is undefined — refuse to resolve.
        var layout = type.StructLayoutAttribute;
        if (layout is not null && layout.Value == LayoutKind.Auto)
        {
            throw new InvalidOperationException(
                $"Component type '{type.FullName ?? type.Name}' contains Entity field(s) but uses " +
                $"LayoutKind.Auto. Apply [StructLayout(LayoutKind.Sequential)] (or Explicit) to enable " +
                $"automatic placeholder resolution embedded Entity references.");
        }

        var result = new int[entityFieldCount];
        var idx = 0;
        foreach (var f in fields)
        {
            if (f.FieldType != typeof(Entity))
                continue;
            result[idx++] = (int)Marshal.OffsetOf(type, f.Name)!;
        }

        return result;
    }

    private static int CountEntityFields(FieldInfo[] fields)
    {
        var count = 0;
        foreach (var f in fields)
        {
            if (f.FieldType == typeof(Entity))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Resolves placeholder <see cref="Entity"/> values found at known field
    /// offsets within <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Raw component data (mutable span).</param>
    /// <param name="typeId">Component type identifier.</param>
    /// <param name="resolveMap">
    /// Table indexed by placeholder <c>seq (Version)</c> — elements with
    /// <c>Id &gt;= 0</c> are the resolved real entity; others are skipped.
    /// </param>
    internal static void ResolveInPlace(Span<byte> data, ComponentType typeId, ReadOnlySpan<Entity> resolveMap)
    {
        var offsets = GetOffsets(typeId);
        if (offsets.IsEmpty)
            return;

        ref var dataRef = ref MemoryMarshal.GetReference(data);
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = offsets[i];
            // Use ReadUnaligned to stay safe on ARM / Pack=1 structs.
            var entity = Unsafe.ReadUnaligned<Entity>(ref Unsafe.Add(ref dataRef, offset));
            if (entity.IsPlaceholder)
            {
                var seq = entity.Version;
                if ((uint)seq < (uint)resolveMap.Length)
                {
                    var resolved = resolveMap[seq];
                    if (resolved.Id >= 0)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dataRef, offset), resolved);
                }
            }
        }
    }
}
