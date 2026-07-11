using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests;

public sealed class PublicApiSentinelTests
{
    private static readonly Assembly MiniArchAssembly = typeof(World).Assembly;

    [Fact]
    public void PublicApi_should_match_baseline()
    {
        string actual = GetPublicApiString();
        string expected = GetExpectedBaseline();
        Assert.Equal(expected, actual);
    }

    private static string GetPublicApiString()
    {
        var lines = new List<string>();
        var types = MiniArchAssembly.GetExportedTypes()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            AppendType(lines, type, indent: 0);
        }

        return string.Join("\n", lines);
    }

    private static void AppendType(List<string> lines, Type type, int indent)
    {
        string prefix = new string(' ', indent * 2);
        string typeKind = GetTypeKind(type);
        string baseType = GetBaseTypeDisplay(type);
        string interfaces = GetImplementedInterfacesDisplay(type);
        string genericConstraints = GetGenericConstraintsDisplay(type);
        
        // Type line with kind, base type, and interfaces
        var typeLine = $"{prefix}{typeKind}: {FormatType(type)}";
        if (!string.IsNullOrEmpty(baseType))
        {
            typeLine += $" : {baseType}";
        }
        if (!string.IsNullOrEmpty(interfaces))
        {
            typeLine += $" [{interfaces}]";
        }
        if (!string.IsNullOrEmpty(genericConstraints))
        {
            typeLine += $" {genericConstraints}";
        }
        lines.Add(typeLine);

        // Constructors
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(c => !c.IsSpecialName)
            .OrderBy(c => GetConstructorDisplay(c), StringComparer.Ordinal);
        foreach (var ctor in constructors)
        {
            lines.Add($"{prefix}  Constructor: {GetConstructorDisplay(ctor)}");
        }

        // Methods (excluding property/event accessors)
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !m.IsConstructor)
            .OrderBy(m => GetMethodDisplay(m), StringComparer.Ordinal);
        foreach (var method in methods)
        {
            string methodLine = $"{prefix}  Method: {GetMethodDisplay(method)}";
            string methodConstraints = GetGenericConstraintsDisplay(method);
            if (!string.IsNullOrEmpty(methodConstraints))
            {
                methodLine += $" {methodConstraints}";
            }
            lines.Add(methodLine);
        }

        // Properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(p => GetPropertyDisplay(p), StringComparer.Ordinal);
        foreach (var property in properties)
        {
            lines.Add($"{prefix}  Property: {GetPropertyDisplay(property)}");
        }

        // Fields (including literals)
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(f => GetFieldDisplay(f), StringComparer.Ordinal);
        foreach (var field in fields)
        {
            lines.Add($"{prefix}  Field: {GetFieldDisplay(field)}");
        }

        // Events
        var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        foreach (var eventInfo in events)
        {
            lines.Add($"{prefix}  Event: {GetEventDisplay(eventInfo)}");
        }

        // Operators (including conversion operators)
        var operators = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.IsSpecialName && (m.Name.StartsWith("op_") || m.Name == "op_Implicit" || m.Name == "op_Explicit"))
            .OrderBy(m => GetOperatorDisplay(m), StringComparer.Ordinal);
        foreach (var op in operators)
        {
            lines.Add($"{prefix}  Operator: {GetOperatorDisplay(op)}");
        }

        // Nested types (recursive)
        var nestedTypes = type.GetNestedTypes(BindingFlags.Public)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var nestedType in nestedTypes)
        {
            AppendType(lines, nestedType, indent + 1);
        }
    }

    private static string GetTypeKind(Type type)
    {
        if (type.IsClass)
        {
            if (type.BaseType == typeof(MulticastDelegate))
                return "delegate";
            return "class";
        }
        if (type.IsValueType)
        {
            if (type.IsEnum)
                return "enum";
            return "struct";
        }
        if (type.IsInterface)
            return "interface";
        return "type";
    }

    private static string GetBaseTypeDisplay(Type type)
    {
        if (type.BaseType == null || type.BaseType == typeof(object) || type.BaseType == typeof(ValueType) || type.BaseType == typeof(Enum))
        {
            return string.Empty;
        }
        return FormatType(type.BaseType);
    }

    private static string GetImplementedInterfacesDisplay(Type type)
    {
        var interfaces = type.GetInterfaces()
            .Where(i => !i.IsGenericType && i.Namespace != "System" && i.Namespace != "System.Collections")
            .Select(i => FormatType(i))
            .ToArray();
        return interfaces.Length > 0 ? string.Join(", ", interfaces) : string.Empty;
    }

    private static string GetGenericConstraintsDisplay(Type type)
    {
        if (!type.IsGenericTypeDefinition)
            return string.Empty;

        var args = type.GetGenericArguments();
        var constraints = new List<string>();
        foreach (var arg in args)
        {
            var constraintList = new List<string>();
            var attrs = arg.GenericParameterAttributes;
            
            if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                constraintList.Add("struct");
            else if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    constraintList.Add("class");
                else
                    constraintList.Add("class");
            }
            
            if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && 
                (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraintList.Add("new()");
            }

            var typeConstraints = arg.GetGenericParameterConstraints()
                .Select(c => FormatType(c))
                .Where(c => c != null)
                .ToArray();
            constraintList.AddRange(typeConstraints);

            if (constraintList.Count > 0)
            {
                constraints.Add($"{FormatType(arg)} : {string.Join(", ", constraintList)}");
            }
        }

        return constraints.Count > 0 ? $"where {string.Join(", ", constraints)}" : string.Empty;
    }

    private static string GetGenericConstraintsDisplay(MethodInfo method)
    {
        if (!method.IsGenericMethodDefinition)
            return string.Empty;

        var args = method.GetGenericArguments();
        var constraints = new List<string>();
        foreach (var arg in args)
        {
            var constraintList = new List<string>();
            var attrs = arg.GenericParameterAttributes;
            
            if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                constraintList.Add("struct");
            else if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    constraintList.Add("class");
                else
                    constraintList.Add("class");
            }
            
            if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && 
                (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraintList.Add("new()");
            }

            var typeConstraints = arg.GetGenericParameterConstraints()
                .Select(c => FormatType(c))
                .Where(c => c != null)
                .ToArray();
            constraintList.AddRange(typeConstraints);

            if (constraintList.Count > 0)
            {
                constraints.Add($"{FormatType(arg)} : {string.Join(", ", constraintList)}");
            }
        }

        return constraints.Count > 0 ? $"where {string.Join(", ", constraints)}" : string.Empty;
    }

    private static string FormatType(Type? type)
    {
        if (type is null)
            return "?";
        if (type == null)
            return "null";

        // Handle generic parameters
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        // Handle arrays
        if (type.IsArray)
        {
            var element = type.GetElementType();
            int rank = type.GetArrayRank();
            return rank == 1 
                ? $"{FormatType(element)}[]" 
                : $"{FormatType(element)}[{new string(',', rank - 1)}]";
        }

        // Handle byref types
        if (type.IsByRef)
        {
            return $"{FormatType(type.GetElementType())}&";
        }

        // Handle pointer types
        if (type.IsPointer)
        {
            return $"{FormatType(type.GetElementType())}*";
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            var baseName = type.Name;
            var backtickIndex = baseName.IndexOf('`');
            if (backtickIndex >= 0)
            {
                baseName = baseName.Substring(0, backtickIndex);
            }
            
            // For constructed generic types, show the full namespace
            if (!type.IsGenericTypeDefinition)
            {
                return $"{type.Namespace}.{baseName}<{string.Join(", ", args.Select(FormatType))}>";
            }
            
            // For generic type definitions, just show the name with backtick
            return $"{type.Namespace}.{baseName}`{args.Length}";
        }

        // Handle non-generic types
        return type.FullName ?? type.Name;
    }

    private static string GetConstructorDisplay(ConstructorInfo ctor)
    {
        var parameters = ctor.GetParameters()
            .Select(p => FormatParameter(p))
            .ToArray();
        return $"{ctor.DeclaringType?.Name}({string.Join(", ", parameters)})";
    }

    private static string GetMethodDisplay(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p => FormatParameter(p))
            .ToArray();
        return $"{method.Name}({string.Join(", ", parameters)}) -> {FormatType(method.ReturnType)}";
    }

    private static string GetPropertyDisplay(PropertyInfo property)
    {
        var getter = property.GetGetMethod() != null ? "get; " : "";
        var setter = property.GetSetMethod() != null ? "set; " : "";
        return $"{property.Name} -> {FormatType(property.PropertyType)} [{getter}{setter}]";
    }

    private static string GetFieldDisplay(FieldInfo field)
    {
        var modifiers = new List<string>();
        if (field.IsStatic) modifiers.Add("static");
        if (field.IsLiteral) modifiers.Add("literal");
        var modifierStr = modifiers.Count > 0 ? $" [{string.Join(", ", modifiers)}]" : "";
        
        string valueStr = "";
        if (field.IsLiteral || field.IsStatic)
        {
            var value = field.GetValue(null);
            if (value != null)
            {
                if (field.FieldType.IsEnum)
                {
                    // Include numeric underlying value for enum literals
                    var numericValue = Convert.ToInt64(value);
                    valueStr = $" = {field.Name} ({numericValue})";
                }
                else if (field.FieldType == typeof(string))
                {
                    valueStr = $" = \"{value}\"";
                }
                else if (field.FieldType.IsPrimitive)
                {
                    valueStr = $" = {value}";
                }
                else
                {
                    valueStr = $" = {value}";
                }
            }
        }
        
        return $"{field.Name} -> {FormatType(field.FieldType)}{modifierStr}{valueStr}";
    }

    private static string GetEventDisplay(EventInfo eventInfo)
    {
        return $"{eventInfo.Name} : {FormatType(eventInfo.EventHandlerType)}";
    }

    private static string GetOperatorDisplay(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p => FormatType(p.ParameterType))
            .ToArray();
        return $"{method.Name}({string.Join(", ", parameters)}) -> {FormatType(method.ReturnType)}";
    }

    private static string FormatParameter(ParameterInfo parameter)
    {
        var parts = new List<string>();
        
        // Parameter modifiers
        if (parameter.IsOut)
            parts.Add("out");
        else if (parameter.IsIn)
            parts.Add("in");
        else if (parameter.ParameterType.IsByRef)
            parts.Add("ref");
        
        if (parameter.IsOptional)
        {
            if (parameter.DefaultValue == null)
            {
                if (parameter.ParameterType.IsValueType)
                    parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name} = default");
                else
                    parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name} = null");
            }
            else
            {
                if (parameter.ParameterType == typeof(string))
                    parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name} = \"{parameter.DefaultValue}\"");
                else if (parameter.ParameterType.IsEnum)
                    parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name} = {parameter.ParameterType.Name}.{parameter.DefaultValue}");
                else
                    parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name} = {parameter.DefaultValue}");
            }
        }
        else
        {
            parts.Add($"{FormatType(parameter.ParameterType)} {parameter.Name}");
        }
        
        if (Attribute.IsDefined(parameter, typeof(ParamArrayAttribute)))
        {
            parts.Insert(parts.Count - 1, "params");
        }
        
        return string.Join(" ", parts);
    }

    private static readonly string EmbeddedBaseline = @"struct: MiniArch.AncestorEnumerable
  Method: GetEnumerator() -> MiniArch.AncestorEnumerator
