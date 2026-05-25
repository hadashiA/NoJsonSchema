using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits the namespace-wide <c>{Ns}Serializer.g.cs</c>: shared options/exception types,
/// the internal UTF-8 tokenizer + writer template, and the generic
/// <c>Deserialize&lt;T&gt;</c>/<c>Serialize&lt;T&gt;</c> dispatch over every emitted type.
/// </summary>
public static class SerializerEmitter
{
    public static string Emit(string serializerTypeName, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine("using System;");
        w.WriteLine("using System.Buffers;");
        w.WriteLine("using System.IO;");
        w.WriteLine();
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        w.WriteLine(SerializerTemplate.SharedDefinitions);
        w.WriteLine();

        using (w.Block($"public static class {NameFactory.EscapeIfReserved(serializerTypeName)}"))
        {
            EmitDeserializeT(w, graph);
            w.WriteLine();
            EmitSerializeT(w, graph);
            w.WriteLine();
            EmitSerializeToUtf8Bytes(w, graph);
        }

        return w.ToString();
    }

    static void EmitDeserializeT(SourceWriter w, TypeGraph graph)
    {
        using (w.Block("public static T Deserialize<T>(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("options ??= NoJsonSerializerOptions.Default;");
            foreach (var kv in graph.Types)
            {
                if (kv.Value is ObjectTypeDescriptor { Style: TypeStyle.ReadonlyRecordStruct })
                {
                    // Unsafe.As avoids boxing the struct when going through the generic entry point.
                    using (w.Block($"if (typeof(T) == typeof({kv.Key}))"))
                    {
                        w.WriteLine($"var v = {kv.Key}Formatter.Deserialize(utf8Json, options);");
                        w.WriteLine($"return global::System.Runtime.CompilerServices.Unsafe.As<{kv.Key}, T>(ref v);");
                    }
                }
                else if (kv.Value is ObjectTypeDescriptor or EnumTypeDescriptor)
                {
                    w.WriteLine($"if (typeof(T) == typeof({kv.Key})) return (T)(object){kv.Key}Formatter.Deserialize(utf8Json, options)!;");
                }
            }
            w.WriteLine("throw new global::System.NotSupportedException(\"No formatter generated for \" + typeof(T).FullName);");
        }

        w.WriteLine();
        w.WriteLine("public static T Deserialize<T>(byte[] utf8Json, NoJsonSerializerOptions? options = null) => Deserialize<T>((global::System.ReadOnlySpan<byte>)utf8Json, options);");
    }

    static void EmitSerializeT(SourceWriter w, TypeGraph graph)
    {
        using (w.Block("public static void Serialize<T>(global::System.Buffers.IBufferWriter<byte> writer, T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("options ??= NoJsonSerializerOptions.Default;");
            foreach (var kv in graph.Types)
            {
                if (kv.Value is ObjectTypeDescriptor { Style: TypeStyle.ReadonlyRecordStruct })
                {
                    // Reinterpret the generic parameter as the concrete struct ref so we can pass it 'in'.
                    using (w.Block($"if (typeof(T) == typeof({kv.Key}))"))
                    {
                        w.WriteLine($"{kv.Key}Formatter.Serialize(writer, in global::System.Runtime.CompilerServices.Unsafe.As<T, {kv.Key}>(ref value), options);");
                        w.WriteLine("return;");
                    }
                }
                else if (kv.Value is ObjectTypeDescriptor)
                {
                    w.WriteLine($"if (typeof(T) == typeof({kv.Key})) {{ {kv.Key}Formatter.Serialize(writer, (({kv.Key})(object)value!), options); return; }}");
                }
                else if (kv.Value is EnumTypeDescriptor)
                {
                    w.WriteLine($"if (typeof(T) == typeof({kv.Key})) {{ {kv.Key}Formatter.Serialize(writer, ({kv.Key})(object)value!, options); return; }}");
                }
            }
            w.WriteLine("throw new global::System.NotSupportedException(\"No formatter generated for \" + typeof(T).FullName);");
        }
    }

    static void EmitSerializeToUtf8Bytes(SourceWriter w, TypeGraph graph)
    {
        _ = graph;
        using (w.Block("public static byte[] SerializeToUtf8Bytes<T>(T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(256);");
            w.WriteLine("Serialize(buffer, value, options);");
            w.WriteLine("return buffer.WrittenSpan.ToArray();");
        }
    }

    static IEnumerable<string> EmittableTypeNames(TypeGraph graph)
    {
        foreach (var kv in graph.Types)
        {
            if (kv.Value is ObjectTypeDescriptor or EnumTypeDescriptor) yield return kv.Key;
        }
    }
}
