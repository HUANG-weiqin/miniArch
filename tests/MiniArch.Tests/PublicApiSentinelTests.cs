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
        lines.Add($"{prefix}Type: {type.FullName}");

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
            lines.Add($"{prefix}  Method: {GetMethodDisplay(method)}");
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
            lines.Add($"{prefix}  Event: {eventInfo.Name}");
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

    private static string GetConstructorDisplay(ConstructorInfo ctor)
    {
        var parameters = ctor.GetParameters()
            .Select(p => $"{p.ParameterType.Name} {p.Name}")
            .ToArray();
        return $"{ctor.DeclaringType?.Name}({string.Join(", ", parameters)})";
    }

    private static string GetMethodDisplay(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p => $"{p.ParameterType.Name} {p.Name}")
            .ToArray();
        return $"{method.Name}({string.Join(", ", parameters)}) -> {method.ReturnType.Name}";
    }

    private static string GetPropertyDisplay(PropertyInfo property)
    {
        var getter = property.GetGetMethod() != null ? "get; " : "";
        var setter = property.GetSetMethod() != null ? "set; " : "";
        return $"{property.Name} -> {property.PropertyType.Name} [{getter}{setter}]";
    }

    private static string GetFieldDisplay(FieldInfo field)
    {
        var modifiers = new List<string>();
        if (field.IsStatic) modifiers.Add("static");
        if (field.IsLiteral) modifiers.Add("literal");
        var modifierStr = modifiers.Count > 0 ? $" [{string.Join(", ", modifiers)}]" : "";
        return $"{field.Name} -> {field.FieldType.Name}{modifierStr}";
    }

    private static string GetOperatorDisplay(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToArray();
        return $"{method.Name}({string.Join(", ", parameters)}) -> {method.ReturnType.Name}";
    }

    private static readonly string EmbeddedBaseline = @"Type: MiniArch.ArchetypeStats
  Property: Capacity -> Int32 [get; ]
  Property: ComponentTypes -> IReadOnlyList`1 [get; ]
  Property: EntityCount -> Int32 [get; ]
Type: MiniArch.ChangeWatch`2
  Method: Diff(World world) -> Void
  Method: Snapshot(World world) -> Void
  Property: Handler -> THandler& [get; ]
Type: MiniArch.ChangeWatch`3
  Method: Diff(World world) -> Void
  Method: Snapshot(World world) -> Void
  Property: Handler -> THandler& [get; ]
Type: MiniArch.ChildrenEnumerable
  Method: GetEnumerator() -> ChildrenEnumerator
Type: MiniArch.ChildrenEnumerator
  Method: MoveNext() -> Boolean
  Property: Current -> Entity [get; ]
Type: MiniArch.ChunkAction
  Method: BeginInvoke(ChunkView chunk, AsyncCallback callback, Object object) -> IAsyncResult
  Method: EndInvoke(IAsyncResult result) -> Void
  Method: Invoke(ChunkView chunk) -> Void
Type: MiniArch.ChunkView
  Method: GetComponentSpanAt(Int32 columnIndex) -> Span`1
  Method: GetEntities() -> ReadOnlySpan`1
  Method: GetSpan() -> Span`1
  Method: TryGetComponentIndex(Int32& columnIndex) -> Boolean
  Property: Count -> Int32 [get; ]
Type: MiniArch.ComponentSchema
  Method: Fingerprint() -> Byte[]
Type: MiniArch.Core.CommandStream
  Method: Add(Entity entity, T component) -> Void
  Method: AddChild(Entity parent, Entity child) -> Void
  Method: Clone(Entity source) -> Entity
  Method: Create() -> Entity
  Method: Destroy(Entity entity) -> Void
  Method: Remove(Entity entity) -> Void
  Method: RemoveChild(Entity child) -> Void
  Method: Set(Entity entity, T component) -> Void
  Method: Track(Entity entity) -> EntitySlot