struct: MiniArch.AncestorEnumerator
  Method: MoveNext() -> System.Boolean
  Property: Current -> MiniArch.Entity [get; ]
struct: MiniArch.ArchetypeStats
  Property: Capacity -> System.Int32 [get; ]
  Property: ComponentTypes -> System.Collections.Generic.IReadOnlyList<System.Type> [get; ]
  Property: EntityCount -> System.Int32 [get; ]
class: MiniArch.ChangeWatch`2 where TComponent : struct, System.ValueType, System.IEquatable<TComponent>, THandler : struct, MiniArch.IChangeHandler<TComponent>, System.ValueType
  Method: Diff(MiniArch.World world) -> System.Void
  Method: Snapshot(MiniArch.World world) -> System.Void
  Property: Handler -> THandler& [get; ]
class: MiniArch.ChangeWatch`3 where TComponent : struct, System.ValueType, TValue : struct, System.ValueType, System.IEquatable<TValue>, THandler : struct, MiniArch.IChangeHandler<TComponent, TValue>, System.ValueType
  Method: Diff(MiniArch.World world) -> System.Void
  Method: Snapshot(MiniArch.World world) -> System.Void
  Property: Handler -> THandler& [get; ]
struct: MiniArch.ChildrenEnumerable
  Method: GetEnumerator() -> MiniArch.ChildrenEnumerator
