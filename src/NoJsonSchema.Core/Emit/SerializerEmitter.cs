using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits the namespace-wide <c>{Ns}Serializer.g.cs</c>: shared options/exception types,
/// the internal UTF-8 tokenizer + writer template, and the generic
/// <c>Deserialize&lt;T&gt;</c>/<c>Serialize&lt;T&gt;</c> dispatch over every emitted type.
/// </summary>
/// <remarks>
/// Generic dispatch goes through a per-T <c>Cache&lt;T&gt;</c> static field holding two delegates
/// (one for Deserialize, one for Serialize). The <c>typeof(T) ==</c> resolution chain runs exactly
/// once per CLR generic instantiation (lazy <c>cctor</c>); every subsequent call is a single
/// static-field load + one delegate invocation.
/// </remarks>
public static class SerializerEmitter
{
    public static string Emit(string serializerTypeName, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine("using System;");
        w.WriteLine("using System.Buffers;");
        w.WriteLine();
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        w.WriteLine(SerializerTemplate.SharedDefinitions);
        w.WriteLine();

        using (w.Block($"public static class {NameFactory.EscapeIfReserved(serializerTypeName)}"))
        {
            EmitDelegates(w);
            w.WriteLine();
            EmitFormatterDictionary(w, graph);
            w.WriteLine();
            EmitCache(w);
            w.WriteLine();
            EmitDeserializeT(w);
            w.WriteLine();
            EmitSerializeT(w);
            w.WriteLine();
            EmitSerializeToUtf8Bytes(w);
            w.WriteLine();
            EmitThrowHelper(w);
        }

        return w.ToString();
    }

    /// <summary>
    /// Per-Serializer delegate types. Nested (private) so they don't conflict across multiple
    /// generated namespaces in the same assembly — each <c>{Ns}Serializer</c> declares its own
    /// pair and they're never visible to consumers.
    /// </summary>
    static void EmitDelegates(SourceWriter w)
    {
        w.WriteLine("delegate T DeserializeDelegate<T>(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options);");
        w.WriteLine("delegate void SerializeDelegate<T>(global::System.Buffers.IBufferWriter<byte> writer, in T value, NoJsonSerializerOptions options);");
    }

    /// <summary>
    /// Static registry of <c>(typeof(T)) → (Deserialize, Serialize)</c> delegate pairs.
    /// One dictionary lookup at <c>Cache&lt;T&gt;.cctor</c> time gets both delegates; the runtime
    /// cast is a tag-less <c>Unsafe.As</c> because the dict key proves the delegate matches T.
    /// </summary>
    static void EmitFormatterDictionary(SourceWriter w, TypeGraph graph)
    {
        var entries = EmittableTypeNames(graph).ToList();
        var capacity = entries.Count;
        w.WriteLine($"static readonly global::System.Collections.Generic.Dictionary<global::System.Type, (object Deserialize, object Serialize)> Formatters = new({capacity})");
        w.WriteLine("{");
        w.Indent();
        foreach (var name in entries)
        {
            w.WriteLine($"[typeof({name})] = ((DeserializeDelegate<{name}>){name}Formatter.Deserialize, (SerializeDelegate<{name}>){name}Formatter.Serialize),");
        }
        w.Outdent();
        w.WriteLine("};");
    }

    /// <summary>
    /// Nested <c>Cache&lt;T&gt;</c> — one static-readonly field per CLR generic instantiation.
    /// The dict lookup runs inside <c>cctor</c>; every subsequent dispatch reads
    /// <c>Cache&lt;T&gt;.Deserialize</c> / <c>Cache&lt;T&gt;.Serialize</c> directly.
    /// </summary>
    static void EmitCache(SourceWriter w)
    {
        using (w.Block("static class Cache<T>"))
        {
            w.WriteLine("public static readonly DeserializeDelegate<T>? Deserialize;");
            w.WriteLine("public static readonly SerializeDelegate<T>? Serialize;");
            w.WriteLine();
            using (w.Block("static Cache()"))
            {
                using (w.Block("if (Formatters.TryGetValue(typeof(T), out var __entry))"))
                {
                    w.WriteLine("Deserialize = global::System.Runtime.CompilerServices.Unsafe.As<DeserializeDelegate<T>>(__entry.Deserialize);");
                    w.WriteLine("Serialize = global::System.Runtime.CompilerServices.Unsafe.As<SerializeDelegate<T>>(__entry.Serialize);");
                }
            }
        }
    }

    static void EmitDeserializeT(SourceWriter w)
    {
        using (w.Block("public static T Deserialize<T>(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var del = Cache<T>.Deserialize;");
            w.WriteLine("if (del is null) ThrowNotSupported<T>();");
            w.WriteLine("return del!(utf8Json, options ?? NoJsonSerializerOptions.Default);");
        }

        w.WriteLine();
        w.WriteLine("public static T Deserialize<T>(byte[] utf8Json, NoJsonSerializerOptions? options = null) => Deserialize<T>((global::System.ReadOnlySpan<byte>)utf8Json, options);");
    }

    static void EmitSerializeT(SourceWriter w)
    {
        using (w.Block("public static void Serialize<T>(global::System.Buffers.IBufferWriter<byte> writer, T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var del = Cache<T>.Serialize;");
            w.WriteLine("if (del is null) ThrowNotSupported<T>();");
            w.WriteLine("del!(writer, value, options ?? NoJsonSerializerOptions.Default);");
        }
    }

    static void EmitSerializeToUtf8Bytes(SourceWriter w)
    {
        using (w.Block("public static byte[] SerializeToUtf8Bytes<T>(T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(256);");
            w.WriteLine("Serialize<T>(buffer, value, options);");
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

    /// <summary>
    /// Cold throw helper — keeps the hot dispatch paths short and JIT-inlineable.
    /// <c>[DoesNotReturn]</c> lets the compiler treat <c>del</c> as non-null after the guard.
    /// </summary>
    static void EmitThrowHelper(SourceWriter w)
    {
        w.WriteLine("[global::System.Diagnostics.CodeAnalysis.DoesNotReturn]");
        w.WriteLine("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        using (w.Block("static void ThrowNotSupported<T>()"))
        {
            w.WriteLine("throw new global::System.NotSupportedException(\"No formatter generated for \" + typeof(T).FullName);");
        }
    }
}
