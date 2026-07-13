using System.Reflection;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Shared codec and validation rules for component type schema names.
/// </summary>
internal static class ComponentSchemaCodec
{
    internal const int MaxSchemaNameUtf8Bytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly MethodInfo RequireUnmanagedMethod = typeof(ComponentSchemaCodec)
        .GetMethod(nameof(RequireUnmanaged), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static string GetSchemaName(Type componentType)
    {
        return componentType.AssemblyQualifiedName ?? componentType.FullName ?? componentType.Name;
    }

    internal static void WriteSchemaName(BinaryWriter writer, Type componentType)
    {
        WriteSchemaName(writer, GetSchemaName(componentType));
    }

    internal static void WriteSchemaName(BinaryWriter writer, string schemaName)
    {
        var bytes = StrictUtf8.GetBytes(schemaName);
        if (bytes.Length > MaxSchemaNameUtf8Bytes)
        {
            throw new InvalidOperationException(
                $"Component schema name is {bytes.Length} UTF-8 bytes, which exceeds " +
                $"the maximum supported length of {MaxSchemaNameUtf8Bytes} bytes.");
        }

        Write7BitEncodedInt(writer, bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadSchemaName(BinaryReader reader, string context)
    {
        var byteCount = Read7BitEncodedInt(reader, context);
        if (byteCount > MaxSchemaNameUtf8Bytes)
        {
            throw new InvalidDataException(
                $"{context} schema name length ({byteCount} UTF-8 bytes) exceeds " +
                $"the maximum supported length of {MaxSchemaNameUtf8Bytes} bytes.");
        }

        var bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
        {
            throw new InvalidDataException(
                $"{context} schema name is truncated: expected {byteCount} byte(s), got {bytes.Length}.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{context} schema name is not valid UTF-8.", ex);
        }
    }

    internal static Type ResolveSchemaType(string schemaName, string context)
    {
        var type = Type.GetType(schemaName, throwOnError: false);
        if (type is null)
        {
            throw new InvalidDataException(
                $"{context}: Could not resolve type '{schemaName}'. " +
                "Ensure the defining assembly is loaded and the name is correct.");
        }

        return type;
    }

    internal static void EnsureImportableComponentType(Type componentType, string schemaName, string context)
    {
        if (componentType.ContainsGenericParameters)
        {
            throw new InvalidDataException(
                $"{context}: Type '{schemaName}' contains open generic parameters " +
                "and cannot be used as a component type.");
        }

        if (!componentType.IsValueType)
        {
            throw new InvalidDataException(
                $"{context}: Type '{schemaName}' is not a value type " +
                "and cannot be used as a component type.");
        }

        if (componentType.IsByRefLike)
        {
            throw new InvalidDataException(
                $"{context}: Type '{schemaName}' is by-ref-like " +
                "and cannot be used as a component type.");
        }

        if (TryFindManagedMemberType(componentType, out var managedType))
        {
            throw new InvalidDataException(
                $"{context}: Type '{schemaName}' contains managed member type " +
                $"{GetDisplayName(managedType)} and cannot be used as a component type.");
        }

        if (!SatisfiesUnmanagedConstraint(componentType))
        {
            throw new InvalidDataException(
                $"{context}: Type '{schemaName}' does not satisfy the unmanaged component constraint.");
        }
    }

    internal static bool SatisfiesUnmanagedConstraint(Type componentType)
    {
        try
        {
            _ = RequireUnmanagedMethod.MakeGenericMethod(componentType);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryFindManagedMemberType(Type type, out Type managedType)
    {
        if (!type.IsValueType)
        {
            managedType = type;
            return true;
        }

        if (type.IsPrimitive || type.IsEnum || type.IsPointer)
        {
            managedType = null!;
            return false;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (TryFindManagedMemberType(field.FieldType, out managedType))
                return true;
        }

        managedType = null!;
        return false;
    }

    internal static string GetDisplayName(Type type) => type.FullName ?? type.Name;

    private static int Read7BitEncodedInt(BinaryReader reader, string context)
    {
        uint result = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            byte b;
            try
            {
                b = reader.ReadByte();
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException($"{context} schema name length is truncated.", ex);
            }

            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (result > int.MaxValue)
                    throw new InvalidDataException($"{context} schema name length is malformed.");
                return (int)result;
            }
        }

        throw new InvalidDataException($"{context} schema name length is malformed.");
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            writer.Write((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        writer.Write((byte)remaining);
    }

    private static void RequireUnmanaged<T>() where T : unmanaged { }
}
