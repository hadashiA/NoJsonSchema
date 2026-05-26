using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.SourceGenerator.Tests;

public class SourceGeneratorTests(ITestOutputHelper output)
{
    [Fact]
    public void Generator_PicksUpAdditionalJsonFile_AndProducesCompilableTypes()
    {
        const string schema = """
        {
          "$defs": {
            "Person": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age":  { "type": "integer", "format": "int32" }
              },
              "required": ["name", "age"]
            }
          }
        }
        """;
        var meta = new Dictionary<string, string>
        {
            ["NoJsonSchemaNamespace"] = "MyApp",
        };
        var (asm, diagnostics, trees) = GeneratorHarness.Run(schema, perFileMetadata: meta);

        foreach (var d in diagnostics) output.WriteLine(d.ToString());
        foreach (var t in trees) output.WriteLine($"--- {t.FilePath} ---\n{t.ToString()}");

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(asm);

        var personType = asm!.GetType("MyApp.Person")!;
        Assert.NotNull(personType);

        var instance = Activator.CreateInstance(personType)!;
        personType.GetProperty("Name")!.SetValue(instance, "Ada");
        personType.GetProperty("Age")!.SetValue(instance, 36);

        // Serializer is the public dispatch surface; Formatter is internal-by-design.
        var serializerType = asm.GetType("MyApp.MyAppSerializer")!;
        var serialize = serializerType.GetMethods()
            .First(m => m.Name == "SerializeToUtf8Bytes" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(personType);
        var bytes = (byte[])serialize.Invoke(null, [instance, null])!;
        Assert.Equal("{\"name\":\"Ada\",\"age\":36}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Generator_UsesGlobalNamespaceFallback()
    {
        const string schema = """
        {
          "$defs": {
            "X": { "type": "object", "properties": { "id": { "type": "integer", "format": "int32" } }, "required": ["id"] }
          }
        }
        """;
        var globals = new Dictionary<string, string>
        {
            ["NoJsonSchemaNamespace"] = "GlobalNs",
        };
        var (asm, diagnostics, _) = GeneratorHarness.Run(schema, globalMetadata: globals);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(asm);
        Assert.NotNull(asm!.GetType("GlobalNs.X"));
    }

    [Fact]
    public void Generator_ValueObjects_RecognisedFromMetadata()
    {
        const string schema = """
        {
          "$defs": {
            "Color": {
              "type": "object",
              "properties": {
                "r": { "type": "integer", "format": "int32" },
                "g": { "type": "integer", "format": "int32" },
                "b": { "type": "integer", "format": "int32" }
              },
              "required": ["r", "g", "b"]
            }
          }
        }
        """;
        var meta = new Dictionary<string, string>
        {
            ["NoJsonSchemaNamespace"] = "GfxApp",
            ["NoJsonSchemaValueObjects"] = "Color",
        };
        var (asm, diagnostics, _) = GeneratorHarness.Run(schema, perFileMetadata: meta);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(asm);
        var color = asm!.GetType("GfxApp.Color")!;
        Assert.True(color.IsValueType);
    }

    [Fact]
    public void Generator_ReportsDiagnostic_OnInvalidSchema()
    {
        const string schema = """{ "type": "not-a-real-type" }""";
        var (_, diagnostics, _) = GeneratorHarness.Run(schema);

        Assert.Contains(diagnostics, d => d.Id == "NJS001" && d.Severity == DiagnosticSeverity.Error);
    }
}
