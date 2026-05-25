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
}
