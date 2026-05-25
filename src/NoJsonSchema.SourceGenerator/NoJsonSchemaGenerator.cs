using System;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NoJsonSchema.Core;
using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.SourceGenerator;

/// <summary>
/// Incremental source generator that turns JSON Schema files (declared as <c>AdditionalFiles</c>)
/// into C# types + UTF-8 parser/emitter at compile time.
/// </summary>
/// <remarks>
/// Pinned to Microsoft.CodeAnalysis 4.3.x for Unity 2022.3 / 2023.x compatibility — avoid using
/// APIs introduced after that release.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class NoJsonSchemaGenerator : IIncrementalGenerator
{
    const string NamespaceMetadata = "NoJsonSchemaNamespace";
    const string ValueObjectsMetadata = "NoJsonSchemaValueObjects";
    const string TypeStyleMetadata = "NoJsonSchemaTypeStyle";
    const string StrictExtraMetadata = "NoJsonSchemaStrictExtraProperties";
    const string UseRequiredMetadata = "NoJsonSchemaUseRequired";
    const string IncludeTypesMetadata = "NoJsonSchemaIncludeTypes";

    static readonly DiagnosticDescriptor SchemaLoadError = new(
        id: "NJS001",
        title: "Schema load failed",
        messageFormat: "Failed to load JSON Schema '{0}': {1}",
        category: "NoJsonSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor GenerationError = new(
        id: "NJS002",
        title: "Code generation failed",
        messageFormat: "Code generation failed for '{0}': {1}",
        category: "NoJsonSchema",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline:
        //   * Pick up every additional file whose path ends with .json.
        //   * Pair it with the per-file MSBuild metadata exposed via .props.
        //   * Pair that with global options (defaults applied when per-file metadata is missing).
        //   * Emit one set of source files per input schema.
        var additionalFiles = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        var perFile = additionalFiles
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) =>
            {
                var file = pair.Left;
                var optionsProvider = pair.Right;
                var fileOptions = optionsProvider.GetOptions(file);
                var globals = optionsProvider.GlobalOptions;

                return new SchemaInput(
                    Path: file.Path,
                    Source: file.GetText(default)?.ToString() ?? string.Empty,
                    Namespace: GetMetadata(fileOptions, NamespaceMetadata)
                               ?? GetGlobal(globals, NamespaceMetadata)
                               ?? "Generated",
                    ValueObjects: ParseList(GetMetadata(fileOptions, ValueObjectsMetadata)
                                            ?? GetGlobal(globals, ValueObjectsMetadata)),
                    TypeStyle: GetMetadata(fileOptions, TypeStyleMetadata)
                               ?? GetGlobal(globals, TypeStyleMetadata)
                               ?? "Class",
                    StrictExtra: bool.TryParse(
                        GetMetadata(fileOptions, StrictExtraMetadata) ?? GetGlobal(globals, StrictExtraMetadata),
                        out var v) && v,
                    UseRequired: bool.TryParse(
                        GetMetadata(fileOptions, UseRequiredMetadata) ?? GetGlobal(globals, UseRequiredMetadata),
                        out var rq) && rq,
                    IncludeTypes: ParseList(GetMetadata(fileOptions, IncludeTypesMetadata)
                                            ?? GetGlobal(globals, IncludeTypesMetadata)));
            });

        context.RegisterSourceOutput(perFile, Emit);
    }

    static void Emit(SourceProductionContext spc, SchemaInput input)
    {
        try
        {
            var options = new GenerationOptions
            {
                Namespace = input.Namespace,
                StrictExtraProperties = input.StrictExtra,
                TypeStyle = ParseStyle(input.TypeStyle),
                ValueObjectTypes = new System.Collections.Generic.HashSet<string>(input.ValueObjects, StringComparer.Ordinal),
                UseRequiredModifier = input.UseRequired,
                IncludedTypes = new System.Collections.Generic.HashSet<string>(input.IncludeTypes, StringComparer.Ordinal),
            };

            GenerationResult result;
            try
            {
                result = new GeneratorPipeline().Generate(input.Source, options);
            }
            catch (SchemaLoadException ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(SchemaLoadError, Location.None, input.Path, ex.Message));
                return;
            }

            foreach (var f in result.Files)
            {
                // Roslyn hint names mustn't contain path separators; flatten the subdir into a `.` style identifier.
                var hint = f.FileName.Replace('/', '.').Replace('\\', '.');
                spc.AddSource(hint, SourceText.From(f.SourceText, Encoding.UTF8));
            }
        }
        catch (Exception ex)
        {
            spc.ReportDiagnostic(Diagnostic.Create(GenerationError, Location.None, input.Path, ex.Message));
        }
    }

    static TypeStyle ParseStyle(string raw) => raw switch
    {
        "Record" => TypeStyle.Record,
        "ReadonlyRecordStruct" => TypeStyle.ReadonlyRecordStruct,
        _ => TypeStyle.Class,
    };

    static string? GetMetadata(AnalyzerConfigOptions options, string name) =>
        options.TryGetValue("build_metadata.AdditionalFiles." + name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    static string? GetGlobal(AnalyzerConfigOptions options, string name) =>
        options.TryGetValue("build_property." + name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    static ImmutableArray<string> ParseList(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return ImmutableArray<string>.Empty;
        var parts = raw!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<string>(parts.Length);
        foreach (var p in parts)
        {
            var trimmed = p.Trim();
            if (trimmed.Length > 0) builder.Add(trimmed);
        }
        return builder.ToImmutable();
    }

    readonly record struct SchemaInput(
        string Path,
        string Source,
        string Namespace,
        ImmutableArray<string> ValueObjects,
        string TypeStyle,
        bool StrictExtra,
        bool UseRequired,
        ImmutableArray<string> IncludeTypes);
}
