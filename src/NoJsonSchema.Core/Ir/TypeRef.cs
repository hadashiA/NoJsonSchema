namespace NoJsonSchema.Core.Ir;

/// <summary>
/// A reference to a type as used at a property or array-element position.
/// Lightweight, immutable, and shareable. The Emitter uses this to write the C# type expression.
/// </summary>
public abstract record TypeRef
{
    /// <summary>A scalar JSON type mapped to a C# primitive.</summary>
    public sealed record Primitive(PrimitiveKind Kind) : TypeRef;

    /// <summary>A reference to a named type defined in the <see cref="TypeGraph"/>.</summary>
    public sealed record Named(string Name) : TypeRef;

    /// <summary>A homogeneous array of <see cref="Element"/>.</summary>
    public sealed record Array(TypeRef Element) : TypeRef;

    /// <summary>A string-keyed dictionary with values of <see cref="Value"/>. Used for additionalProperties-as-schema.</summary>
    public sealed record Dictionary(TypeRef Value) : TypeRef;

    /// <summary>A nullable wrapper. Idempotent: <c>Nullable(Nullable(x)) == Nullable(x)</c> after canonicalisation.</summary>
    public sealed record Nullable(TypeRef Inner) : TypeRef;

    /// <summary>Arbitrary JSON value with no schema (mapped to an <c>object?</c> hold-anything node in emit).</summary>
    public sealed record Any : TypeRef
    {
        public static readonly Any Instance = new();
    }

    /// <summary>
    /// Construct not yet supported by IR (allOf/oneOf/anyOf composition, enum, etc).
    /// Emitter must surface a clear error if it tries to materialise one of these.
    /// </summary>
    public sealed record Unsupported(string Reason) : TypeRef;

    // Convenience constants.
    public static readonly Primitive PrimitiveString         = new(PrimitiveKind.String);
    public static readonly Primitive PrimitiveInt32          = new(PrimitiveKind.Int32);
    public static readonly Primitive PrimitiveInt64          = new(PrimitiveKind.Int64);
    public static readonly Primitive PrimitiveSingle         = new(PrimitiveKind.Single);
    public static readonly Primitive PrimitiveDouble         = new(PrimitiveKind.Double);
    public static readonly Primitive PrimitiveBoolean        = new(PrimitiveKind.Boolean);
    public static readonly Primitive PrimitiveDateTimeOffset = new(PrimitiveKind.DateTimeOffset);
    public static readonly Primitive PrimitiveGuid           = new(PrimitiveKind.Guid);

    /// <summary>Wraps <paramref name="inner"/> in a <see cref="Nullable"/> unless it already is one.</summary>
    public static TypeRef MakeNullable(TypeRef inner) => inner is Nullable ? inner : new Nullable(inner);
}
