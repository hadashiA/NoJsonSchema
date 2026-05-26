using System.Text;
using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits <c>{Enum}Formatter.g.cs</c>: a static-method bag with Deserialize / Serialize
/// entry points plus internal <c>ReadValue</c> / <c>WriteValue</c> used by parent object
/// formatters.
/// </summary>
public static class EnumFormatterEmitter
{
    public static string Emit(EnumTypeDescriptor type, GenerationOptions options)
    {
        var w = new SourceWriter();
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine("using System;");
        w.WriteLine("using System.Buffers;");
        w.WriteLine();
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        var formatterName = type.Name + "Formatter";
        var escaped = NameFactory.EscapeIfReserved(formatterName);
        using (w.Block($"static partial class {escaped}"))
        {
            EmitMemberLiterals(w, type);
            w.WriteLine();
            EmitDeserialize(w, type);
            w.WriteLine();
            EmitReadValue(w, type);
            w.WriteLine();
            EmitSerialize(w, type);
            w.WriteLine();
            EmitWriteValue(w, type);
        }

        return w.ToString();
    }

    static void EmitMemberLiterals(SourceWriter w, EnumTypeDescriptor type)
    {
        foreach (var m in type.Members)
        {
            w.WriteLine($"static global::System.ReadOnlySpan<byte> Member_{m.Name} => {EncodeUtf8Literal(m.JsonValue)};");
        }
    }

    static void EmitDeserialize(SourceWriter w, EnumTypeDescriptor type)
    {
        using (w.Block($"public static {type.Name} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
            w.WriteLine("return ReadValue(ref tokenizer);");
        }
    }

    static void EmitReadValue(SourceWriter w, EnumTypeDescriptor type)
    {
        using (w.Block($"internal static {type.Name} ReadValue(ref Utf8JsonTokenizer tokenizer)"))
        {
            // Fast path: raw UTF-8 byte comparison. Falls back to a string lookup when the value contains escapes.
            using (w.Block("if (tokenizer.TryReadStringRaw(out var __raw))"))
            {
                foreach (var m in type.Members)
                {
                    w.WriteLine($"if (__raw.SequenceEqual(Member_{m.Name})) return {type.Name}.{NameFactory.EscapeIfReserved(m.Name)};");
                }
                w.WriteLine($"tokenizer.ThrowFormatException(\"Unknown enum value for {type.Name}: '\" + global::System.Text.Encoding.UTF8.GetString(__raw) + \"'\");");
                w.WriteLine("return default; // unreachable");
            }
            // Escape-bearing fallback. Should be rare; keeps correctness if the value used a \u escape.
            w.WriteLine($"tokenizer.ThrowFormatException(\"Unknown enum value for {type.Name} (escaped)\");");
            w.WriteLine("return default; // unreachable");
        }
    }

    static void EmitSerialize(SourceWriter w, EnumTypeDescriptor type)
    {
        using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {type.Name} value, NoJsonSerializerOptions options)"))
        {
            w.WriteLine("var w = new Utf8JsonBufferWriter(writer);");
            w.WriteLine("WriteValue(ref w, value);");
            w.WriteLine("w.Flush();");
        }
    }

    static void EmitWriteValue(SourceWriter w, EnumTypeDescriptor type)
    {
        using (w.Block($"internal static void WriteValue(ref Utf8JsonBufferWriter w, {type.Name} value)"))
        {
            using (w.Block("switch (value)"))
            {
                foreach (var m in type.Members)
                {
                    w.WriteLine($"case {type.Name}.{NameFactory.EscapeIfReserved(m.Name)}: w.WriteRawStringValue(Member_{m.Name}); break;");
                }
                w.WriteLine($"default: throw new global::System.InvalidOperationException(\"Unknown {type.Name} value: \" + value);");
            }
        }
    }

    static string EncodeUtf8Literal(string raw)
    {
        var sb = new StringBuilder(raw.Length + 4);
        sb.Append('"');
        foreach (var c in raw)
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
