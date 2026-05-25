using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class M5FeatureRoundtripTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

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
            "M5Generated_" + Guid.NewGuid().ToString("N"),
            trees, refs,
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

    static byte[] Serialize(Assembly asm, string ns, string typeName, object value, bool isEnum = false)
    {
        var formatter = asm.GetType($"{ns}.{typeName}Formatter")!;
        var optsType = asm.GetType($"{ns}.NoJsonSerializerOptions")!;
        var parameterType = isEnum ? asm.GetType($"{ns}.{typeName}")! : value.GetType();
        var method = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null,
            [parameterType, optsType], null)
            ?? throw new InvalidOperationException("SerializeToUtf8Bytes not found");
        return (byte[])method.Invoke(null, [value, null])!;
    }

    static object Deserialize(Assembly asm, string ns, string typeName, byte[] bytes)
    {
        var formatter = asm.GetType($"{ns}.{typeName}Formatter")!;
        var optsType = asm.GetType($"{ns}.NoJsonSerializerOptions")!;
        var method = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(byte[]), optsType], null)
            ?? throw new InvalidOperationException("Deserialize(byte[]) not found");
        return method.Invoke(null, [bytes, null])!;
    }

    void Dump(GenerationResult r)
    {
        foreach (var f in r.Files) { output.WriteLine($"--- {f.FileName} ---"); output.WriteLine(f.SourceText); }
    }

    [Fact]
    public void StringEnum_AsTopLevelDef_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Color": { "type": "string", "enum": ["red", "green", "blue"] }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "EnumTop");
        Dump(gen);

        var colorType = asm.GetType("EnumTop.Color")!;
        Assert.True(colorType.IsEnum);

        var redValue = Enum.Parse(colorType, "Red");
        var bytes = Serialize(asm, "EnumTop", "Color", redValue, isEnum: true);
        Assert.Equal("\"red\"", Encoding.UTF8.GetString(bytes));

        var roundtrip = Deserialize(asm, "EnumTop", "Color", Encoding.UTF8.GetBytes("\"blue\""));
        Assert.Equal(Enum.Parse(colorType, "Blue"), roundtrip);
    }

    [Fact]
    public void StringEnum_AsInlineProperty_GeneratesSyntheticName()
    {
        const string schema = """
        {
          "$defs": {
            "Request": {
              "type": "object",
              "properties": {
                "mode": { "type": "string", "enum": ["launch", "attach"] }
              },
              "required": ["mode"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "EnumInline");
        Dump(gen);

        var enumType = asm.GetType("EnumInline.RequestMode")!;
        Assert.True(enumType.IsEnum);
        Assert.Equal<string[]>(["Launch", "Attach"], Enum.GetNames(enumType));

        var requestType = asm.GetType("EnumInline.Request")!;
        var instance = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Mode")!.SetValue(instance, Enum.Parse(enumType, "Attach"));

        var bytes = Serialize(asm, "EnumInline", "Request", instance);
        Assert.Equal("{\"mode\":\"attach\"}", Encoding.UTF8.GetString(bytes));

        var decoded = Deserialize(asm, "EnumInline", "Request", bytes);
        Assert.Equal(Enum.Parse(enumType, "Attach"), requestType.GetProperty("Mode")!.GetValue(decoded));
    }

    [Fact]
    public void StringEnum_UnknownValue_Throws()
    {
        const string schema = """
        {
          "$defs": {
            "Color": { "type": "string", "enum": ["red", "green"] }
          }
        }
        """;
        var (asm, _) = Compile(schema, "EnumStrict");
        var ex = Assert.ThrowsAny<Exception>(() => Deserialize(asm, "EnumStrict", "Color", Encoding.UTF8.GetBytes("\"yellow\"")));
        var inner = ex.InnerException ?? ex;
        Assert.Contains("yellow", inner.Message);
    }

    [Fact]
    public void DateTimeOffset_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Event": {
              "type": "object",
              "properties": { "occurredAt": { "type": "string", "format": "date-time" } },
              "required": ["occurredAt"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "FmtDt");
        Dump(gen);

        var eventType = asm.GetType("FmtDt.Event")!;
        var occurredAt = eventType.GetProperty("OccurredAt")!;
        Assert.Equal(typeof(DateTimeOffset), occurredAt.PropertyType);

        var instance = Activator.CreateInstance(eventType)!;
        var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        occurredAt.SetValue(instance, stamp);

        var bytes = Serialize(asm, "FmtDt", "Event", instance);
        output.WriteLine(Encoding.UTF8.GetString(bytes));
        var decoded = Deserialize(asm, "FmtDt", "Event", bytes);
        Assert.Equal(stamp, occurredAt.GetValue(decoded));
    }

    [Fact]
    public void Guid_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Doc": {
              "type": "object",
              "properties": { "id": { "type": "string", "format": "uuid" } },
              "required": ["id"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "FmtGuid");
        Dump(gen);

        var docType = asm.GetType("FmtGuid.Doc")!;
        var idProp = docType.GetProperty("Id")!;
        Assert.Equal(typeof(Guid), idProp.PropertyType);

        var instance = Activator.CreateInstance(docType)!;
        var g = Guid.Parse("12345678-1234-5678-1234-567812345678");
        idProp.SetValue(instance, g);

        var bytes = Serialize(asm, "FmtGuid", "Doc", instance);
        Assert.Equal($"{{\"id\":\"{g:D}\"}}", Encoding.UTF8.GetString(bytes));

        var decoded = Deserialize(asm, "FmtGuid", "Doc", bytes);
        Assert.Equal(g, idProp.GetValue(decoded));
    }

    [Fact]
    public void AdditionalProperties_False_Throws_OnUnknownKey()
    {
        const string schema = """
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
        var (asm, _) = Compile(schema, "Strict");

        var ex = Assert.ThrowsAny<Exception>(() =>
            Deserialize(asm, "Strict", "Strict", Encoding.UTF8.GetBytes("{\"a\":\"x\",\"extra\":1}")));
        var inner = ex.InnerException ?? ex;
        Assert.Contains("extra", inner.Message);
    }

    [Fact]
    public void AdditionalProperties_Unspecified_IgnoresUnknownKey()
    {
        const string schema = """
        {
          "$defs": {
            "Loose": {
              "type": "object",
              "properties": { "a": { "type": "string" } },
              "required": ["a"]
            }
          }
        }
        """;
        var (asm, _) = Compile(schema, "Loose");

        var decoded = Deserialize(asm, "Loose", "Loose", Encoding.UTF8.GetBytes("{\"a\":\"x\",\"extra\":42}"));
        Assert.Equal("x", asm.GetType("Loose.Loose")!.GetProperty("A")!.GetValue(decoded));
    }
}