struct: MiniArch.ChildrenEnumerator
  Method: MoveNext() -> System.Boolean
  Property: Current -> MiniArch.Entity [get; ]
delegate: MiniArch.ChunkAction : System.MulticastDelegate [System.Runtime.Serialization.ISerializable]
  Method: BeginInvoke(MiniArch.ChunkView chunk, System.AsyncCallback callback, System.Object object) -> System.IAsyncResult
  Method: EndInvoke(System.IAsyncResult result) -> System.Void
  Method: Invoke(MiniArch.ChunkView chunk) -> System.Void
struct: MiniArch.ChunkView
  Method: GetComponentSpanAt(System.Int32 columnIndex) -> System.Span<T> where T : struct, System.ValueType
  Method: GetEntities() -> System.ReadOnlySpan<MiniArch.Entity>
  Method: GetSpan() -> System.Span<T> where T : struct, System.ValueType
  Method: TryGetComponentIndex(out System.Int32& columnIndex) -> System.Boolean where T : struct, System.ValueType
  Property: Count -> System.Int32 [get; ]
class: MiniArch.ComponentBucketQuery`1 where TComponent : struct, System.ValueType, System.IEquatable<TComponent>
  Method: ContainsKey(TComponent key) -> System.Boolean
  Method: Count(TComponent key) -> System.Int32
  Method: Get(TComponent key, System.Span<MiniArch.Entity> destination) -> System.Int32
  Method: TryGet(TComponent key, System.Span<MiniArch.Entity> destination, out System.Int32& written) -> System.Boolean
class: MiniArch.ComponentSchema
  Method: Fingerprint() -> System.Byte[]
class: MiniArch.Core.CommandStream : MiniArch.Core.CommandStreamCore
  Method: Add(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: AddChild(MiniArch.Entity parent, MiniArch.Entity child) -> System.Void
  Method: Clone(MiniArch.Entity source) -> MiniArch.Entity
  Method: Create() -> MiniArch.Entity
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>, System.ValueType
  Method: Destroy(MiniArch.Entity entity) -> System.Void
  Method: Remove(MiniArch.Entity entity) -> System.Void where T : struct, System.ValueType
  Method: RemoveChild(MiniArch.Entity child) -> System.Void
  Method: Set(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: Track(MiniArch.Entity entity) -> MiniArch.Core.EntitySlot
class: MiniArch.Core.CommandStreamCore
  Method: Clear() -> System.Void
  Method: Replay(MiniArch.Core.FrameDelta delta, System.Boolean resolveSlots = False) -> System.Void
  Method: Snapshot() -> MiniArch.Core.FrameDelta
  Method: SnapshotInto(MiniArch.Core.FrameDelta target) -> System.Void
  Method: Submit() -> System.Boolean
  Method: SubmitAndSnapshotAsync() -> System.Threading.Tasks.Task<MiniArch.Core.FrameDelta>
  Method: SubmitAndSnapshotIntoAsync(MiniArch.Core.FrameDelta target) -> System.Threading.Tasks.Task
  Property: DeferredEntities -> System.Boolean [get; set; ]
struct: MiniArch.Core.EntityAccessor
  Method: Get() -> T& where T : struct, System.ValueType
  Method: Has() -> System.Boolean where T : struct, System.ValueType
  Method: Set(in T& value) -> System.Void where T : struct, System.ValueType
struct: MiniArch.Core.EntitySlot
  Property: HasValue -> System.Boolean [get; ]
  Property: Value -> MiniArch.Entity [get; ]
  Operator: op_Implicit(MiniArch.Core.EntitySlot) -> MiniArch.Entity
class: MiniArch.Core.FrameDelta
  Method: AsSpan() -> System.ReadOnlySpan<System.Byte>
  Method: Deserialize(System.ReadOnlySpan<System.Byte> wire) -> System.Void
  Method: FromWire(System.ReadOnlySpan<System.Byte> wire) -> MiniArch.Core.FrameDelta
  Method: HasEntity(MiniArch.Entity entity) -> System.Boolean
  Method: Validate() -> System.Void
  Property: DeltaCount -> System.Int32 [get; ]
  Property: IsEmpty -> System.Boolean [get; ]
  Field: MaxFrameBytes -> System.Int32 [static] = 16777216
  Field: MaxOpsPerFrame -> System.Int32 [static] = 1000000
interface: MiniArch.Core.ICreateManyWriter`1 where T1 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`2 where T1 : struct, System.ValueType, T2 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`3 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`4 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3, out T4& c4) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`5 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3, out T4& c4, out T5& c5) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`6 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3, out T4& c4, out T5& c5, out T6& c6) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`7 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3, out T4& c4, out T5& c5, out T6& c6, out T7& c7) -> System.Void
interface: MiniArch.Core.ICreateManyWriter`8 where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType
  Method: Write(System.Int32 index, MiniArch.Entity entity, out T1& c1, out T2& c2, out T3& c3, out T4& c4, out T5& c5, out T6& c6, out T7& c7, out T8& c8) -> System.Void
class: MiniArch.Core.ParallelCommandStream : MiniArch.Core.CommandStreamCore
  Method: Add(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: AddChild(MiniArch.Entity parent, MiniArch.Entity child) -> System.Void
  Method: Clone(MiniArch.Entity source) -> MiniArch.Entity
  Method: Create() -> MiniArch.Entity
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>, System.ValueType
  Method: CreateMany(System.Span<MiniArch.Entity> entities, TWriter writer) -> System.Void where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, TWriter : struct, MiniArch.Core.ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>, System.ValueType
  Method: Destroy(MiniArch.Entity entity) -> System.Void
  Method: Remove(MiniArch.Entity entity) -> System.Void where T : struct, System.ValueType
  Method: RemoveChild(MiniArch.Entity child) -> System.Void
  Method: Set(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: Track(MiniArch.Entity entity) -> MiniArch.Core.EntitySlot
class: MiniArch.Core.WorldSnapshot
  Method: ComputeCanonicalChecksum(MiniArch.World world) -> System.Byte[]
  Method: ComputeChecksum(MiniArch.World world) -> System.Byte[]
  Method: Load(System.IO.Stream stream) -> MiniArch.World
  Method: Save(System.IO.Stream stream, MiniArch.World world) -> System.Void
class: MiniArch.Core.WorldStateSnapshot
  Property: IsRecycled -> System.Boolean [get; ]
struct: MiniArch.Diagnostics.ArchetypeInfo
  Property: ComponentTypes -> System.Collections.ObjectModel.ReadOnlyCollection<System.Type> [get; ]
  Property: EntityCount -> System.Int32 [get; ]
class: MiniArch.Diagnostics.ComponentDiff
  Method: ToString() -> System.String
  Property: ComponentType -> System.Type [get; ]
  Property: Kind -> MiniArch.Diagnostics.ComponentDiffKind [get; ]
enum: MiniArch.Diagnostics.ComponentDiffKind
  Field: OnlyInA -> MiniArch.Diagnostics.ComponentDiffKind [static, literal] = OnlyInA (0)
  Field: OnlyInB -> MiniArch.Diagnostics.ComponentDiffKind [static, literal] = OnlyInB (1)
  Field: ValueDifferent -> MiniArch.Diagnostics.ComponentDiffKind [static, literal] = ValueDifferent (2)
  Field: value__ -> System.Int32
struct: MiniArch.Diagnostics.ComponentInfo
  Property: RawBytes -> System.Byte[] [get; ]
  Property: SizeBytes -> System.Int32 [get; ]
  Property: Type -> System.Type [get; ]
class: MiniArch.Diagnostics.EntityDiff
  Method: ToString() -> System.String
  Property: ComponentDiffs -> System.Collections.Generic.IReadOnlyList<MiniArch.Diagnostics.ComponentDiff> [get; ]
  Property: EntityA -> MiniArch.Entity [get; ]
  Property: EntityB -> MiniArch.Entity [get; ]
  Property: EntityId -> System.Int32 [get; ]
  Property: HierarchyDiff -> MiniArch.Diagnostics.HierarchyDiff [get; ]
  Property: Kind -> MiniArch.Diagnostics.EntityDiffKind [get; ]
  Property: VersionMismatch -> System.Boolean [get; ]
enum: MiniArch.Diagnostics.EntityDiffKind
  Field: Different -> MiniArch.Diagnostics.EntityDiffKind [static, literal] = Different (2)
  Field: OnlyInA -> MiniArch.Diagnostics.EntityDiffKind [static, literal] = OnlyInA (0)
  Field: OnlyInB -> MiniArch.Diagnostics.EntityDiffKind [static, literal] = OnlyInB (1)
  Field: value__ -> System.Int32
class: MiniArch.Diagnostics.EntityDump
  Method: Describe(MiniArch.World world, MiniArch.Entity entity) -> MiniArch.Diagnostics.EntityReport
struct: MiniArch.Diagnostics.EntityReport
  Method: ToString() -> System.String
  Property: Archetype -> System.Nullable<MiniArch.Diagnostics.ArchetypeInfo> [get; ]
  Property: Children -> System.Collections.ObjectModel.ReadOnlyCollection<MiniArch.Entity> [get; ]
  Property: Components -> System.Collections.ObjectModel.ReadOnlyCollection<MiniArch.Diagnostics.ComponentInfo> [get; ]
  Property: Id -> System.Int32 [get; ]
  Property: IsAlive -> System.Boolean [get; ]
  Property: Parent -> System.Nullable<MiniArch.Entity> [get; ]
  Property: Version -> System.Int32 [get; ]
class: MiniArch.Diagnostics.FreeListDiff
  Method: ToString() -> System.String
  Property: EntitySlotCountA -> System.Int32 [get; ]
  Property: EntitySlotCountB -> System.Int32 [get; ]
  Property: FreeCountA -> System.Int32 [get; ]
  Property: FreeCountB -> System.Int32 [get; ]
  Property: SlotDiffs -> System.Collections.ObjectModel.ReadOnlyCollection<MiniArch.Diagnostics.FreeSlotDiff> [get; ]
class: MiniArch.Diagnostics.FreeSlotDiff
  Method: ToString() -> System.String
  Property: Kind -> MiniArch.Diagnostics.FreeSlotDiffKind [get; ]
  Property: SlotIdA -> System.Int32 [get; ]
  Property: SlotIdB -> System.Int32 [get; ]
  Property: VersionA -> System.Int32 [get; ]
  Property: VersionB -> System.Int32 [get; ]
enum: MiniArch.Diagnostics.FreeSlotDiffKind
  Field: ExtraInA -> MiniArch.Diagnostics.FreeSlotDiffKind [static, literal] = ExtraInA (2)
  Field: ExtraInB -> MiniArch.Diagnostics.FreeSlotDiffKind [static, literal] = ExtraInB (3)
  Field: OrderMismatch -> MiniArch.Diagnostics.FreeSlotDiffKind [static, literal] = OrderMismatch (1)
  Field: VersionMismatch -> MiniArch.Diagnostics.FreeSlotDiffKind [static, literal] = VersionMismatch (0)
  Field: value__ -> System.Int32
class: MiniArch.Diagnostics.HierarchyDiff
  Method: ToString() -> System.String
  Property: ParentIdA -> System.Int32 [get; ]
  Property: ParentIdB -> System.Int32 [get; ]
enum: MiniArch.Diagnostics.ValidationCategory
  Field: Archetype -> MiniArch.Diagnostics.ValidationCategory [static, literal] = Archetype (3)
  Field: EntitySlot -> MiniArch.Diagnostics.ValidationCategory [static, literal] = EntitySlot (0)
  Field: FreeList -> MiniArch.Diagnostics.ValidationCategory [static, literal] = FreeList (1)
  Field: Hierarchy -> MiniArch.Diagnostics.ValidationCategory [static, literal] = Hierarchy (2)
  Field: value__ -> System.Int32
enum: MiniArch.Diagnostics.ValidationCode
  Field: ArchetypeEntityCount -> MiniArch.Diagnostics.ValidationCode [static, literal] = ArchetypeEntityCount (6)
  Field: AsymmetricParent -> MiniArch.Diagnostics.ValidationCode [static, literal] = AsymmetricParent (4)
  Field: DuplicateEntityId -> MiniArch.Diagnostics.ValidationCode [static, literal] = DuplicateEntityId (7)
  Field: FreeListDuplicate -> MiniArch.Diagnostics.ValidationCode [static, literal] = FreeListDuplicate (3)
  Field: FreeListOccupied -> MiniArch.Diagnostics.ValidationCode [static, literal] = FreeListOccupied (2)
  Field: HierarchyCycle -> MiniArch.Diagnostics.ValidationCode [static, literal] = HierarchyCycle (9)
  Field: OrphanedChild -> MiniArch.Diagnostics.ValidationCode [static, literal] = OrphanedChild (5)
  Field: OrphanedSlot -> MiniArch.Diagnostics.ValidationCode [static, literal] = OrphanedSlot (0)
  Field: SlotCapacityWarning -> MiniArch.Diagnostics.ValidationCode [static, literal] = SlotCapacityWarning (8)
  Field: SlotCollision -> MiniArch.Diagnostics.ValidationCode [static, literal] = SlotCollision (1)
  Field: value__ -> System.Int32
struct: MiniArch.Diagnostics.ValidationIssue
  Method: ToString() -> System.String
  Property: Category -> MiniArch.Diagnostics.ValidationCategory [get; ]
  Property: Code -> MiniArch.Diagnostics.ValidationCode [get; ]
  Property: Description -> System.String [get; ]
  Property: Severity -> MiniArch.Diagnostics.ValidationSeverity [get; ]
struct: MiniArch.Diagnostics.ValidationResult
  Property: IsValid -> System.Boolean [get; ]
  Property: Issues -> System.Collections.ObjectModel.ReadOnlyCollection<MiniArch.Diagnostics.ValidationIssue> [get; ]
enum: MiniArch.Diagnostics.ValidationSeverity
  Field: Error -> MiniArch.Diagnostics.ValidationSeverity [static, literal] = Error (0)
  Field: Warning -> MiniArch.Diagnostics.ValidationSeverity [static, literal] = Warning (1)
  Field: value__ -> System.Int32
class: MiniArch.Diagnostics.WorldDiff
  Method: Compare(MiniArch.World worldA, MiniArch.World worldB) -> MiniArch.Diagnostics.WorldDiffResult
class: MiniArch.Diagnostics.WorldDiffResult
  Property: AreIdentical -> System.Boolean [get; ]
  Property: EntityDiffs -> System.Collections.Generic.IReadOnlyList<MiniArch.Diagnostics.EntityDiff> [get; ]
  Property: FreeListDiff -> MiniArch.Diagnostics.FreeListDiff [get; ]
class: MiniArch.Diagnostics.WorldDigest
  Method: Compute(MiniArch.World world) -> MiniArch.Diagnostics.WorldDigestResult
struct: MiniArch.Diagnostics.WorldDigestResult
  Property: FreeList -> System.Byte[] [get; ]
  Property: Hierarchy -> System.Byte[] [get; ]
  Property: Occupancy -> System.Byte[] [get; ]
  Property: PerArchetype -> System.Collections.Generic.IReadOnlyDictionary<System.Int32, System.Byte[]> [get; ]
  Property: PerComponent -> System.Collections.Generic.IReadOnlyDictionary<System.Type, System.Byte[]> [get; ]
  Property: Total -> System.Byte[] [get; ]
class: MiniArch.Diagnostics.WorldValidator
  Method: Validate(MiniArch.World world) -> MiniArch.Diagnostics.ValidationResult
struct: MiniArch.Entity
  Method: CompareTo(MiniArch.Entity other) -> System.Int32
  Method: Deconstruct(out System.Int32& Id, out System.Int32& Version) -> System.Void
  Method: Equals(MiniArch.Entity other) -> System.Boolean
  Method: Equals(System.Object obj) -> System.Boolean
  Method: GetHashCode() -> System.Int32
  Method: ToString() -> System.String
  Property: Id -> System.Int32 [get; set; ]
  Property: IsPlaceholder -> System.Boolean [get; ]
  Property: IsUnmappedSentinel -> System.Boolean [get; ]
  Property: IsValid -> System.Boolean [get; ]
  Property: Version -> System.Int32 [get; set; ]
  Operator: op_Equality(MiniArch.Entity, MiniArch.Entity) -> System.Boolean
  Operator: op_Inequality(MiniArch.Entity, MiniArch.Entity) -> System.Boolean
class: MiniArch.FrameLookup`1 where TKey : struct, System.ValueType, System.IEquatable<TKey>
  Method: Build(MiniArch.World world, MiniArch.QueryDescription query, TSel selector = default) -> System.Void where TSel : struct, MiniArch.IFrameKeySelector<TKey>, System.ValueType
  Method: Clear() -> System.Void
  Method: EnsureCapacity(System.Int32 minKeyCapacity, System.Int32 minRowCapacity) -> System.Void
  Method: TryBuild(MiniArch.World world, MiniArch.QueryDescription query, TSel selector = default) -> System.Boolean where TSel : struct, MiniArch.IFrameKeySelector<TKey>, System.ValueType
  Property: Item -> System.ReadOnlySpan<MiniArch.RowRef> [get; ]
  Property: KeyCount -> System.Int32 [get; ]
  Property: RowCount -> System.Int32 [get; ]
