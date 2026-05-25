using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NoJsonSchema.SourceGenerator;

namespace NoJsonSchema.SourceGenerator.Tests;

/// <summary>
/// Helpers that wire up <see cref="NoJsonSchemaGenerator"/> against an in-memory
/// CSharpCompilation so the test can inspect both generated source and runtime behaviour.
/// </summary>
static class GeneratorHarness
{
    sealed class FakeAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(text, Encoding.UTF8);
    }

    sealed class FakeOptions(
        IReadOnlyDictionary<string, string> globals,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> perFile)
        : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new Map(globals);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Map(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            perFile.TryGetValue(textFile.Path, out var fileOpts) ? new Map(fileOpts) : new Map(new Dictionary<string, string>());

        sealed class Map(IReadOnlyDictionary<string, string> map) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (map.TryGetValue(key, out var v)) { value = v; return true; }
                value = null!;
                return false;
            }
        }
    }

    /// <summary>
    /// Run the generator over a single schema with the given per-file metadata. Returns the
    /// compiled assembly (so tests can reflect over generated types) plus diagnostics.
    /// </summary>
    public static (Assembly? Assembly, ImmutableArray<Diagnostic> Diagnostics, IReadOnlyList<SyntaxTree> GeneratedTrees)
        Run(string schemaJson, Dictionary<string, string>? perFileMetadata = null, Dictionary<string, string>? globalMetadata = null)
    {
        var schemaPath = "/virtual/schema.json";
        var additional = new FakeAdditionalText(schemaPath, schemaJson);

        var perFile = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (perFileMetadata is not null)
        {
            var prefixed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in perFileMetadata)
            {
                prefixed["build_metadata.AdditionalFiles." + kv.Key] = kv.Value;
            }
            perFile[schemaPath] = prefixed;
        }

        var globals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (globalMetadata is not null)
        {
            foreach (var kv in globalMetadata)
            {
                globals["build_property." + kv.Key] = kv.Value;
            }
        }

        var optionsProvider = new FakeOptions(globals, perFile);

        // Minimal compilation so the generator has something to attach to.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var inputCompilation = CSharpCompilation.Create(
            "GeneratorTestCompilation_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: [],
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new NoJsonSchemaGenerator())
            .AddAdditionalTexts([additional])
            .WithUpdatedAnalyzerConfigOptions(optionsProvider);

        var ranDriver = driver.RunGeneratorsAndUpdateCompilation(inputCompilation, out var outputCompilation, out var diagnostics);
        var runResult = ranDriver.GetRunResult();
        var generatedTrees = runResult.GeneratedTrees;

        if (diagnostics.Length > 0 && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, diagnostics, generatedTrees);
        }

        using var ms = new MemoryStream();
        var emit = outputCompilation.Emit(ms);
        if (!emit.Success)
        {
            return (null, emit.Diagnostics, generatedTrees);
        }
        return (Assembly.Load(ms.ToArray()), emit.Diagnostics, generatedTrees);
    }
}
