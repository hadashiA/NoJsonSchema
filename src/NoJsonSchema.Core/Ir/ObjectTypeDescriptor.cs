namespace NoJsonSchema.Core.Ir;

public sealed class ObjectTypeDescriptor : TypeDescriptor
{
    /// <summary>
    /// Name of the base type when this object was synthesised from <c>allOf: [$ref base, inline ...]</c>.
    /// The POCO inherits from it; the Formatter still emits/consumes all fields (own + inherited).
    /// </summary>
    public string? BaseTypeName { get; init; }

    /// <summary>True when this type acts as the abstract base of a polymorphic family (future oneOf+discriminator).</summary>
    public bool IsAbstract { get; init; }

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