interface: MiniArch.IChangeHandler`1 where TComponent : struct, System.ValueType, System.IEquatable<TComponent>
  Method: OnChange(MiniArch.World world, MiniArch.Entity entity, in TComponent& oldValue, in TComponent& newValue) -> System.Void
interface: MiniArch.IChangeHandler`2 where TComponent : struct, System.ValueType, TValue : struct, System.ValueType, System.IEquatable<TValue>
  Method: OnChange(MiniArch.World world, MiniArch.Entity entity, TValue oldValue, TValue newValue) -> System.Void
  Method: Project(in TComponent& component) -> TValue
interface: MiniArch.IChunkForEach
  Method: OnChunk(MiniArch.ChunkView chunk) -> System.Void
interface: MiniArch.IFrameKeySelector`1 where TKey : struct, System.ValueType, System.IEquatable<TKey>
  Method: Select(MiniArch.Entity entity, System.ReadOnlySpan<MiniArch.ChunkView> chunks, System.Int32 chunkIndex, System.Int32 rowIndex) -> TKey
interface: MiniArch.ITransitionHandler
  Method: OnChange(MiniArch.World world, MiniArch.Entity entity, MiniArch.TransitionKind kind) -> System.Void
struct: MiniArch.OrderedComponentEnumerator`1 where T : struct, System.ValueType
  Method: Dispose() -> System.Void
  Method: MoveNext() -> System.Boolean
  Property: Current -> MiniArch.Entity [get; ]
