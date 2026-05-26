using System.Text;
using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits <c>{Type}Formatter.g.cs</c> for a single <see cref="ObjectTypeDescriptor"/> —
/// the type-specific Deserialize / Serialize implementation.
/// </summary>
public static class FormatterEmitter
{
    readonly record struct EmitContext(TypeGraph Graph)
    {
        public bool IsEnum(string name) => Graph.Types.TryGetValue(name, out var d) && d is EnumTypeDescriptor;

        public bool IsStruct(string name) =>
            Graph.Types.TryGetValue(name, out var d)
                && d is ObjectTypeDescriptor obj
                && obj.Style == TypeStyle.ReadonlyRecordStruct;

        /// <summary>
        /// Graph-aware extension of <see cref="TypeExpression.IsValueType"/> — recognises named enum
        /// + struct types in addition to the primitive set. Used to decide whether a property whose
        /// declared type is <c>T?</c> needs <c>.Value</c> when forwarded to an <c>in T</c> parameter.
        /// </summary>
        public bool IsValueTypeReference(TypeRef type) => type switch
        {
            TypeRef.Nullable nu => IsValueTypeReference(nu.Inner),
            TypeRef.Named n => IsEnum(n.Name) || IsStruct(n.Name),
            _ => TypeExpression.IsValueType(type),
        };
    }