Type: MiniArch.Core.CommandStreamCore
  Method: Clear() -> Void
  Method: Replay(FrameDelta delta, Boolean resolveSlots) -> Void
  Method: Snapshot() -> FrameDelta
  Method: SnapshotInto(FrameDelta target) -> Void
  Method: Submit() -> Boolean
  Method: SubmitAndSnapshotAsync() -> Task`1
  Method: SubmitAndSnapshotIntoAsync(FrameDelta target) -> Task
  Property: DeferredEntities -> Boolean [get; set; ]
Type: MiniArch.Core.EntityAccessor
  Method: Get() -> T&
  Method: Has() -> Boolean
  Method: Set(T& value) -> Void
Type: MiniArch.Core.EntitySlot
  Property: HasValue -> Boolean [get; ]
  Property: Value -> Entity [get; ]
  Operator: op_Implicit(EntitySlot) -> Entity
Type: MiniArch.Core.FrameDelta
  Method: AsSpan() -> ReadOnlySpan`1
  Method: Deserialize(ReadOnlySpan`1 wire) -> Void
  Method: FromWire(ReadOnlySpan`1 wire) -> FrameDelta
  Method: HasEntity(Entity entity) -> Boolean
  Method: Validate() -> Void
  Property: DeltaCount -> Int32 [get; ]
  Property: IsEmpty -> Boolean [get; ]
  Field: MaxFrameBytes -> Int32 [static]
  Field: MaxOpsPerFrame -> Int32 [static]
Type: MiniArch.Core.ParallelCommandStream
  Method: Add(Entity entity, T component) -> Void
  Method: AddChild(Entity parent, Entity child) -> Void
  Method: Clone(Entity source) -> Entity
  Method: Create() -> Entity
  Method: Destroy(Entity entity) -> Void
  Method: Remove(Entity entity) -> Void
  Method: RemoveChild(Entity child) -> Void
  Method: Set(Entity entity, T component) -> Void
  Method: Track(Entity entity) -> EntitySlot
Type: MiniArch.Core.WorldSnapshot
  Method: ComputeCanonicalChecksum(World world) -> Byte[]
  Method: ComputeChecksum(World world) -> Byte[]
  Method: Load(Stream stream) -> World
  Method: Save(Stream stream, World world) -> Void
Type: MiniArch.Core.WorldStateSnapshot
  Property: IsRecycled -> Boolean [get; ]
Type: MiniArch.Diagnostics.ArchetypeInfo
  Property: ComponentTypes -> ReadOnlyCollection`1 [get; ]
  Property: EntityCount -> Int32 [get; ]
Type: MiniArch.Diagnostics.ComponentDiff
  Method: ToString() -> String
  Property: ComponentType -> Type [get; ]
  Property: Kind -> ComponentDiffKind [get; ]
Type: MiniArch.Diagnostics.ComponentDiffKind
  Field: OnlyInA -> ComponentDiffKind [static, literal]
  Field: OnlyInB -> ComponentDiffKind [static, literal]
  Field: ValueDifferent -> ComponentDiffKind [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.ComponentInfo
  Property: RawBytes -> Byte[] [get; ]
  Property: SizeBytes -> Int32 [get; ]
  Property: Type -> Type [get; ]
Type: MiniArch.Diagnostics.EntityDiff
  Method: ToString() -> String
  Property: ComponentDiffs -> IReadOnlyList`1 [get; ]
  Property: EntityA -> Entity [get; ]
  Property: EntityB -> Entity [get; ]
  Property: EntityId -> Int32 [get; ]
  Property: HierarchyDiff -> HierarchyDiff [get; ]
  Property: Kind -> EntityDiffKind [get; ]
  Property: VersionMismatch -> Boolean [get; ]