struct: MiniArch.OrderedComponentQuery`1 where T : struct, System.ValueType
  Method: GetEnumerator() -> MiniArch.OrderedComponentEnumerator<T>
struct: MiniArch.OrderedEntityEnumerator
  Method: Dispose() -> System.Void
  Method: MoveNext() -> System.Boolean
  Property: Current -> MiniArch.Entity [get; ]
struct: MiniArch.OrderedEntityQuery
  Method: GetEnumerator() -> MiniArch.OrderedEntityEnumerator
struct: MiniArch.Query
  Method: ForEachChunk(MiniArch.ChunkAction action) -> System.Void
  Method: ForEachChunk(ref TForEach& forEach) -> System.Void where TForEach : struct, MiniArch.IChunkForEach, System.ValueType
  Method: ForEachChunkParallel(MiniArch.ChunkAction action) -> System.Void
  Method: ForEachChunkParallel(TForEach forEach) -> System.Void where TForEach : struct, MiniArch.IChunkForEach, System.ValueType
  Method: GetChunks() -> System.ReadOnlySpan<MiniArch.ChunkView>
  Method: GetEnumerator() -> MiniArch.QueryEnumerator
  Method: OrderByComponent(System.Collections.Generic.IComparer<T> comparer) -> MiniArch.OrderedComponentQuery<T> where T : struct, System.ValueType
  Method: OrderByComponent(System.Comparison<T> comparison) -> MiniArch.OrderedComponentQuery<T> where T : struct, System.ValueType
  Method: OrderByComponentDescending(System.Collections.Generic.IComparer<T> comparer) -> MiniArch.OrderedComponentQuery<T> where T : struct, System.ValueType
  Method: OrderByComponentDescending(System.Comparison<T> comparison) -> MiniArch.OrderedComponentQuery<T> where T : struct, System.ValueType
  Method: OrderByEntityId() -> MiniArch.OrderedEntityQuery
  Method: OrderByEntityIdDescending() -> MiniArch.OrderedEntityQuery
  Property: RefreshCount -> System.Int32 [get; ]
struct: MiniArch.QueryDescription
  Method: Equals(MiniArch.QueryDescription other) -> System.Boolean
  Method: Equals(System.Object obj) -> System.Boolean
  Method: Exact() -> MiniArch.QueryDescription
  Method: GetHashCode() -> System.Int32
  Method: With() -> MiniArch.QueryDescription where T : struct, System.ValueType
  Method: WithAny() -> MiniArch.QueryDescription where T : struct, System.ValueType
  Method: Without() -> MiniArch.QueryDescription where T : struct, System.ValueType
  Property: AnyTypes -> System.Collections.Generic.IReadOnlyList<System.Type> [get; ]
  Property: ExcludedTypes -> System.Collections.Generic.IReadOnlyList<System.Type> [get; ]
  Property: IsExact -> System.Boolean [get; ]
  Property: RequiredTypes -> System.Collections.Generic.IReadOnlyList<System.Type> [get; ]
  Operator: op_Equality(MiniArch.QueryDescription, MiniArch.QueryDescription) -> System.Boolean
  Operator: op_Inequality(MiniArch.QueryDescription, MiniArch.QueryDescription) -> System.Boolean
struct: MiniArch.QueryEnumerator
  Method: MoveNext() -> System.Boolean
  Property: Current -> MiniArch.Entity [get; ]
struct: MiniArch.RowRef
  Method: Component(System.ReadOnlySpan<MiniArch.ChunkView> chunks) -> T& where T : struct, System.ValueType
  Method: Entity(System.ReadOnlySpan<MiniArch.ChunkView> chunks) -> MiniArch.Entity
enum: MiniArch.TransitionKind
  Field: Entered -> MiniArch.TransitionKind [static, literal] = Entered (0)
  Field: Exited -> MiniArch.TransitionKind [static, literal] = Exited (1)
  Field: value__ -> System.Int32
class: MiniArch.TransitionWatch`1 where THandler : struct, MiniArch.ITransitionHandler, System.ValueType
  Method: Diff(MiniArch.World world) -> System.Void
  Method: Snapshot(MiniArch.World world) -> System.Void
  Property: Handler -> THandler& [get; ]
