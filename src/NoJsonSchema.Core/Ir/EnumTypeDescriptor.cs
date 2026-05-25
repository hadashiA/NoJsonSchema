namespace NoJsonSchema.Core.Ir;

/// <summary>One member of an <see cref="EnumTypeDescriptor"/>.</summary>
public sealed class EnumMember
{
    /// <summary>C# identifier for the member (PascalCase, unique within the enum).</summary>
    public required string Name { get; init; }

    /// <summary>The JSON value this member matches. String for string enums, integer for int enums.</summary>
    public required string JsonValue { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// A schema mapped to a closed C# <c>enum</c> with parser/emitter overlay.
/// Open enums (DAP's <c>_enum</c> extension) are not lowered to this — they stay as plain strings.
/// </summary>
public sealed record EnumTypeDescriptor : TypeDescriptor
{
    /// <summary>Currently only <see cref="PrimitiveKind.String"/> is supported.</summary>
    public required PrimitiveKind Underlying { get; init; }

    public IReadOnlyList<EnumMember> Members { get; init; } = [];
}
