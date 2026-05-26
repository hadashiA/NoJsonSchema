using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class RequiredModifierTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static (Assembly, GenerationResult) Compile(string schema, string ns, bool useRequired)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions
        {
            Namespace = ns,
            UseRequiredModifier = useRequired,
        });

        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName)).ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "ReqGenerated_" + Guid.NewGuid().ToString("N"), trees, refs,
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

    const string PersonSchema = """
    {
      "$defs": {
        "Person": {
          "type": "object",
          "properties": {
            "name":  { "type": "string" },
            "email": { "type": "string" },
            "age":   { "type": "integer", "format": "int32" }
          },
          "required": ["name", "age"]
        }
      }
    }
    """;

    [Fact]
    public void DefaultMode_UsesDefaultBangSuppression()
    {
        var (asm, gen) = Compile(PersonSchema, "PersonDefault", useRequired: false);
        var pocoFile = gen.Files.First(f => f.FileName == "Person.g.cs").SourceText;
        output.WriteLine(pocoFile);

        // Without UseRequiredModifier, non-nullable ref props get `= null!;`.
        Assert.Contains("public string Name { get; set; } = null!;", pocoFile);
        // Value types don't need the suppression.
        Assert.Contains("public int Age { get; set; }", pocoFile);
        Assert.DoesNotContain("required", pocoFile);

        // Roundtrip still works.
        var personType = asm.GetType("PersonDefault.Person")!;
        var instance = Activator.CreateInstance(personType)!;
        personType.GetProperty("Name")!.SetValue(instance, "Ada");
        personType.GetProperty("Age")!.SetValue(instance, 36);

        var bytes = RoundtripReflection.SerializeToUtf8Bytes(asm, "PersonDefault", personType, instance);
        Assert.Equal("{\"name\":\"Ada\",\"age\":36}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void RequiredMode_EmitsRequiredModifier_AndStillRoundTrips()
    {
        var (asm, gen) = Compile(PersonSchema, "PersonReq", useRequired: true);
        var pocoFile = gen.Files.First(f => f.FileName == "Person.g.cs").SourceText;
        output.WriteLine(pocoFile);

        Assert.Contains("public required string Name { get; set; }", pocoFile);
        Assert.Contains("public required int Age { get; set; }", pocoFile);
        // Email is optional → no required, type is annotated nullable.
        Assert.Contains("public string? Email { get; set; }", pocoFile);
        // No '= null!' anywhere on these properties — required modifier replaces the suppression.
        Assert.DoesNotContain("= null!", pocoFile);

        // Roundtrip via reflection: ctor + init both work.
        var personType = asm.GetType("PersonReq.Person")!;
        var ctor = personType.GetConstructor(Type.EmptyTypes)!;
        var instance = ctor.Invoke(null);
        personType.GetProperty("Name")!.SetValue(instance, "Grace");
        personType.GetProperty("Age")!.SetValue(instance, 85);

        var bytes = RoundtripReflection.SerializeToUtf8Bytes(asm, "PersonReq", personType, instance);
        Assert.Equal("{\"name\":\"Grace\",\"age\":85}", Encoding.UTF8.GetString(bytes));

        var decoded = RoundtripReflection.Deserialize(asm, "PersonReq", "Person", bytes);
        Assert.Equal("Grace", personType.GetProperty("Name")!.GetValue(decoded));
        Assert.Equal(85, personType.GetProperty("Age")!.GetValue(decoded));
    }
}