class: MiniArch.World
  Method: Access(MiniArch.Entity entity) -> MiniArch.Core.EntityAccessor
  Method: Add(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: AddChild(MiniArch.Entity parent, MiniArch.Entity child) -> System.Void
  Method: CanonicalChecksum() -> System.Byte[]
  Method: CaptureState() -> MiniArch.Core.WorldStateSnapshot
  Method: Checksum() -> System.Byte[]
  Method: Clear(in MiniArch.QueryDescription& description) -> System.Void
  Method: Clone() -> MiniArch.World
  Method: Clone(MiniArch.Entity source) -> MiniArch.Entity
  Method: Create(T1 component1) -> MiniArch.Entity where T1 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType, T12 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType, T12 : struct, System.ValueType, T13 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType, T12 : struct, System.ValueType, T13 : struct, System.ValueType, T14 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType, T12 : struct, System.ValueType, T13 : struct, System.ValueType, T14 : struct, System.ValueType, T15 : struct, System.ValueType
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16) -> MiniArch.Entity where T1 : struct, System.ValueType, T2 : struct, System.ValueType, T3 : struct, System.ValueType, T4 : struct, System.ValueType, T5 : struct, System.ValueType, T6 : struct, System.ValueType, T7 : struct, System.ValueType, T8 : struct, System.ValueType, T9 : struct, System.ValueType, T10 : struct, System.ValueType, T11 : struct, System.ValueType, T12 : struct, System.ValueType, T13 : struct, System.ValueType, T14 : struct, System.ValueType, T15 : struct, System.ValueType, T16 : struct, System.ValueType
  Method: CreateEmpty() -> MiniArch.Entity
  Method: Destroy(MiniArch.Entity entity) -> System.Void
  Method: Destroy(System.ReadOnlySpan<MiniArch.Entity> entities) -> System.Void
  Method: Destroy(in MiniArch.QueryDescription& description) -> System.Void
  Method: Dispose() -> System.Void
  Method: EnsureCapacity(System.Int32 entityCapacity) -> System.Void
  Method: EnumerateAncestors(MiniArch.Entity child) -> MiniArch.AncestorEnumerable
  Method: EnumerateChildren(MiniArch.Entity parent) -> MiniArch.ChildrenEnumerable
  Method: Get(MiniArch.Entity entity) -> T where T : struct, System.ValueType
  Method: GetArchetypeStats() -> MiniArch.ArchetypeStats[]
  Method: GetDepth(MiniArch.Entity entity) -> System.Int32
  Method: GetRef(MiniArch.Entity entity) -> T& where T : struct, System.ValueType
  Method: GetSingleton() -> MiniArch.Entity where T : struct, System.ValueType
  Method: GetStats() -> MiniArch.WorldStats
  Method: Has(MiniArch.Entity entity) -> System.Boolean where T : struct, System.ValueType
  Method: HasChildren(MiniArch.Entity entity) -> System.Boolean
  Method: IsAlive(MiniArch.Entity entity) -> System.Boolean
  Method: IsRoot(MiniArch.Entity entity) -> System.Boolean
  Method: Query(in MiniArch.QueryDescription& description) -> MiniArch.Query
  Method: Remove(MiniArch.Entity entity) -> System.Void where T : struct, System.ValueType
  Method: RemoveChild(MiniArch.Entity child) -> System.Void
  Method: RestoreState(MiniArch.Core.WorldStateSnapshot snapshot) -> System.Void
  Method: Set(MiniArch.Entity entity, T component) -> System.Void where T : struct, System.ValueType
  Method: TryGet(MiniArch.Entity entity, out T& component) -> System.Boolean where T : struct, System.ValueType
  Method: TryGetParent(MiniArch.Entity child, out MiniArch.Entity& parent) -> System.Boolean
  Method: Watch(MiniArch.QueryDescription filter) -> MiniArch.TransitionWatch<THandler> where THandler : struct, MiniArch.ITransitionHandler, System.ValueType
  Method: Watch(System.Nullable<MiniArch.QueryDescription> query = default) -> MiniArch.ChangeWatch<TComponent, THandler> where TComponent : struct, System.ValueType, System.IEquatable<TComponent>, THandler : struct, MiniArch.IChangeHandler<TComponent>, System.ValueType
  Method: Watch(System.Nullable<MiniArch.QueryDescription> query = default) -> MiniArch.ChangeWatch<TComponent, TValue, THandler> where TComponent : struct, System.ValueType, TValue : struct, System.ValueType, System.IEquatable<TValue>, THandler : struct, MiniArch.IChangeHandler<TComponent, TValue>, System.ValueType
  Property: EntityCapacity -> System.Int32 [get; ]
  Property: EntityCount -> System.Int32 [get; ]
struct: MiniArch.WorldStats
  Property: ArchetypeCount -> System.Int32 [get; ]
  Property: EntityCapacity -> System.Int32 [get; ]
  Property: EntityCount -> System.Int32 [get; ]
  Property: RecycledEntityCount -> System.Int32 [get; ]";

    private static string GetExpectedBaseline()
    {
        string actual = GetPublicApiString();

        var generateFlag = Environment.GetEnvironmentVariable("GENERATE_API_BASELINE");
        if (!string.IsNullOrEmpty(generateFlag))
        {
            string baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt");
            File.WriteAllText(baselinePath, actual);
            return actual;
        }

        // Use file if available (committed to repo, copied to output by csproj).
        string filePath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt");
        if (File.Exists(filePath))
            return File.ReadAllText(filePath).ReplaceLineEndings("\n").TrimEnd('\n');

        return EmbeddedBaseline.ReplaceLineEndings("\n").TrimEnd('\n');
    }
}
