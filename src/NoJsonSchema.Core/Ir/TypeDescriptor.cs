namespace NoJsonSchema.Core.Ir;

/// <summary>
/// A named type that the generator will materialise as a C# type.
/// Concrete kinds live in this namespace as separate records.
/// </summary>
public abstract record TypeDescriptor
{
    /// <summary>C# identifier for the type (PascalCase, unique within the graph).</summary>
    public required string Name { get; init; }

    /// <summary>JSON Pointer of the source schema node.</summary>
    public string SourcePointer { get; init; } = "#";

    public string? Description { get; init; }
    public bool Deprecated { get; init; }
}
