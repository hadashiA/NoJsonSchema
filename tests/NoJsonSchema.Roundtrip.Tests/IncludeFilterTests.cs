using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class IncludeFilterTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static (Assembly?, GenerationResult) Generate(string schema, string ns, params string[] includes)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions
        {
            Namespace = ns,
            IncludedTypes = new HashSet<string>(includes, StringComparer.Ordinal),
        });

        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName)).ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "IncludeGen_" + Guid.NewGuid().ToString("N"), trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder("Compilation failed:\n");
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) sb.AppendLine(d.ToString());
            foreach (var f in result.Files) { sb.AppendLine($"=== {f.FileName} ==="); sb.AppendLine(f.SourceText); }
            throw new InvalidOperationException(sb.ToString());
        }
        return (Assembly.Load(ms.ToArray()), result);
    }

    const string Schema = """
    {
      "$defs": {
        "Address":   { "type": "object", "properties": { "city": { "type": "string" } }, "required": ["city"] },
        "ContactBook": { "type": "object", "properties": { "owner": { "type": "string" } } },
        "User": {
          "type": "object",
          "properties": {
            "name":    { "type": "string" },
            "address": { "$ref": "#/$defs/Address" }
          },
          "required": ["name"]
        },
        "Standalone": { "type": "object", "properties": { "x": { "type": "integer" } } }
      }
    }
    """;

    [Fact]
    public void IncludeFilter_DropsUnreferencedTypes()
    {
        var (asm, gen) = Generate(Schema, "IncFilter1", "User");
        var fileNames = gen.Files.Select(f => f.FileName).ToList();
        foreach (var n in fileNames) output.WriteLine(n);

        // User -> Address transitively included; ContactBook and Standalone dropped.
        Assert.Contains("User.g.cs", fileNames);
        Assert.Contains("Address.g.cs", fileNames);
        Assert.Contains("Formatters/UserFormatter.g.cs", fileNames);
        Assert.Contains("Formatters/AddressFormatter.g.cs", fileNames);

        Assert.DoesNotContain("ContactBook.g.cs", fileNames);
        Assert.DoesNotContain("Standalone.g.cs", fileNames);
        Assert.DoesNotContain("Formatters/ContactBookFormatter.g.cs", fileNames);

        Assert.NotNull(asm);
        Assert.NotNull(asm!.GetType("IncFilter1.User"));
        Assert.NotNull(asm.GetType("IncFilter1.Address"));
        Assert.Null(asm.GetType("IncFilter1.ContactBook"));
        Assert.Null(asm.GetType("IncFilter1.Standalone"));
    }

    [Fact]
    public void IncludeFilter_IsCaseInsensitiveOnSeed()
    {
        // Schema $defs key "User" → input "user" (lowercased) should PascalCase to "User".
        var (asm, _) = Generate(Schema, "IncFilter2", "user");
        Assert.NotNull(asm);
        Assert.NotNull(asm!.GetType("IncFilter2.User"));
    }

    [Fact]
    public void IncludeFilter_PolymorphicBranches_AreFollowed()
    {
        // Make sure a polymorphic base pulls in all its branches.
        const string petSchema = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "discriminator": { "propertyName": "kind" },
                "oneOf": [
                  { "$ref": "#/components/schemas/Cat" },
                  { "$ref": "#/components/schemas/Dog" }
                ]
              },
              "Cat": { "type": "object", "properties": { "kind": { "type": "string" } }, "required": ["kind"] },
              "Dog": { "type": "object", "properties": { "kind": { "type": "string" } }, "required": ["kind"] },
              "UnrelatedThing": { "type": "object", "properties": { "x": { "type": "string" } } }
            }
          }
        }
        """;

        var (asm, gen) = Generate(petSchema, "IncPets", "Pet");
        foreach (var f in gen.Files) output.WriteLine(f.FileName);

        Assert.NotNull(asm);
        Assert.NotNull(asm!.GetType("IncPets.Pet"));
        Assert.NotNull(asm.GetType("IncPets.Cat"));
        Assert.NotNull(asm.GetType("IncPets.Dog"));
        Assert.Null(asm.GetType("IncPets.UnrelatedThing"));
    }

    [Fact]
    public void IncludeFilter_UnknownSeed_Throws()
    {
        var ex = Assert.Throws<NoJsonSchema.Core.Schema.SchemaLoadException>(() =>
            Generate(Schema, "IncFilter3", "Nonexistent"));
        Assert.Contains("Nonexistent", ex.Message);
    }
}
