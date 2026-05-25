namespace NoJsonSchema.Core;

public enum TypeStyle
{
    /// <summary>Mutable POCO with <c>{ get; set; }</c> properties (default).</summary>
    Class,

    /// <summary>Immutable <c>partial record</c> with <c>{ get; init; }</c> properties.</summary>
    Record,

    /// <summary>
    /// Immutable <c>readonly partial record struct</c> declared in positional / primary-ctor form.
    /// Suitable for small value objects (IDs, coordinates, semver, …). The type:
    /// <list type="bullet">
    ///   <item>cannot use <c>allOf</c> inheritance,</item>
    ///   <item>cannot be referenced as another type's base,</item>
    ///   <item>is passed by <c>in</c> reference to its formatter to avoid copies.</item>
    /// </list>
    /// Requested per-type via <see cref="GenerationOptions.ValueObjectTypes"/>.
    /// </summary>
    ReadonlyRecordStruct,
}

public enum AllOfStrategy
{
    Inherit,
    Flatten,
}

public sealed class GenerationOptions
{
    public string Namespace { get; init; } = "Generated";
    public string? RootTypeName { get; init; }
    public TypeStyle TypeStyle { get; init; } = TypeStyle.Class;
    public AllOfStrategy AllOfStrategy { get; init; } = AllOfStrategy.Inherit;
    public bool StrictExtraProperties { get; init; }
    public bool EmitAsync { get; init; } = true;

    /// <summary>Override the namespace-wide serializer class name. Defaults to {NsLeaf}Serializer.</summary>
    public string? SerializerName { get; init; }

    /// <summary>
    /// Names of $defs entries that should be generated as <see cref="TypeStyle.ReadonlyRecordStruct"/>
    /// (value-object semantics) instead of the default style. Use for small immutable types like IDs,
    /// SemVer, coordinates, etc.
    /// </summary>
    public HashSet<string> ValueObjectTypes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Emit the C# 11+ <c>required</c> modifier on non-nullable required properties (replaces the
    /// <c>= default!;</c> suppression). Requires consumers to target a C# 11-aware compiler.
    /// </summary>
    public bool UseRequiredModifier { get; init; }

    /// <summary>
    /// When non-empty, generate only the named <c>$defs</c> / <c>components.schemas</c> entries
    /// plus everything they transitively depend on (base types, property types, polymorphic branches,
    /// array element / dictionary value types). Empty means "generate every type in the schema".
    /// Names are matched after PascalCase normalisation, so <c>"user"</c> and <c>"User"</c> both
    /// resolve to a <c>$defs/User</c> entry.
    /// </summary>
    public HashSet<string> IncludedTypes { get; init; } = new(StringComparer.Ordinal);
}
