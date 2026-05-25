using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Emits the namespace-wide <c>{Ns}Serializer.g.cs</c>: shared options/exception types,
/// the internal UTF-8 tokenizer + writer template, and the generic
/// <c>Deserialize&lt;T&gt;</c>/<c>Serialize&lt;T&gt;</c> dispatch over every emitted type.
/// </summary>
/// <remarks>
/// Generic dispatch goes through a per-T <c>Cache&lt;T&gt;</c> static field. The
/// <c>typeof(T) ==</c> resolution chain runs exactly once per CLR generic instantiation
/// (lazy <c>cctor</c>); every subsequent call is a single field load + one interface call.
/// </remarks>
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
            EmitStreamGenerics(w);
            w.WriteLine();
            EmitThrowHelper(w);
        }

        return w.ToString();
    }

    /// <summary>
    /// Per-Serializer adapter registry. Stored as <c>object</c> because every entry has a different
    /// <c>INoJsonFormatter&lt;T&gt;</c> generic parameter; the dict key (<see cref="System.Type"/>)
    /// guarantees the value is the exact <c>INoJsonFormatter&lt;T&gt;</c> we want.
    /// </summary>
    static void EmitFormatterDictionary(SourceWriter w, TypeGraph graph)
    {
        var entries = EmittableTypeNames(graph).ToList();
        var capacity = entries.Count;
        // Inline the brace block manually so the closing brace gets the trailing semicolon.
        w.WriteLine($"static readonly global::System.Collections.Generic.Dictionary<global::System.Type, object> Formatters = new({capacity})");
        w.WriteLine("{");
        w.Indent();
        foreach (var name in entries)
        {
            w.WriteLine($"[typeof({name})] = {name}FormatterAdapter.Instance,");
        }
        w.Outdent();
        w.WriteLine("};");
    }

    /// <summary>
    /// Nested <c>Cache&lt;T&gt;</c> — does one dict lookup inside the static initialiser, then
    /// every dispatch site reads a single static field. <c>Unsafe.As</c> drops the runtime cast
    /// check because the dict key (<c>typeof(T)</c>) already proves the value matches.
    /// </summary>
    static void EmitCache(SourceWriter w)
    {
        using (w.Block("static class Cache<T>"))
        {
            w.WriteLine("public static readonly INoJsonFormatter<T>? Formatter =");
            w.WriteLine("    Formatters.TryGetValue(typeof(T), out var __v)");
            w.WriteLine("        ? global::System.Runtime.CompilerServices.Unsafe.As<INoJsonFormatter<T>>(__v)");
            w.WriteLine("        : null;");
        }
    }

    static void EmitDeserializeT(SourceWriter w)
    {
        using (w.Block("public static T Deserialize<T>(global::System.ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("return formatter!.Deserialize(utf8Json, options ?? NoJsonSerializerOptions.Default);");
        }

        w.WriteLine();
        w.WriteLine("public static T Deserialize<T>(byte[] utf8Json, NoJsonSerializerOptions? options = null) => Deserialize<T>((global::System.ReadOnlySpan<byte>)utf8Json, options);");
    }

    static void EmitSerializeT(SourceWriter w)
    {
        using (w.Block("public static void Serialize<T>(global::System.Buffers.IBufferWriter<byte> writer, T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("formatter!.Serialize(writer, in value, options ?? NoJsonSerializerOptions.Default);");
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
    /// Stream / async generic wrappers — uniform across every type the graph knows about because
    /// they all forward through <c>Cache&lt;T&gt;.Formatter</c>.
    /// </summary>
    static void EmitStreamGenerics(SourceWriter w)
    {
        using (w.Block("public static T Deserialize<T>(global::System.IO.Stream stream, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("return formatter!.Deserialize(stream, options ?? NoJsonSerializerOptions.Default);");
        }
        w.WriteLine();
        using (w.Block("public static global::System.Threading.Tasks.ValueTask<T> DeserializeAsync<T>(global::System.IO.Stream stream, NoJsonSerializerOptions? options = null, global::System.Threading.CancellationToken cancellationToken = default)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("return formatter!.DeserializeAsync(stream, options ?? NoJsonSerializerOptions.Default, cancellationToken);");
        }
        w.WriteLine();
        using (w.Block("public static void Serialize<T>(global::System.IO.Stream stream, T value, NoJsonSerializerOptions? options = null)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("formatter!.Serialize(stream, in value, options ?? NoJsonSerializerOptions.Default);");
        }
        w.WriteLine();
        using (w.Block("public static global::System.Threading.Tasks.ValueTask SerializeAsync<T>(global::System.IO.Stream stream, T value, NoJsonSerializerOptions? options = null, global::System.Threading.CancellationToken cancellationToken = default)"))
        {
            w.WriteLine("var formatter = Cache<T>.Formatter;");
            w.WriteLine("if (formatter is null) ThrowNotSupported<T>();");
            w.WriteLine("return formatter!.SerializeAsync(stream, value, options ?? NoJsonSerializerOptions.Default, cancellationToken);");
        }
    }

    /// <summary>
    /// Cold throw helper — keeps the hot dispatch paths short and JIT-inlineable.
    /// <c>[DoesNotReturn]</c> lets the compiler treat <c>formatter</c> as non-null after the guard.
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
