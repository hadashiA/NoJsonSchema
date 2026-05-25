namespace NoJsonSchema.Core.Ir;

public sealed record ObjectTypeDescriptor : TypeDescriptor
{
    /// <summary>
    /// C# materialisation style for this type. Defaults to the project-wide
    /// <see cref="GenerationOptions.TypeStyle"/>; set per-type for value-object generation.
    /// </summary>
    public TypeStyle Style { get; init; } = TypeStyle.Class;

    /// <summary>
    /// Name of the base type when this object was synthesised from <c>allOf: [$ref base, inline ...]</c>.
    /// The POCO inherits from it; the Formatter still emits/consumes all fields (own + inherited).
    /// </summary>
    public string? BaseTypeName { get; init; }

    /// <summary>True when this type acts as the abstract base of a polymorphic family.</summary>
    public bool IsAbstract { get; init; }

    /// <summary>
    /// When set, this type is the base of a discriminated polymorphic family
    /// (<c>oneOf + discriminator</c>). The Formatter for this type peeks the discriminator field
    /// and dispatches to the matching branch.
    /// </summary>
    public PolymorphicInfo? Polymorphic { get; init; }

    public IReadOnlyList<PropertyDescriptor> Properties { get; init; } = [];

    /// <summary>
    /// Schema for unknown keys (when permitted): the value type for an overflow bag.
    /// <c>null</c> = no overflow bag is generated. See <see cref="AdditionalPropertiesDenied"/>.
    /// </summary>
    public TypeRef? AdditionalProperties { get; init; }

    /// <summary>
    /// True when the schema set <c>additionalProperties: false</c>.
    /// Unknown keys always throw, regardless of the runtime <c>StrictExtraProperties</c> option.
    /// </summary>
    public bool AdditionalPropertiesDenied { get; init; }
}
