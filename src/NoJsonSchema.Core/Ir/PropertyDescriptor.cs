namespace NoJsonSchema.Core.Ir;

/// <summary>
/// One JSON property on an object type, with both its on-wire name and the C# identifier the emitter will use.
/// </summary>
public sealed class PropertyDescriptor
{
    /// <summary>C# identifier (PascalCase). Guaranteed to be a legal C# identifier.</summary>
    public required string Name { get; init; }

    /// <summary>The original key in the JSON document, used verbatim during parse/emit.</summary>
    public required string JsonName { get; init; }

    public required TypeRef Type { get; init; }

    /// <summary>Declared in the schema's <c>required</c> array.</summary>
    public bool IsRequired { get; init; }

    /// <summary>True when the schema admits a literal <c>null</c> value (e.g. <c>type: ["string", "null"]</c>).</summary>
    public bool IsNullable { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// True when this property hides a base-class property of the same JSON name with a different
    /// CLR type (e.g. <c>Event.body: object?</c> narrowed to <c>StoppedEvent.body: StoppedEventBody</c>).
    /// The POCO emits a <c>new</c> modifier.
    /// </summary>
    public bool HidesBaseProperty { get; init; }

    /// <summary>JSON Pointer of the source schema node for diagnostics.</summary>
    public string SourcePointer { get; init; } = "#";
}
