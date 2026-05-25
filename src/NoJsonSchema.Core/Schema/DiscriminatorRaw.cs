namespace NoJsonSchema.Core.Schema;

/// <summary>
/// Raw <c>discriminator</c> block as it appears in the source schema (OpenAPI 3.x):
/// the property name carrying the tag, plus an optional explicit mapping from tag value to <c>$ref</c>.
/// The Resolver pass turns this into <see cref="Ir.PolymorphicInfo"/>.
/// </summary>
public sealed class DiscriminatorRaw
{
    public required string PropertyName { get; init; }

    /// <summary>Optional explicit mapping: discriminator value → <c>$ref</c> string.</summary>
    public IReadOnlyDictionary<string, string>? Mapping { get; init; }
}
