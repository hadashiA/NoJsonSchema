namespace NoJsonSchema.Core.Ir;

/// <summary>
/// All named types discovered from a schema document, plus the entry-point reference.
/// </summary>
public sealed class TypeGraph
{
    /// <summary>Map from C# type name to descriptor. Iteration order is the discovery order.</summary>
    public required IReadOnlyDictionary<string, TypeDescriptor> Types { get; init; }

    /// <summary>How to refer to the document root (typically a <see cref="TypeRef.Named"/> or a primitive).</summary>
    public required TypeRef Root { get; init; }
}
