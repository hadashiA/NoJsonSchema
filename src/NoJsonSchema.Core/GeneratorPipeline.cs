using NoJsonSchema.Core.Emit;
using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Resolver;
using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.Core;

public sealed record GeneratedFile(string FileName, string SourceText);

public sealed record GenerationResult(IReadOnlyList<GeneratedFile> Files);

/// <summary>
/// End-to-end driver: JSON Schema text → C# source files.
/// </summary>
public sealed class GeneratorPipeline
{
    public GenerationResult Generate(string schemaJson, GenerationOptions options)
    {
        var doc = JsonSchemaLoader.Load(schemaJson);
        var serializerName = ResolveSerializerName(options);
        var graph = new TypeGraphBuilder().Build(
            doc,
            reservedNames: [serializerName],
            valueObjectTypeNames: options.ValueObjectTypes,
            includedTypeNames: options.IncludedTypes);

        var files = new List<GeneratedFile>(graph.Types.Count * 2 + 2)
        {
            new($"{serializerName}.g.cs", SerializerEmitter.Emit(serializerName, graph, options)),
        };
        if (options.UseRequiredModifier)
        {
            // Polyfill so generated code works on pre-net7 / netstandard2.0 consumers.
            files.Add(new("_SetsRequiredMembersShim.g.cs", Emit.SerializerTemplate.SetsRequiredMembersShim));
        }

        foreach (var kv in graph.Types)
        {
            switch (kv.Value)
            {
                case ObjectTypeDescriptor obj:
                    files.Add(new($"{obj.Name}.g.cs", TypeEmitter.Emit(obj, graph, options)));
                    files.Add(new($"Formatters/{obj.Name}Formatter.g.cs", FormatterEmitter.Emit(obj, graph, options)));
                    break;
                case EnumTypeDescriptor enm:
                    files.Add(new($"{enm.Name}.g.cs", EnumTypeEmitter.Emit(enm, options)));
                    files.Add(new($"Formatters/{enm.Name}Formatter.g.cs", EnumFormatterEmitter.Emit(enm, options)));
                    break;
            }
        }

        return new GenerationResult(files);
    }

    static string ResolveSerializerName(GenerationOptions options)
    {
        if (!string.IsNullOrEmpty(options.SerializerName)) return options.SerializerName!;
        var ns = options.Namespace;
        var idx = ns.LastIndexOf('.');
        var leaf = idx < 0 ? ns : ns.Substring(idx + 1);
        return leaf + "Serializer";
    }
}
