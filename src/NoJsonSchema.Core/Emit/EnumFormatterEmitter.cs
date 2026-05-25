using System.Text;
using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits <c>{Enum}Formatter.g.cs</c>: a per-enum static formatter with Deserialize / Serialize entry points
/// plus internal <c>ReadValue</c> / <c>WriteValue</c> used by parent object formatters.
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
        using (w.Block($"public static partial class {NameFactory.EscapeIfReserved(formatterName)}"))
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

        w.WriteLine();
        EmitAdapter(w, type.Name);
        return w.ToString();
    }

    /// <summary>
    /// Per-enum adapter — sealed singleton implementing <c>INoJsonFormatter&lt;T&gt;</c>, forwarding
    /// to the static enum Formatter. Plumbed through the namespace-wide <c>Cache&lt;T&gt;</c>.
    /// </summary>
    static void EmitAdapter(SourceWriter w, string typeName)
    {
        var adapter = typeName + "FormatterAdapter";
        var formatter = typeName + "Formatter";
        using (w.Block($"sealed class {NameFactory.EscapeIfReserved(adapter)} : INoJsonFormatter<{typeName}>"))
        {
            w.WriteLine($"public static readonly {adapter} Instance = new();");
            w.WriteLine($"{adapter}() {{ }}");
            w.WriteLine();
            w.WriteLine($"public {typeName} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options) => {formatter}.Deserialize(utf8Json, options);");
            w.WriteLine($"public void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {typeName} value, NoJsonSerializerOptions options) => {formatter}.Serialize(writer, value, options);");
            w.WriteLine($"public {typeName} Deserialize(global::System.IO.Stream stream, NoJsonSerializerOptions options) => {formatter}.Deserialize(stream, options);");
            w.WriteLine($"public void Serialize(global::System.IO.Stream stream, in {typeName} value, NoJsonSerializerOptions options) => {formatter}.Serialize(stream, value, options);");
            w.WriteLine($"public global::System.Threading.Tasks.ValueTask<{typeName}> DeserializeAsync(global::System.IO.Stream stream, NoJsonSerializerOptions options, global::System.Threading.CancellationToken cancellationToken) => {formatter}.DeserializeAsync(stream, options, cancellationToken);");
            w.WriteLine($"public global::System.Threading.Tasks.ValueTask SerializeAsync(global::System.IO.Stream stream, {typeName} value, NoJsonSerializerOptions options, global::System.Threading.CancellationToken cancellationToken) => {formatter}.SerializeAsync(stream, value, options, cancellationToken);");
        }
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
        using (w.Block($"public static {type.Name} Deserialize(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
            w.WriteLine("return ReadValue(ref tokenizer);");
        }
        w.WriteLine();
        w.WriteLine($"public static {type.Name} Deserialize(byte[] utf8Json, NoJsonSerializerOptions? options = null) => Deserialize((global::System.ReadOnlySpan<byte>)utf8Json, options);");
        w.WriteLine();
        using (w.Block($"public static {type.Name} Deserialize(global::System.IO.Stream stream, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("return Deserialize(NoJsonStreamUtility.ReadAllBytes(stream), options);");
        }
        w.WriteLine();
        using (w.Block($"public static async global::System.Threading.Tasks.ValueTask<{type.Name}> DeserializeAsync(global::System.IO.Stream stream, NoJsonSerializerOptions? options = null, global::System.Threading.CancellationToken cancellationToken = default)"))
        {
            w.WriteLine("var __bytes = await NoJsonStreamUtility.ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);");
            w.WriteLine("return Deserialize(__bytes, options);");
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
        using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, {type.Name} value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var w = new Utf8JsonBufferWriter(writer);");
            w.WriteLine("WriteValue(ref w, value);");
            w.WriteLine("w.Flush();");
        }
        w.WriteLine();
        using (w.Block($"public static byte[] SerializeToUtf8Bytes({type.Name} value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(16);");
            w.WriteLine("Serialize(buffer, value, options);");
            w.WriteLine("return buffer.WrittenSpan.ToArray();");
        }
        w.WriteLine();
        using (w.Block($"public static void Serialize(global::System.IO.Stream stream, {type.Name} value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var __buffer = new global::System.Buffers.ArrayBufferWriter<byte>(16);");
            w.WriteLine("Serialize(__buffer, value, options);");
            w.WriteLine("stream.Write(__buffer.WrittenSpan);");
        }
        w.WriteLine();
        using (w.Block($"public static async global::System.Threading.Tasks.ValueTask SerializeAsync(global::System.IO.Stream stream, {type.Name} value, NoJsonSerializerOptions? options = null, global::System.Threading.CancellationToken cancellationToken = default)"))
        {
            w.WriteLine("var __buffer = new global::System.Buffers.ArrayBufferWriter<byte>(16);");
            w.WriteLine("Serialize(__buffer, value, options);");
            w.WriteLine("await stream.WriteAsync(__buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);");
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