Type: MiniArch.Diagnostics.EntityDiffKind
  Field: Different -> EntityDiffKind [static, literal]
  Field: OnlyInA -> EntityDiffKind [static, literal]
  Field: OnlyInB -> EntityDiffKind [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.EntityDump
  Method: Describe(World world, Entity entity) -> EntityReport
Type: MiniArch.Diagnostics.EntityReport
  Method: ToString() -> String
  Property: Archetype -> Nullable`1 [get; ]
  Property: Children -> ReadOnlyCollection`1 [get; ]
  Property: Components -> ReadOnlyCollection`1 [get; ]
  Property: Id -> Int32 [get; ]
  Property: IsAlive -> Boolean [get; ]
  Property: Parent -> Nullable`1 [get; ]
  Property: Version -> Int32 [get; ]
Type: MiniArch.Diagnostics.FreeListDiff
  Method: ToString() -> String
  Property: EntitySlotCountA -> Int32 [get; ]
  Property: EntitySlotCountB -> Int32 [get; ]
  Property: FreeCountA -> Int32 [get; ]
  Property: FreeCountB -> Int32 [get; ]
  Property: SlotDiffs -> ReadOnlyCollection`1 [get; ]
Type: MiniArch.Diagnostics.FreeSlotDiff
  Method: ToString() -> String
  Property: Kind -> FreeSlotDiffKind [get; ]
  Property: SlotIdA -> Int32 [get; ]
  Property: SlotIdB -> Int32 [get; ]
  Property: VersionA -> Int32 [get; ]
  Property: VersionB -> Int32 [get; ]
Type: MiniArch.Diagnostics.FreeSlotDiffKind
  Field: ExtraInA -> FreeSlotDiffKind [static, literal]
  Field: ExtraInB -> FreeSlotDiffKind [static, literal]
  Field: OrderMismatch -> FreeSlotDiffKind [static, literal]
  Field: VersionMismatch -> FreeSlotDiffKind [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.HierarchyDiff
  Method: ToString() -> String
  Property: ParentIdA -> Int32 [get; ]
  Property: ParentIdB -> Int32 [get; ]
Type: MiniArch.Diagnostics.ValidationCategory
  Field: Archetype -> ValidationCategory [static, literal]
  Field: EntitySlot -> ValidationCategory [static, literal]
  Field: FreeList -> ValidationCategory [static, literal]
  Field: Hierarchy -> ValidationCategory [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.ValidationCode
  Field: ArchetypeEntityCount -> ValidationCode [static, literal]
  Field: AsymmetricParent -> ValidationCode [static, literal]
  Field: DuplicateEntityId -> ValidationCode [static, literal]
  Field: FreeListDuplicate -> ValidationCode [static, literal]
  Field: FreeListOccupied -> ValidationCode [static, literal]
  Field: HierarchyCycle -> ValidationCode [static, literal]
  Field: OrphanedChild -> ValidationCode [static, literal]
  Field: OrphanedSlot -> ValidationCode [static, literal]
  Field: SlotCapacityWarning -> ValidationCode [static, literal]
  Field: SlotCollision -> ValidationCode [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.ValidationIssue
  Method: ToString() -> String
  Property: Category -> ValidationCategory [get; ]
  Property: Code -> ValidationCode [get; ]
  Property: Description -> String [get; ]
  Property: Severity -> ValidationSeverity [get; ]
Type: MiniArch.Diagnostics.ValidationResult
  Property: IsValid -> Boolean [get; ]
  Property: Issues -> ReadOnlyCollection`1 [get; ]
Type: MiniArch.Diagnostics.ValidationSeverity
  Field: Error -> ValidationSeverity [static, literal]
  Field: Warning -> ValidationSeverity [static, literal]
  Field: value__ -> Int32
Type: MiniArch.Diagnostics.WorldDiff
  Method: Compare(World worldA, World worldB) -> WorldDiffResult
Type: MiniArch.Diagnostics.WorldDiffResult
  Property: AreIdentical -> Boolean [get; ]
  Property: EntityDiffs -> IReadOnlyList`1 [get; ]
  Property: FreeListDiff -> FreeListDiff [get; ]
Type: MiniArch.Diagnostics.WorldDigest
  Method: Compute(World world) -> WorldDigestResult
Type: MiniArch.Diagnostics.WorldDigestResult
  Property: FreeList -> Byte[] [get; ]
  Property: Hierarchy -> Byte[] [get; ]
  Property: Occupancy -> Byte[] [get; ]
  Property: PerArchetype -> IReadOnlyDictionary`2 [get; ]
  Property: PerComponent -> IReadOnlyDictionary`2 [get; ]
  Property: Total -> Byte[] [get; ]
Type: MiniArch.Diagnostics.WorldValidator
  Method: Validate(World world) -> ValidationResult
Type: MiniArch.Entity
  Method: CompareTo(Entity other) -> Int32
  Method: Deconstruct(Int32& Id, Int32& Version) -> Void
  Method: Equals(Entity other) -> Boolean
  Method: Equals(Object obj) -> Boolean
  Method: GetHashCode() -> Int32
  Method: ToString() -> String
  Property: Id -> Int32 [get; set; ]
  Property: IsPlaceholder -> Boolean [get; ]
  Property: IsUnmappedSentinel -> Boolean [get; ]
  Property: IsValid -> Boolean [get; ]
  Property: Version -> Int32 [get; set; ]
  Operator: op_Equality(Entity, Entity) -> Boolean
  Operator: op_Inequality(Entity, Entity) -> Boolean
Type: MiniArch.IChangeHandler`1
  Method: OnChange(World world, Entity entity, TComponent& oldValue, TComponent& newValue) -> Void
Type: MiniArch.IChangeHandler`2
  Method: OnChange(World world, Entity entity, TValue oldValue, TValue newValue) -> Void
  Method: Project(TComponent& component) -> TValue
Type: MiniArch.IChunkForEach
  Method: OnChunk(ChunkView chunk) -> Void
Type: MiniArch.ITransitionHandler
  Method: OnChange(World world, Entity entity, TransitionKind kind) -> Void
Type: MiniArch.OrderedComponentEnumerator`1
  Method: Dispose() -> Void
  Method: MoveNext() -> Boolean
  Property: Current -> Entity [get; ]
Type: MiniArch.OrderedComponentQuery`1
  Method: GetEnumerator() -> OrderedComponentEnumerator`1
Type: MiniArch.OrderedEntityEnumerator
  Method: Dispose() -> Void
  Method: MoveNext() -> Boolean
  Property: Current -> Entity [get; ]
Type: MiniArch.OrderedEntityQuery
  Method: GetEnumerator() -> OrderedEntityEnumerator
Type: MiniArch.Query
  Method: ForEachChunk(ChunkAction action) -> Void
  Method: ForEachChunk(TForEach& forEach) -> Void
  Method: ForEachChunkParallel(ChunkAction action) -> Void
  Method: ForEachChunkParallel(TForEach forEach) -> Void
  Method: GetChunks() -> ReadOnlySpan`1
  Method: GetEnumerator() -> QueryEnumerator
  Method: OrderByComponent(Comparison`1 comparison) -> OrderedComponentQuery`1
  Method: OrderByComponent(IComparer`1 comparer) -> OrderedComponentQuery`1
  Method: OrderByComponentDescending(Comparison`1 comparison) -> OrderedComponentQuery`1
  Method: OrderByComponentDescending(IComparer`1 comparer) -> OrderedComponentQuery`1
  Method: OrderByEntityId() -> OrderedEntityQuery
  Method: OrderByEntityIdDescending() -> OrderedEntityQuery
  Property: RefreshCount -> Int32 [get; ]
Type: MiniArch.QueryDescription
  Method: Equals(Object obj) -> Boolean
  Method: Equals(QueryDescription other) -> Boolean
  Method: GetHashCode() -> Int32
  Method: With() -> QueryDescription
  Method: WithAny() -> QueryDescription
  Method: Without() -> QueryDescription
  Property: AnyTypes -> IReadOnlyList`1 [get; ]
  Property: ExcludedTypes -> IReadOnlyList`1 [get; ]
  Property: RequiredTypes -> IReadOnlyList`1 [get; ]
  Operator: op_Equality(QueryDescription, QueryDescription) -> Boolean
  Operator: op_Inequality(QueryDescription, QueryDescription) -> Boolean
