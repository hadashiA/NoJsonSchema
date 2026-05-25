using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class ErrorLocationTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    static (Assembly, GenerationResult) Compile(string schema, string ns)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions { Namespace = ns });

        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName)).ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "ErrLocGen_" + Guid.NewGuid().ToString("N"), trees, refs,
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

    static Exception InvokeAndUnwrap(MethodInfo m, object?[] args)
    {
        try
        {
            m.Invoke(null, args);
            throw new Xunit.Sdk.XunitException("Expected exception but call returned successfully.");
        }
        catch (TargetInvocationException tie)
        {
            return tie.InnerException ?? tie;
        }
    }

    const string Schema = """
    {
      "$defs": {
        "User": {
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

    [Fact]
    public void MalformedJson_ReportsLineAndColumn()
    {
        var (asm, _) = Compile(Schema, "ErrLoc1");
        var formatter = asm.GetType("ErrLoc1.UserFormatter")!;
        var optsType = asm.GetType("ErrLoc1.NoJsonSerializerOptions")!;
        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null, [typeof(byte[]), optsType], null)!;

        // The garbage `???` lives on line 3, starting at column 1.
        var json = "{\n  \"name\": \"Ada\",\n???\n}\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = InvokeAndUnwrap(deserialize, [bytes, null]);

        output.WriteLine(ex.Message);
        Assert.Contains("line 3", ex.Message);
        Assert.Contains("column 1", ex.Message);

        var exceptionType = ex.GetType();
        Assert.Equal(3, exceptionType.GetProperty("Line")!.GetValue(ex));
        Assert.Equal(1, exceptionType.GetProperty("Column")!.GetValue(ex));
    }

    [Fact]
    public void UnknownProperty_WithStrictMode_ReportsLineAndColumn()
    {
        const string strictSchema = """
        {
          "$defs": {
            "Strict": {
              "type": "object",
              "properties": { "a": { "type": "string" } },
              "required": ["a"],
              "additionalProperties": false
            }
          }
        }
        """;
        var (asm, _) = Compile(strictSchema, "ErrLoc2");
        var formatter = asm.GetType("ErrLoc2.StrictFormatter")!;
        var optsType = asm.GetType("ErrLoc2.NoJsonSerializerOptions")!;
        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null, [typeof(byte[]), optsType], null)!;

        // unknown "extra" sits on line 3.
        var json = "{\n  \"a\": \"x\",\n  \"extra\": 1\n}\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = InvokeAndUnwrap(deserialize, [bytes, null]);

        output.WriteLine(ex.Message);
        Assert.Contains("Unknown property", ex.Message);
        Assert.Contains("extra", ex.Message);
        // The error is raised right after consuming the property name, so the reported position is
        // somewhere on line 3 — exact column depends on tokenizer state, but line should be 3.
        var exceptionType = ex.GetType();
        Assert.Equal(3, exceptionType.GetProperty("Line")!.GetValue(ex));
    }

    [Fact]
    public void TypeMismatch_OnSecondLine_ReportsLine2()
    {
        var (asm, _) = Compile(Schema, "ErrLoc3");
        var formatter = asm.GetType("ErrLoc3.UserFormatter")!;
        var optsType = asm.GetType("ErrLoc3.NoJsonSerializerOptions")!;
        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null, [typeof(byte[]), optsType], null)!;

        // age expects an integer, but gets a string on line 2.
        var json = "{\n  \"age\": \"not-a-number\",\n  \"name\": \"Ada\"\n}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = InvokeAndUnwrap(deserialize, [bytes, null]);

        output.WriteLine(ex.Message);
        var exceptionType = ex.GetType();
        Assert.Equal(2, exceptionType.GetProperty("Line")!.GetValue(ex));
    }
}
