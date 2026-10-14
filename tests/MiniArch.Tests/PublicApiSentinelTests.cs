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

        throw new InvalidOperationException($"PublicApiBaseline.txt was not found in {AppContext.BaseDirectory}; MiniArch.Tests.csproj copies it to the test output, so a missing baseline fails instead of comparing a stale embedded copy.");
    }
}
