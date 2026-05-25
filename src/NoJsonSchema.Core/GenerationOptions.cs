namespace NoJsonSchema.Core;

public enum TypeStyle
{
    Class,
    Record,
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
}
