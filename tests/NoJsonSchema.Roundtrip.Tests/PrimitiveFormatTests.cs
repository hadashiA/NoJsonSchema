using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

/// <summary>
/// End-to-end tests for the extended JSON Schema <c>format</c> dispatch — integer width/sign,
/// date/time/duration, uri, and base64 byte arrays. Each test compiles the generated code, then
/// roundtrips a value through Serialize → Deserialize and confirms the underlying CLR property
/// type is what we expect.
/// </summary>
public class PrimitiveFormatTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static Assembly Compile(string schema, string ns)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions { Namespace = ns });

        var trees = result.Files
            .Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName))
            .ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "PrimFmtGen_" + Guid.NewGuid().ToString("N"), trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder("Compilation failed:\n");
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine(d.ToString());
            foreach (var f in result.Files)
            {
                sb.AppendLine($"=== {f.FileName} ===");
                sb.AppendLine(f.SourceText);
            }
            throw new InvalidOperationException(sb.ToString());
        }
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>Roundtrip helper: deserialize JSON via the public Serializer, reserialize, compare.</summary>
    static (object instance, string serialized) Roundtrip(Assembly asm, string ns, string typeName, string json)
        => RoundtripReflection.Roundtrip(asm, ns, typeName, json);

    [Fact]
    public void IntegerWidths_RoundTrip()
    {
        const string schema = """
        {
          "$defs": {
            "Widths": {
              "type": "object",
              "properties": {
                "a": { "type": "integer", "format": "int8" },
                "b": { "type": "integer", "format": "uint8" },
                "c": { "type": "integer", "format": "int16" },
                "d": { "type": "integer", "format": "uint16" },
                "e": { "type": "integer", "format": "int32" },
                "f": { "type": "integer", "format": "uint32" },
                "g": { "type": "integer", "format": "int64" },
                "h": { "type": "integer", "format": "uint64" }
              },
              "required": ["a","b","c","d","e","f","g","h"]
            }
          }
        }
        """;

        var asm = Compile(schema, "PrimFmt1");
        var t = asm.GetType("PrimFmt1.Widths")!;

        // CLR property types match the format-mapped C# types.
        Assert.Equal(typeof(sbyte),  t.GetProperty("A")!.PropertyType);
        Assert.Equal(typeof(byte),   t.GetProperty("B")!.PropertyType);
        Assert.Equal(typeof(short),  t.GetProperty("C")!.PropertyType);
        Assert.Equal(typeof(ushort), t.GetProperty("D")!.PropertyType);
        Assert.Equal(typeof(int),    t.GetProperty("E")!.PropertyType);
        Assert.Equal(typeof(uint),   t.GetProperty("F")!.PropertyType);
        Assert.Equal(typeof(long),   t.GetProperty("G")!.PropertyType);
        Assert.Equal(typeof(ulong),  t.GetProperty("H")!.PropertyType);

        var (_, json) = Roundtrip(asm, "PrimFmt1", "Widths",
            """{"a":-128,"b":255,"c":-32768,"d":65535,"e":-2147483648,"f":4294967295,"g":-9223372036854775808,"h":18446744073709551615}""");

        // Each value is preserved (asymmetry like u64 max would silently truncate to int64 without these formats).
        Assert.Contains("\"b\":255", json);
        Assert.Contains("\"d\":65535", json);
        Assert.Contains("\"f\":4294967295", json);
        Assert.Contains("\"h\":18446744073709551615", json);
        output.WriteLine(json);
    }

    [Fact]
    public void DateTime_Formats_RoundTrip()
    {
        const string schema = """
        {
          "$defs": {
            "TemporalBag": {
              "type": "object",
              "properties": {
                "d":  { "type": "string", "format": "date" },
                "t":  { "type": "string", "format": "time" },
                "dt": { "type": "string", "format": "date-time" },
                "du": { "type": "string", "format": "duration" }
              },
              "required": ["d","t","dt","du"]
            }
          }
        }
        """;

        var asm = Compile(schema, "PrimFmt2");
        var t = asm.GetType("PrimFmt2.TemporalBag")!;
        Assert.Equal(typeof(DateOnly),       t.GetProperty("D")!.PropertyType);
        Assert.Equal(typeof(TimeOnly),       t.GetProperty("T")!.PropertyType);
        Assert.Equal(typeof(DateTimeOffset), t.GetProperty("Dt")!.PropertyType);
        Assert.Equal(typeof(TimeSpan),       t.GetProperty("Du")!.PropertyType);

        var (inst, json) = Roundtrip(asm, "PrimFmt2", "TemporalBag",
            """{"d":"2025-01-15","t":"09:30:00","dt":"2025-01-15T09:30:00Z","du":"PT1H30M"}""");

        Assert.Equal(new DateOnly(2025, 1, 15), t.GetProperty("D")!.GetValue(inst));
        Assert.Equal(new TimeOnly(9, 30, 0),     t.GetProperty("T")!.GetValue(inst));
        Assert.Equal(TimeSpan.FromMinutes(90),  t.GetProperty("Du")!.GetValue(inst));

        Assert.Contains("\"d\":\"2025-01-15\"", json);
        Assert.Contains("\"du\":\"PT1H30M\"", json);
        output.WriteLine(json);
    }

    [Fact]
    public void Uri_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Link": {
              "type": "object",
              "properties": {
                "href":     { "type": "string", "format": "uri" },
                "fragment": { "type": "string", "format": "uri-reference" }
              },
              "required": ["href","fragment"]
            }
          }
        }
        """;

        var asm = Compile(schema, "PrimFmt3");
        var t = asm.GetType("PrimFmt3.Link")!;
        Assert.Equal(typeof(Uri), t.GetProperty("Href")!.PropertyType);
        Assert.Equal(typeof(Uri), t.GetProperty("Fragment")!.PropertyType);

        var (inst, json) = Roundtrip(asm, "PrimFmt3", "Link",
            """{"href":"https://example.com/x","fragment":"/relative/path"}""");

        Assert.Equal(new Uri("https://example.com/x"), t.GetProperty("Href")!.GetValue(inst));
        Assert.Equal(new Uri("/relative/path", UriKind.Relative), t.GetProperty("Fragment")!.GetValue(inst));
        Assert.Contains("\"href\":\"https://example.com/x\"", json);
    }

    [Fact]
    public void ByteArray_Base64_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Payload": {
              "type": "object",
              "properties": {
                "data": { "type": "string", "format": "byte" },
                "blob": { "type": "string", "format": "binary" }
              },
              "required": ["data","blob"]
            }
          }
        }
        """;

        var asm = Compile(schema, "PrimFmt4");
        var t = asm.GetType("PrimFmt4.Payload")!;
        Assert.Equal(typeof(byte[]), t.GetProperty("Data")!.PropertyType);
        Assert.Equal(typeof(byte[]), t.GetProperty("Blob")!.PropertyType);

        // "Hello" → SGVsbG8=  ;  bytes 0x00..0x05 → AAECAwQF
        var (inst, json) = Roundtrip(asm, "PrimFmt4", "Payload",
            """{"data":"SGVsbG8=","blob":"AAECAwQF"}""");

        Assert.Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }, (byte[])t.GetProperty("Data")!.GetValue(inst)!);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, (byte[])t.GetProperty("Blob")!.GetValue(inst)!);
        Assert.Contains("\"data\":\"SGVsbG8=\"", json);
        output.WriteLine(json);
    }
}
