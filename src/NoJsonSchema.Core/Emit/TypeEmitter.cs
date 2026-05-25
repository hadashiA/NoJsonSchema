using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits a POCO file (<c>{Type}.g.cs</c>) for a single <see cref="ObjectTypeDescriptor"/>.
/// </summary>
public static class TypeEmitter
{
    public static string Emit(ObjectTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        if (type.Style == TypeStyle.ReadonlyRecordStruct)
        {
            return EmitRecordStruct(type, options);
        }
        return EmitClassOrRecord(type, graph, options);
    }

    static string EmitClassOrRecord(ObjectTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        WriteFileHeader(w);
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        EmitXmlDoc(w, type.Description);
        var modifier = type.IsAbstract ? "abstract " : "";
        var keyword = options.TypeStyle == TypeStyle.Record ? "partial record" : "partial class";
        var inheritance = type.BaseTypeName is null
            ? ""
            : " : " + NameFactory.EscapeIfReserved(type.BaseTypeName);
        using (w.Block($"public {modifier}{keyword} {NameFactory.EscapeIfReserved(type.Name)}{inheritance}"))
        {
            if (options.UseRequiredModifier && HasAnyRequired(type))
            {
                EmitSetsRequiredMembersCtor(w, type);
                if (type.Properties.Count > 0) w.WriteLine();
            }

            for (var i = 0; i < type.Properties.Count; i++)
            {
                if (i > 0) w.WriteLine();
                EmitProperty(w, type.Properties[i], options.TypeStyle, options, graph);
            }
        }

        return w.ToString();
    }

    static bool HasAnyRequired(ObjectTypeDescriptor type)
    {
        foreach (var p in type.Properties)
        {
            if (p.IsRequired && !p.IsNullable) return true;
        }
        return false;
    }

    /// <summary>
    /// When the user opts into the C# 11 <c>required</c> modifier, the formatter still needs a
    /// parameterless ctor to instantiate the type before populating the required members. Emit one
    /// annotated with <c>[SetsRequiredMembers]</c> so the compiler treats it as safe.
    /// </summary>
    static void EmitSetsRequiredMembersCtor(SourceWriter w, ObjectTypeDescriptor type)
    {
        var visibility = type.IsAbstract ? "protected" : "public";
        var baseCall = type.BaseTypeName is null ? "" : " : base()";
        w.WriteLine("[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        w.WriteLine($"{visibility} {NameFactory.EscapeIfReserved(type.Name)}(){baseCall} {{ }}");
    }

    /// <summary>
    /// Emit a value-object style declaration: <c>public readonly partial record struct T(P1 X, P2 Y);</c>.
    /// Required + non-null parameters come first; everything else is given a default so callers can
    /// construct it with positional args even when the schema marks a field optional.
    /// </summary>
    static string EmitRecordStruct(ObjectTypeDescriptor type, GenerationOptions options)
    {
        var w = new SourceWriter();
        WriteFileHeader(w);
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        EmitXmlDoc(w, type.Description);

        var ordered = type.Properties
            .Select(p => (Prop: p, Required: p.IsRequired && !p.IsNullable))
            .OrderByDescending(x => x.Required)
            .ToList();

        foreach (var (p, _) in ordered)
        {
            if (string.IsNullOrEmpty(p.Description)) continue;
            var lines = p.Description!.Split('\n');
            foreach (var line in lines)
            {
                w.WriteLine("/// <param name=\"" + NameFactory.EscapeIfReserved(p.Name) + "\">"
                    + System.Net.WebUtility.HtmlEncode(line.TrimEnd('\r')) + "</param>");
            }
        }

        var parts = new List<string>(ordered.Count);
        foreach (var (p, isRequired) in ordered)
        {
            var typeExpr = RenderPropertyType(p);
            var paramName = NameFactory.EscapeIfReserved(p.Name);
            var paramDecl = $"{typeExpr} {paramName}";
            if (!isRequired)
            {
                paramDecl += " = " + DefaultLiteral(p);
            }
            parts.Add(paramDecl);
        }

        var paramList = string.Join(", ", parts);
        w.WriteLine($"public readonly partial record struct {NameFactory.EscapeIfReserved(type.Name)}({paramList});");
        return w.ToString();
    }

    static string DefaultLiteral(PropertyDescriptor p)
    {
        // Nullable annotation already appended in RenderPropertyType for optional/nullable values.
        if (p.IsNullable || !p.IsRequired)
        {
            return TypeExpression.IsValueType(p.Type) ? "default" : "null";
        }
        return "default";
    }

    internal static void WriteFileHeader(SourceWriter w)
    {
        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#nullable enable");
        w.WriteLine();
    }

    internal static void EmitXmlDoc(SourceWriter w, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var lines = text!.Split('\n');
        w.WriteLine("/// <summary>");
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            w.WriteLine("/// " + System.Net.WebUtility.HtmlEncode(trimmed));
        }
        w.WriteLine("/// </summary>");
    }

    static void EmitProperty(SourceWriter w, PropertyDescriptor p, TypeStyle style, GenerationOptions options, TypeGraph graph)
    {
        EmitXmlDoc(w, p.Description);
        var typeExpr = RenderPropertyType(p);
        var name = NameFactory.EscapeIfReserved(p.Name);
        var accessors = style == TypeStyle.Record ? "{ get; init; }" : "{ get; set; }";
        var hiding = p.HidesBaseProperty ? "new " : "";

        var isRequiredNonNull = p.IsRequired && !p.IsNullable;
        var initializer = "";
        var requiredModifier = "";

        if (isRequiredNonNull)
        {
            if (options.UseRequiredModifier)
            {
                requiredModifier = "required ";
            }
            else if (!IsValueTypeReference(p.Type, graph))
            {
                // CS8618 suppression for non-nullable reference-typed properties.
                initializer = " = null!;";
            }
        }

        w.WriteLine($"public {hiding}{requiredModifier}{typeExpr} {name} {accessors}{initializer}");
    }

    /// <summary>
    /// True when <paramref name="type"/> resolves to a CLR value type. Looks at primitives plus the
    /// graph's named entries (enum / readonly record struct value-object) — pure <see cref="TypeRef"/>
    /// can't know that on its own.
    /// </summary>
    static bool IsValueTypeReference(Ir.TypeRef type, TypeGraph graph)
    {
        return type switch
        {
            Ir.TypeRef.Primitive p => p.Kind != Ir.PrimitiveKind.String,
            Ir.TypeRef.Nullable nu => IsValueTypeReference(nu.Inner, graph),
            Ir.TypeRef.Named n =>
                graph.Types.TryGetValue(n.Name, out var desc)
                && (desc is Ir.EnumTypeDescriptor
                    || (desc is Ir.ObjectTypeDescriptor obj && obj.Style == TypeStyle.ReadonlyRecordStruct)),
            _ => false,
        };
    }


    static string RenderPropertyType(PropertyDescriptor p)
    {
        var expr = TypeExpression.Render(p.Type);
        if (p.IsRequired && !p.IsNullable) return expr;
        return expr.EndsWith("?", StringComparison.Ordinal) ? expr : expr + "?";
    }
}
