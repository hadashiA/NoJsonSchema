using System.Net.Http;
using System.Text.Json;
using NoJsonSchema.Core;
using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.Cli;

/// <summary>
/// CLI surface. Each public method becomes a subcommand; ConsoleAppFramework's source generator
/// reads parameter names + XML doc comments to wire up flags, short aliases, and help text.
/// Multi-value flags (<c>--value-object</c>, <c>--include-type</c>) take comma-separated values.
/// </summary>
/// <remarks>
/// <c>--input</c> accepts either a local file path or an http(s) URL — the latter is fetched
/// over the network and parsed in-memory, no caching.
/// </remarks>
class NoJsonSchemaCommands
{
    /// <summary>Generate C# code from a JSON Schema.</summary>
    /// <param name="input">-i, Path or http(s) URL of the JSON Schema.</param>
    /// <param name="output">-o, Directory to write generated .cs files into.</param>
    /// <param name="namespace">-n, Namespace for generated types.</param>
    /// <param name="rootType">Optional root type name override.</param>
    /// <param name="typeStyle">Type style for objects (Class | Record | ReadonlyRecordStruct).</param>
    /// <param name="allofStrategy">How to represent allOf composition (Inherit | Flatten).</param>
    /// <param name="strictExtra">Treat unknown JSON properties as errors.</param>
    /// <param name="valueObject">Comma-separated list of $defs entries to emit as readonly record struct (primary-ctor) value objects.</param>
    /// <param name="useRequired">Emit the C# 11 'required' modifier on non-nullable required properties (otherwise '= null!' is used to suppress CS8618).</param>
    /// <param name="includeType">Comma-separated whitelist of $defs / components.schemas entries to generate (transitive deps included automatically). Default is everything.</param>
    /// <param name="cancellationToken">Injected by ConsoleAppFramework (Ctrl+C).</param>
    public async Task<int> Generate(
        string input,
        string output,
        string @namespace = "Generated",
        string? rootType = null,
        TypeStyle typeStyle = TypeStyle.Class,
        AllOfStrategy allofStrategy = AllOfStrategy.Inherit,
        bool strictExtra = false,
        string[]? valueObject = null,
        bool useRequired = false,
        string[]? includeType = null,
        CancellationToken cancellationToken = default)
    {
        var options = new GenerationOptions
        {
            Namespace = @namespace,
            RootTypeName = rootType,
            TypeStyle = typeStyle,
            AllOfStrategy = allofStrategy,
            StrictExtraProperties = strictExtra,
            ValueObjectTypes = new HashSet<string>(valueObject ?? [], StringComparer.Ordinal),
            UseRequiredModifier = useRequired,
            IncludedTypes = new HashSet<string>(includeType ?? [], StringComparer.Ordinal),
        };

        string schemaJson;
        try
        {
            schemaJson = await SchemaSource.ReadAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine($"error: failed to read schema from '{input}': {ex.Message}");
            return 1;
        }

        var pipeline = new GeneratorPipeline();
        GenerationResult result;
        try
        {
            result = pipeline.Generate(schemaJson, options);
        }
        catch (Exception ex) when (ex is SchemaLoadException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(output);
        foreach (var file in result.Files)
        {
            var path = Path.Combine(output, file.FileName);
            // FileName may contain a relative subdir (e.g. "Formatters/UserFormatter.g.cs").
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, file.SourceText, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"wrote {path}");
        }

        Console.WriteLine($"generated {result.Files.Count} file(s).");
        return 0;
    }

    /// <summary>Parse a schema and report structural problems without generating code.</summary>
    /// <param name="input">-i, Path or http(s) URL of the JSON Schema.</param>
    /// <param name="cancellationToken">Injected by ConsoleAppFramework (Ctrl+C).</param>
    public async Task<int> Lint(string input, CancellationToken cancellationToken = default)
    {
        string schemaJson;
        try
        {
            schemaJson = await SchemaSource.ReadAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine($"error: failed to read schema from '{input}': {ex.Message}");
            return 1;
        }

        JsonSchemaDocument doc;
        try
        {
            doc = JsonSchemaLoader.Load(schemaJson, input);
        }
        catch (Exception ex) when (ex is SchemaLoadException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"file:        {doc.SourcePath}");
        Console.WriteLine($"$schema:     {doc.Dialect ?? "(none)"}");
        Console.WriteLine($"$id:         {doc.Id ?? "(none)"}");
        Console.WriteLine($"root kind:   {doc.Root.Kind}");
        Console.WriteLine($"definitions: {doc.Root.Defs.Count}");
        Console.WriteLine($"properties:  {doc.Root.Properties.Count} (on root)");
        return 0;
    }
}
