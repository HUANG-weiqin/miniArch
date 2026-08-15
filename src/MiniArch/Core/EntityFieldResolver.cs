using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Cached discovery of <see cref="Entity"/> field byte offsets within
/// unmanaged component types, plus in-place placeholder resolution.
/// </summary>
/// <remarks>
/// Entity-bearing component graphs may contain sequential or explicit value
/// structs at any depth, including private record backing fields and
/// <see cref="InlineArrayAttribute"/> elements. Offsets are the actual CLR
/// managed value-layout offsets, not marshaling offsets. Exact explicit-layout
/// aliases are deduplicated; partially overlapping Entity ranges are rejected.
///
/// Any <see cref="LayoutKind.Auto"/> type in an Entity-bearing graph is
/// rejected because its field layout is not a deterministic component contract.
/// Auto-layout types without Entity descendants are not rejected by this
/// resolver.
/// </remarks>
internal static class EntityFieldResolver
{
    // Per-type-id cache: offsets[id] = canonical sorted byte offsets, or an
    // empty array if the type has no Entity fields (or has been checked).
    // All access is done under s_gate (reads outside the lock use
    // Volatile.Read to ensure publication visibility).
    private static int[][]? s_offsetsByTypeId;

    private static readonly object s_gate = new();

    /// <summary>
    /// Returns the canonical, strictly ascending byte offsets of all
    /// <see cref="Entity"/> fields within the component type identified by
    /// <paramref name="typeId"/>.
    /// </summary>
    internal static ReadOnlySpan<int> GetOffsets(ComponentType typeId)
    {
        var id = typeId.Value;
        var arr = Volatile.Read(ref s_offsetsByTypeId);
        if (arr is not null && (uint)id < (uint)arr.Length)
        {
            var result = Volatile.Read(ref arr[id]);
            if (result is not null)
                return result;
        }
        return ScanAndCache(typeId);
    }

