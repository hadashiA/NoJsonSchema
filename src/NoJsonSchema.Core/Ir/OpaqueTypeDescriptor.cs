namespace NoJsonSchema.Core.Ir;

/// <summary>
/// A type whose schema uses constructs not yet lowered to IR (allOf / oneOf / anyOf / enum / const).
/// Kept in the graph so referencing types can resolve their names, then filled in by later passes (M5/M6).
/// </summary>
public sealed class OpaqueTypeDescriptor : TypeDescriptor
{
    public required string Reason { get; init; }
}
