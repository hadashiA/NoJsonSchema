namespace NoJsonSchema.Core.Ir;

/// <summary>
/// One branch of a polymorphic type — pairs the discriminator value seen on the wire
/// with the concrete subtype the parser should dispatch to.
/// </summary>
public sealed class PolymorphicBranch
{
    /// <summary>The literal string the discriminator field carries (e.g. <c>"cat"</c>).</summary>
    public required string DiscriminatorValue { get; init; }

    /// <summary>The C# type name of the concrete subtype (must inherit from the polymorphic base).</summary>
    public required string TypeName { get; init; }
}

/// <summary>
/// Polymorphism metadata attached to a base <see cref="ObjectTypeDescriptor"/>.
/// The Formatter emits a peek on the discriminator field, then dispatches to the matching branch.
/// </summary>
public sealed class PolymorphicInfo
{
    /// <summary>The JSON property name that carries the discriminator (e.g. <c>"petType"</c>).</summary>
    public required string DiscriminatorJsonName { get; init; }

    public IReadOnlyList<PolymorphicBranch> Branches { get; init; } = [];
}