    private static int[] ScanAndCache(ComponentType typeId)
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
            }
            // Populate the slot BEFORE publishing a resized outer array so a fast
            // reader can never observe a non-null slot with unpublished contents.
            var slot = Volatile.Read(ref arr[id]);
            if (slot is null)
            {
                Volatile.Write(ref arr[id], offsets);
                Volatile.Write(ref s_offsetsByTypeId, arr);
            }
            return slot ?? offsets;
        }
    }

    private static int[] ScanType(Type rootType)
    {
        // Layout validation is deliberately a separate first pass. In
        // particular, a sequential outer type containing an Auto-layout Entity
        // descendant must report the Auto-layout contract error before offset
        // discovery attempts to inspect any field.
        // This set classifies subtrees for pruning only; it is not a visited set.
        // Collection still traverses every occurrence at its own absolute base.
        var entityBearingTypes = new HashSet<Type>();
        var containsEntity = ValidateEntityBearingLayout(
            rootType,
            rootType,
            FormatType(rootType),
            new HashSet<Type>(),
            entityBearingTypes);
        if (!containsEntity)
            return [];

        var discovered = new List<int>();
        CollectOffsets(
            rootType,
            0,
            rootType,
            FormatType(rootType),
            new HashSet<Type>(),
            entityBearingTypes,
            discovered);

        var result = discovered.ToArray();
        Array.Sort(result);
        var uniqueCount = 0;
        for (var i = 0; i < result.Length; i++)
        {
            if (uniqueCount == 0 || result[i] != result[uniqueCount - 1])
                result[uniqueCount++] = result[i];
        }
        if (uniqueCount != result.Length)
            Array.Resize(ref result, uniqueCount);

        var componentSize = ComponentSizeCache.GetSize(rootType);
        var entitySize = Unsafe.SizeOf<Entity>();
        for (var i = 0; i < result.Length; i++)
        {
            var offset = result[i];
            if (offset < 0 || (long)offset + entitySize > componentSize)
            {
                throw new InvalidOperationException(
                    $"Component type '{FormatType(rootType)}' produced invalid Entity " +
                    $"offset {offset}; component size is {componentSize} bytes.");
            }
            if (i > 0 && offset < (long)result[i - 1] + entitySize)
            {
                throw new InvalidOperationException(
                    $"Component type '{FormatType(rootType)}' has partially overlapping " +
                    $"Entity fields at offsets {result[i - 1]} and {offset}. Exact aliases " +
                    "are supported, but partially overlapping Entity values cannot be resolved safely.");
            }
        }

        return result;
    }

    private static bool ValidateEntityBearingLayout(
        Type type,
        Type rootType,
        string path,
        HashSet<Type> recursionStack,
        HashSet<Type> entityBearingTypes)
    {
        if (type == typeof(Entity))
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;
        if (!recursionStack.Add(type))
            return false;

        try
        {
            bool containsEntity;
            if (TryGetInlineArray(type, out _, out var elementField))
            {
                // All elements have the same type and layout. Validate the
                // element graph once; collection below expands every element.
                containsEntity = ValidateEntityBearingLayout(
                    elementField.FieldType,
                    rootType,
                    $"{path}[0]",
                    recursionStack,
                    entityBearingTypes);
            }
            else
            {
                containsEntity = false;
                foreach (var field in GetInstanceFields(type))
                {
                    if (ValidateEntityBearingLayout(
                            field.FieldType,
                            rootType,
                            $"{path}.{field.Name}",
                            recursionStack,
                            entityBearingTypes))
                    {
                        containsEntity = true;
                    }
                }
            }

            if (containsEntity)
            {
                if (IsAutoLayout(type))
                {
                    throw new InvalidOperationException(
                        $"Component type '{FormatType(rootType)}' contains Entity field(s) " +
                        $"through type '{FormatType(type)}' at path '{path}', but that type " +
                        "uses LayoutKind.Auto. LayoutKind.Auto is not supported for " +
                        "Entity-bearing component layouts; apply LayoutKind.Sequential or Explicit.");
                }
                entityBearingTypes.Add(type);
            }

            return containsEntity;
        }
        finally
        {
            // This is a recursion stack, not a global visited set. The same
            // nested type in sibling fields must be traversed independently.
            recursionStack.Remove(type);
        }
    }

    private static void CollectOffsets(
        Type type,
        int baseOffset,
        Type rootType,
        string path,
        HashSet<Type> recursionStack,
        HashSet<Type> entityBearingTypes,
        List<int> offsets)
    {
        if (type == typeof(Entity))
        {
            offsets.Add(baseOffset);
            return;
        }
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return;
        if (!recursionStack.Add(type))
            return;

        try
        {
            if (TryGetInlineArray(type, out var inlineLength, out var elementField))
            {
                var firstElementOffset = GetManagedFieldOffset(type, elementField, rootType, path);
                var elementSize = ComponentSizeCache.GetSize(elementField.FieldType);
                var inlineSize = ComponentSizeCache.GetSize(type);
                var elementsEnd = checked((long)firstElementOffset + (long)inlineLength * elementSize);
                if (elementSize <= 0 || elementsEnd > inlineSize)
                {
                    throw new InvalidOperationException(
                        $"InlineArray type '{FormatType(type)}' has invalid CLR layout: " +
                        $"first element offset {firstElementOffset}, length {inlineLength}, " +
                        $"element size {elementSize}, total size {inlineSize}.");
                }

                // Discover the element graph once, then replicate its relative
                // offsets with the CLR element stride. Large InlineArrays still
                // require one cached offset per Entity, but not one DynamicMethod
                // scan per element.
                var elementOffsets = new List<int>();
                CollectOffsets(
                    elementField.FieldType,
                    0,
                    rootType,
                    $"{path}[0]",
                    recursionStack,
                    entityBearingTypes,
                    elementOffsets);
                for (var i = 0; i < inlineLength; i++)
                {
                    for (var j = 0; j < elementOffsets.Count; j++)
                    {
                        offsets.Add(AddOffset(
                            baseOffset,
                            firstElementOffset,
                            (long)i * elementSize + elementOffsets[j],
                            rootType,
                            $"{path}[{i}]"));
                    }
                }
            }
            else
            {
                foreach (var field in GetInstanceFields(type))
                {
                    if (field.FieldType != typeof(Entity) &&
                        !entityBearingTypes.Contains(field.FieldType))
                    {
                        continue;
                    }

                    var fieldOffset = GetManagedFieldOffset(type, field, rootType, path);
                    var fieldBase = AddOffset(
                        baseOffset,
                        fieldOffset,
                        0,
                        rootType,
                        $"{path}.{field.Name}");
                    CollectOffsets(
                        field.FieldType,
                        fieldBase,
                        rootType,
                        $"{path}.{field.Name}",
                        recursionStack,
                        entityBearingTypes,
                        offsets);
                }
            }
        }
        finally
        {
            recursionStack.Remove(type);
        }
    }

    private static int AddOffset(
        int baseOffset,
        int fieldOffset,
        long extraOffset,
        Type rootType,
        string path)
    {
        long combined;
        try
        {
            combined = checked((long)baseOffset + fieldOffset + extraOffset);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Component type '{FormatType(rootType)}' produced an overflowing " +
                $"Entity offset at path '{path}'.", ex);
        }

        if (combined < int.MinValue || combined > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Component type '{FormatType(rootType)}' produced an invalid " +
                $"Entity offset {combined} at path '{path}'.");
        }
        return (int)combined;
    }

    private static FieldInfo[] GetInstanceFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static bool TryGetInlineArray(
        Type type,
        out int length,
        out FieldInfo elementField)
    {
        var attribute = type.GetCustomAttribute<InlineArrayAttribute>(inherit: false);
        if (attribute is null)
        {
            length = 0;
            elementField = null!;
            return false;
        }

        var fields = GetInstanceFields(type);
        length = attribute.Length;
        if (length <= 0 || fields.Length != 1)
        {
            throw new InvalidOperationException(
                $"InlineArray type '{FormatType(type)}' must have a positive length " +
                $"and exactly one instance field; found length={length}, fields={fields.Length}.");
        }

        elementField = fields[0];
        return true;
    }

    private static bool IsAutoLayout(Type type) =>
        type.StructLayoutAttribute?.Value == LayoutKind.Auto;

    private static string FormatType(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Gets the actual CLR managed value-layout offset of an instance field.
    /// This is intentionally a cold-path DynamicMethod operation: marshaling
    /// offsets are not equivalent to managed value-layout offsets for every
    /// unmanaged field combination (notably bool/byte layouts).
    /// </summary>
    private static int GetManagedFieldOffset(
        Type ownerType,
        FieldInfo field,
        Type rootType,
        string path)
    {
        long offset;
        try
        {
            var method = new DynamicMethod(
                $"GetManagedFieldOffset_{ownerType.Name}_{field.Name}",
                typeof(IntPtr),
                Type.EmptyTypes,
                typeof(EntityFieldResolver).Module,
                skipVisibility: true)
            {
                InitLocals = true
            };
            var il = method.GetILGenerator();
            var owner = il.DeclareLocal(ownerType);

            il.Emit(OpCodes.Ldloca_S, owner);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ldloca_S, owner);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ret);

            var getter = (Func<IntPtr>)method.CreateDelegate(typeof(Func<IntPtr>));
            offset = getter().ToInt64();
        }
        catch (Exception ex) when (IsExpectedDynamicOffsetFailure(ex))
        {
            throw new InvalidOperationException(
                $"Could not obtain the actual CLR offset of field '{path}.{field.Name}' " +
                $"in component type '{FormatType(rootType)}'.", ex);
        }

        if (offset < 0 || offset > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Field '{path}.{field.Name}' in component type '{FormatType(rootType)}' " +
                $"has invalid CLR offset {offset}.");
        }
        return (int)offset;
    }

    private static bool IsExpectedDynamicOffsetFailure(Exception exception) =>
        exception is ArgumentException or
            InvalidOperationException or
            InvalidProgramException or
            MemberAccessException or
            NotSupportedException or
            TypeLoadException or
            System.Security.SecurityException;

    /// <summary>
    /// Resolves placeholder <see cref="Entity"/> values found at known field
    /// offsets within <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Raw component data (mutable span).</param>
    /// <param name="typeId">Component type identifier.</param>
    /// <param name="resolveMap">
    /// Table indexed by placeholder <c>seq (Version)</c>. Referenced entries must
    /// contain a resolved real entity; missing or cancelled entries are rejected.
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
                if ((uint)seq >= (uint)resolveMap.Length)
                    throw new InvalidOperationException(
                        $"Unresolved placeholder entity seq={seq}: the referenced entity was not materialized (seq exceeds resolveMap length).");
                var resolved = resolveMap[seq];
                if (resolved.Id < 0)
                    throw new InvalidOperationException(
                        $"Unresolved placeholder entity seq={seq}: the referenced entity was not materialized (cancelled or not reserved).");
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dataRef, offset), resolved);
            }
        }
    }
}