    public static string Emit(ObjectTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine("using System;");
        w.WriteLine("using System.Buffers;");
        w.WriteLine();
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        var formatterName = type.Name + "Formatter";
        var ctx = new EmitContext(graph);

        // The Formatter is a `static partial class` — purely a function bag. Internal-by-default so
        // it doesn't leak into the user's public surface; the Serializer is the only entry point.
        if (type.Polymorphic is not null)
        {
            using (w.Block($"static partial class {NameFactory.EscapeIfReserved(formatterName)}"))
            {
                EmitPolymorphicBody(w, type);
            }
            return w.ToString();
        }

        var allProps = FlattenProperties(type, graph);
        var isStruct = type.Style == TypeStyle.ReadonlyRecordStruct;

        using (w.Block($"static partial class {NameFactory.EscapeIfReserved(formatterName)}"))
        {
            EmitPropertyNameFields(w, allProps);
            w.WriteLine();

            if (isStruct)
            {
                EmitStructDeserialize(w, type);
                w.WriteLine();
                EmitStructReadValue(w, type, allProps, ctx);
                w.WriteLine();
                EmitStructSerialize(w, type);
                w.WriteLine();
                EmitStructWriteValue(w, type, allProps, ctx);
            }
            else
            {
                EmitDeserialize(w, type, options);
                w.WriteLine();
                EmitReadInto(w, type, allProps, options, ctx);
                w.WriteLine();
                EmitSerialize(w, type, options);
                w.WriteLine();
                EmitWriteValue(w, type, allProps, options, ctx);
            }
        }

        return w.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Polymorphic dispatch: peek a discriminator field, then forward to the matching branch.
    // ---------------------------------------------------------------------------------------------

    static void EmitPolymorphicBody(SourceWriter w, ObjectTypeDescriptor type)
    {
        var poly = type.Polymorphic!;
        var discLiteral = EncodeUtf8Literal(EscapeJsonString(poly.DiscriminatorJsonName));
        w.WriteLine($"static global::System.ReadOnlySpan<byte> DiscriminatorName => {discLiteral};");
        w.WriteLine();

        using (w.Block($"public static {type.Name} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
            w.WriteLine("tokenizer.ReadStartObject();");
            w.WriteLine($"if (!tokenizer.TryPeekDiscriminator(DiscriminatorName, out var __disc)) tokenizer.ThrowFormatException(\"Missing discriminator '{poly.DiscriminatorJsonName}' on " + type.Name + "\");");
            foreach (var b in poly.Branches)
            {
                var valueLiteral = EncodeUtf8Literal(EscapeJsonString(b.DiscriminatorValue));
                using (w.Block($"if (__disc.SequenceEqual({valueLiteral}))"))
                {
                    w.WriteLine($"var v = new {b.TypeName}();");
                    w.WriteLine($"{b.TypeName}Formatter.ReadInto(ref tokenizer, v, options);");
                    w.WriteLine("return v;");
                }
            }
            w.WriteLine("tokenizer.ThrowFormatException(\"Unknown discriminator value '\" + global::System.Text.Encoding.UTF8.GetString(__disc) + \"' on " + type.Name + "\");");
            w.WriteLine("return default!; // unreachable");
        }

        w.WriteLine();
        using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {type.Name} value, NoJsonSerializerOptions options)"))
        {
            // Plain if-is chain — easier to read at a glance than `switch (value) { case ... }`.
            // JIT folds the type checks just as efficiently for sealed branches.
            foreach (var b in poly.Branches)
            {
                using (w.Block($"if (value is {b.TypeName} __sub_{b.TypeName})"))
                {
                    w.WriteLine($"{b.TypeName}Formatter.Serialize(writer, __sub_{b.TypeName}, options);");
                    w.WriteLine("return;");
                }
            }
            w.WriteLine($"throw new global::System.InvalidOperationException(\"Unknown {type.Name} subtype: \" + value!.GetType());");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Property layout: walk the base chain to produce a flat ordered list (base first, derived last).
    // ---------------------------------------------------------------------------------------------

    static IReadOnlyList<PropertyDescriptor> FlattenProperties(ObjectTypeDescriptor type, TypeGraph graph)
    {
        if (type.BaseTypeName is null) return type.Properties;

        var chain = new List<ObjectTypeDescriptor> { type };
        var current = type;
        while (current.BaseTypeName is not null
            && graph.Types.TryGetValue(current.BaseTypeName, out var b)
            && b is ObjectTypeDescriptor obj)
        {
            chain.Add(obj);
            current = obj;
        }
        chain.Reverse(); // base first

        // Walk base-first. When a derived type re-declares the same JSON property (a narrowed
        // override), keep the base's slot index but swap in the derived descriptor so the Formatter
        // emits/reads the more specific type at that position.
        var indexByJsonName = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<PropertyDescriptor>();
        foreach (var node in chain)
        {
            foreach (var p in node.Properties)
            {
                if (indexByJsonName.TryGetValue(p.JsonName, out var idx))
                {
                    ordered[idx] = p; // derived override replaces the base entry in place
                }
                else
                {
                    indexByJsonName[p.JsonName] = ordered.Count;
                    ordered.Add(p);
                }
            }
        }
        return ordered;
    }

    static void EmitPropertyNameFields(SourceWriter w, IReadOnlyList<PropertyDescriptor> properties)
    {
        foreach (var p in properties)
        {
            var literal = EncodeUtf8Literal("\"" + EscapeJsonString(p.JsonName) + "\":");
            w.WriteLine($"static global::System.ReadOnlySpan<byte> Name_{p.Name} => {literal};");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Deserialize entry points + ReadInto.
    // ---------------------------------------------------------------------------------------------

    static void EmitDeserialize(SourceWriter w, ObjectTypeDescriptor type, GenerationOptions options)
    {
        _ = options;
        using (w.Block($"public static {type.Name} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
            w.WriteLine("tokenizer.ReadStartObject();");
            w.WriteLine($"var value = new {type.Name}();");
            w.WriteLine("ReadInto(ref tokenizer, value, options);");
            w.WriteLine("return value;");
        }
    }

    static void EmitReadInto(SourceWriter w, ObjectTypeDescriptor type, IReadOnlyList<PropertyDescriptor> properties, GenerationOptions options, EmitContext ctx)
    {
        _ = options;
        using (w.Block($"internal static void ReadInto(ref Utf8JsonTokenizer tokenizer, {type.Name} value, NoJsonSerializerOptions options)"))
        {
            if (properties.Count == 0)
            {
                using (w.Block("while (tokenizer.TryReadPropertyName(out var __name))"))
                {
                    EmitUnknownPropertyBranch(w, type);
                }
                return;
            }

            // Length-bucketed dispatch on the property name's UTF-8 byte length.
            var groups = properties
                .GroupBy(p => Encoding.UTF8.GetByteCount(p.JsonName))
                .OrderBy(g => g.Key)
                .ToList();

            using (w.Block("while (tokenizer.TryReadPropertyName(out var __name))"))
            {
                using (w.Block("switch (__name.Length)"))
                {
                    foreach (var group in groups)
                    {
                        w.WriteLine($"case {group.Key}:");
                        w.Indent();
                        EmitLengthGroup(w, group, ctx, type);
                        w.WriteLine("break;");
                        w.Outdent();
                    }

                    w.WriteLine("default:");
                    w.Indent();
                    EmitUnknownPropertyBranch(w, type);
                    w.WriteLine("break;");
                    w.Outdent();
                }
            }
        }
    }

    static void EmitLengthGroup(SourceWriter w, IGrouping<int, PropertyDescriptor> group, EmitContext ctx, ObjectTypeDescriptor type)
    {
        var first = true;
        foreach (var p in group)
        {
            var literal = EncodeUtf8Literal(EscapeJsonString(p.JsonName));
            w.WriteLine($"{(first ? "if" : "else if")} (__name.SequenceEqual({literal}))");
            using (w.BraceBlock())
            {
                EmitReadValueInto(w, p, ctx);
            }
            first = false;
        }
        w.WriteLine("else");
        using (w.BraceBlock())
        {
            EmitUnknownPropertyBranch(w, type);
        }
    }

    static void EmitUnknownPropertyBranch(SourceWriter w, ObjectTypeDescriptor type)
    {
        if (type.AdditionalPropertiesDenied)
        {
            w.WriteLine("tokenizer.ThrowFormatException(\"Unknown property '\" + global::System.Text.Encoding.UTF8.GetString(__name) + \"' on " + type.Name + " (additionalProperties:false)\");");
        }
        else
        {
            w.WriteLine("if (options.StrictExtraProperties) tokenizer.ThrowFormatException(\"Unknown property '\" + global::System.Text.Encoding.UTF8.GetString(__name) + \"'\");");
            w.WriteLine("tokenizer.SkipValue();");
        }
    }

    static void EmitReadValueInto(SourceWriter w, PropertyDescriptor p, EmitContext ctx)
    {
        if (p.IsNullable || !p.IsRequired)
        {
            using (w.Block("if (tokenizer.TryReadNull())"))
            {
                w.WriteLine($"value.{p.Name} = default;");
            }
            w.WriteLine("else");
            using (w.BraceBlock())
            {
                EmitReadCoreInto(w, p, ctx);
            }
        }
        else
        {
            EmitReadCoreInto(w, p, ctx);
        }
    }

    static void EmitReadCoreInto(SourceWriter w, PropertyDescriptor p, EmitContext ctx)
    {
        var inner = Unwrap(p.Type);
        switch (inner)
        {
            case TypeRef.Primitive prim:
                w.WriteLine($"value.{p.Name} = {ReadPrimitiveExpr(prim)};");
                break;

            case TypeRef.Named named when ctx.IsEnum(named.Name):
                w.WriteLine($"value.{p.Name} = {named.Name}Formatter.ReadValue(ref tokenizer);");
                break;

            case TypeRef.Named named when ctx.IsStruct(named.Name):
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"value.{p.Name} = {named.Name}Formatter.ReadValue(ref tokenizer, options);");
                break;

            case TypeRef.Named named:
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"var sub_{p.Name} = new {named.Name}();");
                w.WriteLine($"{named.Name}Formatter.ReadInto(ref tokenizer, sub_{p.Name}, options);");
                w.WriteLine($"value.{p.Name} = sub_{p.Name};");
                break;

            case TypeRef.Array arr:
                EmitReadArrayInto(w, p, arr, ctx);
                break;

            case TypeRef.Dictionary dict:
                EmitReadDictionaryInto(w, p, dict, ctx);
                break;

            case TypeRef.Any:
                w.WriteLine("tokenizer.SkipValue();");
                w.WriteLine($"value.{p.Name} = null;");
                break;

            default:
                w.WriteLine($"tokenizer.SkipValue(); // unsupported type for {p.Name}");
                break;
        }
    }

    static void EmitReadArrayInto(SourceWriter w, PropertyDescriptor p, TypeRef.Array arr, EmitContext ctx)
    {
        var elementType = TypeExpression.Render(arr.Element);
        w.WriteLine("tokenizer.ReadStartArray();");
        w.WriteLine($"var list_{p.Name} = new global::System.Collections.Generic.List<{elementType}>();");
        using (w.Block($"while (!tokenizer.TryReadEndArray())"))
        {
            EmitReadElementInto(w, arr.Element, $"list_{p.Name}", ctx);
        }
        w.WriteLine($"value.{p.Name} = list_{p.Name}.ToArray();");
    }

    static void EmitReadElementInto(SourceWriter w, TypeRef element, string listVar, EmitContext ctx)
    {
        switch (Unwrap(element))
        {
            case TypeRef.Primitive prim:
                w.WriteLine($"{listVar}.Add({ReadPrimitiveExpr(prim)});");
                break;
            case TypeRef.Named named when ctx.IsEnum(named.Name):
                w.WriteLine($"{listVar}.Add({named.Name}Formatter.ReadValue(ref tokenizer));");
                break;
            case TypeRef.Named named when ctx.IsStruct(named.Name):
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"{listVar}.Add({named.Name}Formatter.ReadValue(ref tokenizer, options));");
                break;
            case TypeRef.Named named:
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"var elem = new {named.Name}();");
                w.WriteLine($"{named.Name}Formatter.ReadInto(ref tokenizer, elem, options);");
                w.WriteLine($"{listVar}.Add(elem);");
                break;
            default:
                w.WriteLine("tokenizer.SkipValue(); // unsupported element");
                break;
        }
    }

    static void EmitReadDictionaryInto(SourceWriter w, PropertyDescriptor p, TypeRef.Dictionary dict, EmitContext ctx)
    {
        var valueType = TypeExpression.Render(dict.Value);
        w.WriteLine("tokenizer.ReadStartObject();");
        w.WriteLine($"var dict_{p.Name} = new global::System.Collections.Generic.Dictionary<string, {valueType}>();");
        using (w.Block($"while (tokenizer.TryReadPropertyName(out var __dictKey))"))
        {
            w.WriteLine($"var key_{p.Name} = global::System.Text.Encoding.UTF8.GetString(__dictKey);");
            switch (Unwrap(dict.Value))
            {
                case TypeRef.Primitive prim:
                    w.WriteLine($"dict_{p.Name}[key_{p.Name}] = {ReadPrimitiveExpr(prim)};");
                    break;
                case TypeRef.Named named when ctx.IsEnum(named.Name):
                    w.WriteLine($"dict_{p.Name}[key_{p.Name}] = {named.Name}Formatter.ReadValue(ref tokenizer);");
                    break;
                case TypeRef.Named named when ctx.IsStruct(named.Name):
                    w.WriteLine("tokenizer.ReadStartObject();");
                    w.WriteLine($"dict_{p.Name}[key_{p.Name}] = {named.Name}Formatter.ReadValue(ref tokenizer, options);");
                    break;
                case TypeRef.Named named:
                    w.WriteLine("tokenizer.ReadStartObject();");
                    w.WriteLine($"var dv = new {named.Name}();");
                    w.WriteLine($"{named.Name}Formatter.ReadInto(ref tokenizer, dv, options);");
                    w.WriteLine($"dict_{p.Name}[key_{p.Name}] = dv;");
                    break;
                default:
                    w.WriteLine($"tokenizer.SkipValue(); dict_{p.Name}[key_{p.Name}] = default!;");
                    break;
            }
        }
        w.WriteLine($"value.{p.Name} = dict_{p.Name};");
    }

    static string ReadPrimitiveExpr(TypeRef.Primitive prim) => prim.Kind switch
    {
        PrimitiveKind.String         => "tokenizer.ReadString()",
        PrimitiveKind.SByte          => "tokenizer.ReadSByte()",
        PrimitiveKind.Byte           => "tokenizer.ReadByte()",
        PrimitiveKind.Int16          => "tokenizer.ReadInt16()",
        PrimitiveKind.UInt16         => "tokenizer.ReadUInt16()",
        PrimitiveKind.Int32          => "tokenizer.ReadInt32()",
        PrimitiveKind.UInt32         => "tokenizer.ReadUInt32()",
        PrimitiveKind.Int64          => "tokenizer.ReadInt64()",
        PrimitiveKind.UInt64         => "tokenizer.ReadUInt64()",
        PrimitiveKind.Single         => "tokenizer.ReadSingle()",
        PrimitiveKind.Double         => "tokenizer.ReadDouble()",
        PrimitiveKind.Boolean        => "tokenizer.ReadBoolean()",
        PrimitiveKind.DateTimeOffset => "tokenizer.ReadDateTimeOffset()",
        PrimitiveKind.DateOnly       => "tokenizer.ReadDateOnly()",
        PrimitiveKind.TimeOnly       => "tokenizer.ReadTimeOnly()",
        PrimitiveKind.TimeSpan       => "tokenizer.ReadTimeSpan()",
        PrimitiveKind.Guid           => "tokenizer.ReadGuid()",
        PrimitiveKind.Uri            => "tokenizer.ReadUri()",
        PrimitiveKind.ByteArray      => "tokenizer.ReadByteArray()",
        _ => "default",
    };

    // ---------------------------------------------------------------------------------------------
    // Serialize entry points + WriteValue.
    // ---------------------------------------------------------------------------------------------

    static void EmitSerialize(SourceWriter w, ObjectTypeDescriptor type, GenerationOptions options)
    {
        _ = options;
        using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {type.Name} value, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var w = new Utf8JsonBufferWriter(writer);");
            w.WriteLine("WriteValue(ref w, value, options);");
            w.WriteLine("w.Flush();");
        }
    }

    static void EmitWriteValue(SourceWriter w, ObjectTypeDescriptor type, IReadOnlyList<PropertyDescriptor> properties, GenerationOptions options, EmitContext ctx)
    {
        _ = options;
        using (w.Block($"internal static void WriteValue(ref Utf8JsonBufferWriter w, {type.Name} value, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("w.WriteStartObject();");
            foreach (var p in properties)
            {
                EmitWriteProperty(w, p, ctx);
            }
            w.WriteLine("w.WriteEndObject();");
        }
    }

    static void EmitWriteProperty(SourceWriter w, PropertyDescriptor p, EmitContext ctx)
    {
        var skipNull = !p.IsRequired || p.IsNullable;
        var inner = Unwrap(p.Type);

        if (skipNull)
        {
            using (w.Block($"if (value.{p.Name} is null)"))
            {
                w.WriteLine("if (!options.SkipNullProperties)");
                using (w.BraceBlock())
                {
                    w.WriteLine($"w.WritePropertyNameRaw(Name_{p.Name});");
                    w.WriteLine("w.WriteNull();");
                }
            }
            w.WriteLine("else");
            using (w.BraceBlock())
            {
                w.WriteLine($"w.WritePropertyNameRaw(Name_{p.Name});");
                // The accessor has type T? for the optional/nullable case. When T is a value type
                // (primitive, enum, OR a value-object struct from the graph), we must call `.Value`
                // before forwarding to an `in T` parameter; for reference T the null-branch above
                // already returned, so the property is non-null at this point.
                var forceUnwrap = ctx.IsValueTypeReference(inner);
                EmitWriteCoreValue(w, p, inner, accessor: $"value.{p.Name}", forceUnwrap: forceUnwrap, ctx);
            }
        }
        else
        {
            w.WriteLine($"w.WritePropertyNameRaw(Name_{p.Name});");
            EmitWriteCoreValue(w, p, inner, accessor: $"value.{p.Name}", forceUnwrap: false, ctx);
        }
    }

    static void EmitWriteCoreValue(SourceWriter w, PropertyDescriptor p, TypeRef inner, string accessor, bool forceUnwrap, EmitContext ctx)
    {
        var read = forceUnwrap ? accessor + ".Value" : accessor;
        switch (inner)
        {
            case TypeRef.Primitive prim:
                w.WriteLine(WritePrimitiveExpr(prim, read) + ";");
                break;
            case TypeRef.Named named when ctx.IsEnum(named.Name):
                w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, {read});");
                break;
            case TypeRef.Named named when ctx.IsStruct(named.Name):
                {
                    // Property getters return by value, so spill into a local before passing 'in'.
                    var local = "__vo_" + p.Name;
                    w.WriteLine($"var {local} = {read};");
                    w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, in {local}, options);");
                    break;
                }
            case TypeRef.Named named:
                w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, {read}!, options);");
                break;
            case TypeRef.Array arr:
                w.WriteLine("w.WriteStartArray();");
                using (w.Block($"foreach (var item in {read}!)"))
                {
                    EmitWriteElement(w, arr.Element, ctx);
                }
                w.WriteLine("w.WriteEndArray();");
                break;
            case TypeRef.Dictionary dict:
                w.WriteLine("w.WriteStartObject();");
                using (w.Block($"foreach (var kv in {read}!)"))
                {
                    w.WriteLine("var keyBytes = global::System.Text.Encoding.UTF8.GetBytes(\"\\\"\" + kv.Key.Replace(\"\\\"\", \"\\\\\\\"\") + \"\\\":\");");
                    w.WriteLine("w.WritePropertyNameRaw(keyBytes);");
                    EmitWriteElement(w, dict.Value, ctx, accessor: "kv.Value");
                }
                w.WriteLine("w.WriteEndObject();");
                break;
            case TypeRef.Any:
                w.WriteLine("w.WriteNull(); // any-typed value not yet supported");
                break;
            default:
                w.WriteLine("w.WriteNull(); // unsupported type");
                break;
        }
        _ = p;
    }

    static void EmitWriteElement(SourceWriter w, TypeRef element, EmitContext ctx, string accessor = "item")
    {
        // For nullable elements (e.g. array of string? / dictionary<string, string?>) emit a null
        // branch so we don't pass a null straight to WriteString/WriteInt32 etc.
        if (element is TypeRef.Nullable)
        {
            using (w.Block($"if ({accessor} is null)"))
            {
                w.WriteLine("w.WriteNull();");
            }
            w.WriteLine("else");
            using (w.BraceBlock())
            {
                EmitWriteElementCore(w, Unwrap(element), ctx, accessor, unwrapValue: true);
            }
            return;
        }

        EmitWriteElementCore(w, Unwrap(element), ctx, accessor, unwrapValue: false);
    }

    static void EmitWriteElementCore(SourceWriter w, TypeRef inner, EmitContext ctx, string accessor, bool unwrapValue)
    {
        // When the original element was Nullable<T> for a value type (primitive, enum, or value-
        // object struct), callers need `.Value` to unwrap; for reference types we use the non-null
        // assertion since the null branch already returned.
        string read;
        if (unwrapValue)
        {
            read = ctx.IsValueTypeReference(inner) ? accessor + ".Value" : accessor + "!";
        }
        else
        {
            read = accessor;
        }

        switch (inner)
        {
            case TypeRef.Primitive prim:
                w.WriteLine(WritePrimitiveExpr(prim, read) + ";");
                break;
            case TypeRef.Named named when ctx.IsEnum(named.Name):
                w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, {read});");
                break;
            case TypeRef.Named named when ctx.IsStruct(named.Name):
                w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, in {read}, options);");
                break;
            case TypeRef.Named named:
                w.WriteLine($"{named.Name}Formatter.WriteValue(ref w, {read}, options);");
                break;
            default:
                w.WriteLine("w.WriteNull();");
                break;
        }
    }

    static string WritePrimitiveExpr(TypeRef.Primitive prim, string accessor) => prim.Kind switch
    {
        PrimitiveKind.String         => $"w.WriteString({accessor})",
        PrimitiveKind.SByte          => $"w.WriteSByte({accessor})",
        PrimitiveKind.Byte           => $"w.WriteByte({accessor})",
        PrimitiveKind.Int16          => $"w.WriteInt16({accessor})",
        PrimitiveKind.UInt16         => $"w.WriteUInt16({accessor})",
        PrimitiveKind.Int32          => $"w.WriteInt32({accessor})",
        PrimitiveKind.UInt32         => $"w.WriteUInt32({accessor})",
        PrimitiveKind.Int64          => $"w.WriteInt64({accessor})",
        PrimitiveKind.UInt64         => $"w.WriteUInt64({accessor})",
        PrimitiveKind.Single         => $"w.WriteSingle({accessor})",
        PrimitiveKind.Double         => $"w.WriteDouble({accessor})",
        PrimitiveKind.Boolean        => $"w.WriteBoolean({accessor})",
        PrimitiveKind.DateTimeOffset => $"w.WriteDateTimeOffset({accessor})",
        PrimitiveKind.DateOnly       => $"w.WriteDateOnly({accessor})",
        PrimitiveKind.TimeOnly       => $"w.WriteTimeOnly({accessor})",
        PrimitiveKind.TimeSpan       => $"w.WriteTimeSpan({accessor})",
        PrimitiveKind.Guid           => $"w.WriteGuid({accessor})",
        PrimitiveKind.Uri            => $"w.WriteUri({accessor})",
        PrimitiveKind.ByteArray      => $"w.WriteByteArray({accessor})",
        _ => "w.WriteNull()",
    };

    static TypeRef Unwrap(TypeRef t) => t is TypeRef.Nullable nu ? Unwrap(nu.Inner) : t;

    // ---------------------------------------------------------------------------------------------
    // Struct path: deserialize into locals then construct via primary ctor; serialize takes 'in T'.
    // ---------------------------------------------------------------------------------------------

    static void EmitStructDeserialize(SourceWriter w, ObjectTypeDescriptor type)
    {
        using (w.Block($"public static {type.Name} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
            w.WriteLine("tokenizer.ReadStartObject();");
            w.WriteLine("return ReadValue(ref tokenizer, options);");
        }
    }

    static void EmitStructReadValue(SourceWriter w, ObjectTypeDescriptor type, IReadOnlyList<PropertyDescriptor> properties, EmitContext ctx)
    {
        using (w.Block($"internal static {type.Name} ReadValue(ref Utf8JsonTokenizer tokenizer, NoJsonSerializerOptions options)"))
        {
            // Local variables for every property; populated in any JSON order.
            foreach (var p in properties)
            {
                var localType = TypeExpression.Render(p.Type);
                if (!p.IsRequired || p.IsNullable)
                {
                    if (!localType.EndsWith("?", StringComparison.Ordinal)) localType += "?";
                }
                w.WriteLine($"{localType} __v_{p.Name} = default;");
            }

            if (properties.Count == 0)
            {
                using (w.Block("while (tokenizer.TryReadPropertyName(out var __name))"))
                {
                    EmitUnknownPropertyBranch(w, type);
                }
            }
            else
            {
                var groups = properties
                    .GroupBy(p => Encoding.UTF8.GetByteCount(p.JsonName))
                    .OrderBy(g => g.Key)
                    .ToList();

                using (w.Block("while (tokenizer.TryReadPropertyName(out var __name))"))
                {
                    using (w.Block("switch (__name.Length)"))
                    {
                        foreach (var group in groups)
                        {
                            w.WriteLine($"case {group.Key}:");
                            w.Indent();
                            EmitStructLengthGroup(w, group, ctx, type);
                            w.WriteLine("break;");
                            w.Outdent();
                        }
                        w.WriteLine("default:");
                        w.Indent();
                        EmitUnknownPropertyBranch(w, type);
                        w.WriteLine("break;");
                        w.Outdent();
                    }
                }
            }

            // Construct via primary ctor. Parameter order = TypeEmitter's order
            // (required + non-null first, optionals last) — call by name so we don't depend on the order.
            var args = new List<string>(properties.Count);
            foreach (var p in properties)
            {
                args.Add($"{NameFactory.EscapeIfReserved(p.Name)}: __v_{p.Name}");
            }
            w.WriteLine($"return new {type.Name}({string.Join(", ", args)});");
        }
    }

    static void EmitStructLengthGroup(SourceWriter w, IGrouping<int, PropertyDescriptor> group, EmitContext ctx, ObjectTypeDescriptor type)
    {
        var first = true;
        foreach (var p in group)
        {
            var literal = EncodeUtf8Literal(EscapeJsonString(p.JsonName));
            w.WriteLine($"{(first ? "if" : "else if")} (__name.SequenceEqual({literal}))");
            using (w.BraceBlock())
            {
                EmitStructReadInto(w, p, ctx);
            }
            first = false;
        }
        w.WriteLine("else");
        using (w.BraceBlock())
        {
            EmitUnknownPropertyBranch(w, type);
        }
    }

    static void EmitStructReadInto(SourceWriter w, PropertyDescriptor p, EmitContext ctx)
    {
        if (p.IsNullable || !p.IsRequired)
        {
            using (w.Block("if (tokenizer.TryReadNull())"))
            {
                w.WriteLine($"__v_{p.Name} = default;");
            }
            w.WriteLine("else");
            using (w.BraceBlock())
            {
                EmitStructReadCore(w, p, ctx);
            }
        }
        else
        {
            EmitStructReadCore(w, p, ctx);
        }
    }

    static void EmitStructReadCore(SourceWriter w, PropertyDescriptor p, EmitContext ctx)
    {
        var inner = Unwrap(p.Type);
        switch (inner)
        {
            case TypeRef.Primitive prim:
                w.WriteLine($"__v_{p.Name} = {ReadPrimitiveExpr(prim)};");
                break;
            case TypeRef.Named named when ctx.IsEnum(named.Name):
                w.WriteLine($"__v_{p.Name} = {named.Name}Formatter.ReadValue(ref tokenizer);");
                break;
            case TypeRef.Named named when ctx.IsStruct(named.Name):
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"__v_{p.Name} = {named.Name}Formatter.ReadValue(ref tokenizer, options);");
                break;
            case TypeRef.Named named:
                w.WriteLine("tokenizer.ReadStartObject();");
                w.WriteLine($"var sub_{p.Name} = new {named.Name}();");
                w.WriteLine($"{named.Name}Formatter.ReadInto(ref tokenizer, sub_{p.Name}, options);");
                w.WriteLine($"__v_{p.Name} = sub_{p.Name};");
                break;
            case TypeRef.Array arr:
                {
                    var elementType = TypeExpression.Render(arr.Element);
                    w.WriteLine("tokenizer.ReadStartArray();");
                    w.WriteLine($"var list_{p.Name} = new global::System.Collections.Generic.List<{elementType}>();");
                    using (w.Block("while (!tokenizer.TryReadEndArray())"))
                    {
                        EmitReadElementInto(w, arr.Element, $"list_{p.Name}", ctx);
                    }
                    w.WriteLine($"__v_{p.Name} = list_{p.Name}.ToArray();");
                }
                break;
            case TypeRef.Any:
                w.WriteLine("tokenizer.SkipValue();");
                w.WriteLine($"__v_{p.Name} = null;");
                break;
            default:
                w.WriteLine($"tokenizer.SkipValue(); // unsupported type for {p.Name}");
                break;
        }
    }

    static void EmitStructSerialize(SourceWriter w, ObjectTypeDescriptor type)
    {
        using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {type.Name} value, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var w = new Utf8JsonBufferWriter(writer);");
            w.WriteLine("WriteValue(ref w, in value, options);");
            w.WriteLine("w.Flush();");
        }
    }

    static void EmitStructWriteValue(SourceWriter w, ObjectTypeDescriptor type, IReadOnlyList<PropertyDescriptor> properties, EmitContext ctx)
    {
        using (w.Block($"internal static void WriteValue(ref Utf8JsonBufferWriter w, in {type.Name} value, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("w.WriteStartObject();");
            foreach (var p in properties)
            {
                EmitWriteProperty(w, p, ctx);
            }
            w.WriteLine("w.WriteEndObject();");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // String helpers.
    // ---------------------------------------------------------------------------------------------

    static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders <paramref name="rawAscii"/> as a C# UTF-8 string literal (e.g. <c>"foo"u8</c>).
    /// All characters must already be safe (the caller is responsible for JSON-escaping).
    /// </summary>
    static string EncodeUtf8Literal(string rawAscii)
    {
        var sb = new StringBuilder(rawAscii.Length + 4);
        sb.Append('"');
        foreach (var c in rawAscii)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append("\"u8");
        return sb.ToString();
    }
}