Type: MiniArch.QueryEnumerator
  Method: MoveNext() -> Boolean
  Property: Current -> Entity [get; ]
Type: MiniArch.TransitionKind
  Field: Entered -> TransitionKind [static, literal]
  Field: Exited -> TransitionKind [static, literal]
  Field: value__ -> Int32
Type: MiniArch.TransitionWatch`1
  Method: Diff(World world) -> Void
  Method: Snapshot(World world) -> Void
  Property: Handler -> THandler& [get; ]
Type: MiniArch.World
  Method: Access(Entity entity) -> EntityAccessor
  Method: Add(Entity entity, T component) -> Void
  Method: AddChild(Entity parent, Entity child) -> Void
  Method: CanonicalChecksum() -> Byte[]
  Method: CaptureState() -> WorldStateSnapshot
  Method: Checksum() -> Byte[]
  Method: Clone() -> World
  Method: Clone(Entity source) -> Entity
  Method: Create() -> Entity
  Method: Create(T1 component1) -> Entity
  Method: Create(T1 component1, T2 component2) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15) -> Entity
  Method: Create(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16) -> Entity
  Method: CreateMany(Span`1 entities) -> Void
  Method: Destroy(Entity entity) -> Void
  Method: Dispose() -> Void
  Method: EnsureCapacity(Int32 entityCapacity) -> Void
  Method: EnumerateChildren(Entity parent) -> ChildrenEnumerable
  Method: Get(Entity entity) -> T
  Method: GetArchetypeStats() -> ArchetypeStats[]
  Method: GetRef(Entity entity) -> T&
  Method: GetSingleton() -> Entity
  Method: GetStats() -> WorldStats
  Method: Has(Entity entity) -> Boolean
  Method: HasChildren(Entity entity) -> Boolean
  Method: IsAlive(Entity entity) -> Boolean
  Method: Query(QueryDescription& description) -> Query
  Method: Remove(Entity entity) -> Void
  Method: RemoveChild(Entity child) -> Void
  Method: RestoreState(WorldStateSnapshot snapshot) -> Void
  Method: Set(Entity entity, T component) -> Void
  Method: TryGet(Entity entity, T& component) -> Boolean
  Method: TryGetParent(Entity child, Entity& parent) -> Boolean
  Method: Watch(Nullable`1 query) -> ChangeWatch`2
  Method: Watch(Nullable`1 query) -> ChangeWatch`3
  Method: Watch(QueryDescription filter) -> TransitionWatch`1
  Property: EntityCapacity -> Int32 [get; ]
  Property: EntityCount -> Int32 [get; ]
Type: MiniArch.WorldStats
  Property: ArchetypeCount -> Int32 [get; ]
  Property: EntityCapacity -> Int32 [get; ]
  Property: EntityCount -> Int32 [get; ]
  Property: RecycledEntityCount -> Int32 [get; ]";

    private static string GetExpectedBaseline()
    {
        // If the environment variable is set, generate baseline and write to file.
        var generateFlag = Environment.GetEnvironmentVariable("GENERATE_API_BASELINE");
        if (!string.IsNullOrEmpty(generateFlag))
        {
            string actual = GetPublicApiString();
            string baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt");
            File.WriteAllText(baselinePath, actual);
            return actual; // Return the same as actual so test passes in generation mode.
        }

        // Otherwise use the embedded baseline.
        return EmbeddedBaseline;
    }
}