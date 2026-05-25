using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Renders a <see cref="TypeRef"/> as the C# type expression the emitter writes into source.
/// Arrays land as <c>T[]</c>, dictionaries as <c>Dictionary&lt;string, V&gt;</c>, primitives map 1:1.
/// </summary>
public static class TypeExpression
{
    public static string Render(TypeRef type)
    {
        return type switch
        {
            TypeRef.Primitive p  => RenderPrimitive(p.Kind),
            TypeRef.Named n      => NameFactory.EscapeIfReserved(n.Name),
            TypeRef.Array a      => Render(a.Element) + "[]",
            TypeRef.Dictionary d => "global::System.Collections.Generic.Dictionary<string, " + Render(d.Value) + ">",
            TypeRef.Nullable nu  => RenderNullable(nu),
            TypeRef.Any          => "object?",
            // Reason is recorded in IR + XML doc; the rendered type is just object? so it composes
            // cleanly with nullable / array / dictionary wrappers.
            TypeRef.Unsupported  => "object?",
            _ => throw new InvalidOperationException($"Unhandled TypeRef: {type}"),
        };
    }

    public static string RenderPrimitive(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.String         => "string",
        PrimitiveKind.Int32          => "int",
        PrimitiveKind.Int64          => "long",
        PrimitiveKind.Single         => "float",
        PrimitiveKind.Double         => "double",
        PrimitiveKind.Boolean        => "bool",
        PrimitiveKind.DateTimeOffset => "global::System.DateTimeOffset",
        PrimitiveKind.Guid           => "global::System.Guid",
        _ => throw new InvalidOperationException($"Unhandled PrimitiveKind: {kind}"),
    };

    /// <summary>True when the type is a CLR value type (no reference identity, can't be null without ?).</summary>
    public static bool IsValueType(TypeRef type) => type switch
    {
        TypeRef.Primitive p => p.Kind != PrimitiveKind.String,
        TypeRef.Nullable nu => IsValueType(nu.Inner),
        _ => false,
    };

    public static bool IsPrimitive(TypeRef type) => type is TypeRef.Primitive;

    static string RenderNullable(TypeRef.Nullable nu)
    {
        var inner = Render(nu.Inner);
        return inner.EndsWith("?", StringComparison.Ordinal) ? inner : inner + "?";
    }
}
