using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class ValueObjectStructTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static (Assembly, GenerationResult) Compile(string schema, string ns, params string[] valueObjects)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions
        {
            Namespace = ns,
            ValueObjectTypes = new HashSet<string>(valueObjects, StringComparer.Ordinal),
        });
        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName)).ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "VoGenerated_" + Guid.NewGuid().ToString("N"), trees, refs,
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

    static byte[] SerializeStruct(Assembly asm, string ns, string typeName, object value)
    {
        var formatter = asm.GetType($"{ns}.{typeName}Formatter")!;
        var optsType = asm.GetType($"{ns}.NoJsonSerializerOptions")!;
        var structType = asm.GetType($"{ns}.{typeName}")!;
        // Match "in T" parameter; reflection treats it like a regular ref-to-T parameter type.
        var paramType = structType.MakeByRefType();
        var method = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null,
            [paramType, optsType], null)
            ?? throw new InvalidOperationException("SerializeToUtf8Bytes(in T) not found");
        var args = new object?[] { value, null };
        var bytes = (byte[])method.Invoke(null, args)!;
        return bytes;
    }

    static object DeserializeStruct(Assembly asm, string ns, string typeName, byte[] bytes)
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
    public void ValueObject_GeneratesReadonlyRecordStruct()
    {
        const string schema = """
        {
          "$defs": {
            "SemVer": {
              "type": "object",
              "properties": {
                "major": { "type": "integer", "format": "int32" },
                "minor": { "type": "integer", "format": "int32" },
                "patch": { "type": "integer", "format": "int32" }
              },
              "required": ["major", "minor", "patch"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "VoSemVer", "SemVer");
        Dump(gen);

        var semVerType = asm.GetType("VoSemVer.SemVer")!;
        Assert.True(semVerType.IsValueType);
        Assert.NotNull(semVerType.GetCustomAttribute<System.Runtime.CompilerServices.IsReadOnlyAttribute>());

        // Construct via primary ctor
        var ctor = semVerType.GetConstructors().Single(c => c.GetParameters().Length == 3);
        var instance = ctor.Invoke([1, 2, 3])!;

        var bytes = SerializeStruct(asm, "VoSemVer", "SemVer", instance);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);
        Assert.Equal("{\"major\":1,\"minor\":2,\"patch\":3}", json);

        var roundtrip = DeserializeStruct(asm, "VoSemVer", "SemVer", bytes);
        Assert.Equal(instance, roundtrip);
    }

    [Fact]
    public void ValueObject_NestedInsideClass_RoundTrips()
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
            },
            "Pixel": {
              "type": "object",
              "properties": {
                "x":     { "type": "integer", "format": "int32" },
                "y":     { "type": "integer", "format": "int32" },
                "color": { "$ref": "#/$defs/Color" }
              },
              "required": ["x", "y", "color"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "VoPixel", "Color");
        Dump(gen);

        var colorType = asm.GetType("VoPixel.Color")!;
        var pixelType = asm.GetType("VoPixel.Pixel")!;
        Assert.True(colorType.IsValueType);
        Assert.False(pixelType.IsValueType);

        var color = colorType.GetConstructors().Single(c => c.GetParameters().Length == 3).Invoke([255, 0, 128])!;
        var pixel = Activator.CreateInstance(pixelType)!;
        pixelType.GetProperty("X")!.SetValue(pixel, 10);
        pixelType.GetProperty("Y")!.SetValue(pixel, 20);
        pixelType.GetProperty("Color")!.SetValue(pixel, color);

        var formatter = asm.GetType("VoPixel.PixelFormatter")!;
        var optsType = asm.GetType("VoPixel.NoJsonSerializerOptions")!;
        var serialize = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null, [pixelType, optsType], null)!;
        var bytes = (byte[])serialize.Invoke(null, [pixel, null])!;
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);
        Assert.Contains("\"color\":{\"r\":255,\"g\":0,\"b\":128}", json);

        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null, [typeof(byte[]), optsType], null)!;
        var decoded = deserialize.Invoke(null, [bytes, null])!;
        Assert.Equal(10, pixelType.GetProperty("X")!.GetValue(decoded));
        var decodedColor = pixelType.GetProperty("Color")!.GetValue(decoded);
        Assert.Equal(color, decodedColor);
    }

    [Fact]
    public void GenericDispatch_AvoidsBoxing()
    {
        // Confirms the namespace-wide Deserialize<T>/Serialize<T> works for a value-object type
        // (and via Unsafe.As, so it's source-level correct — boxing avoidance is JIT-verified).
        const string schema = """
        {
          "$defs": {
            "Id": {
              "type": "object",
              "properties": { "value": { "type": "integer", "format": "int32" } },
              "required": ["value"]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "VoDispatch", "Id");
        Dump(gen);

        var idType = asm.GetType("VoDispatch.Id")!;
        var serializerType = asm.GetType("VoDispatch.VoDispatchSerializer")!;
        var idInstance = idType.GetConstructors().Single().Invoke([42])!;

        var bytes = (byte[])serializerType
            .GetMethods()
            .First(m => m.Name == "SerializeToUtf8Bytes" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(idType)
            .Invoke(null, [idInstance, null])!;
        Assert.Equal("{\"value\":42}", Encoding.UTF8.GetString(bytes));

        var deserialized = serializerType
            .GetMethods()
            .First(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition
                && m.GetParameters()[0].ParameterType == typeof(byte[]))
            .MakeGenericMethod(idType)
            .Invoke(null, [bytes, null])!;
        Assert.Equal(idInstance, deserialized);
    }

    [Fact]
    public void ValueObject_OnInheritanceBase_Throws()
    {
        const string schema = """
        {
          "$defs": {
            "Base": {
              "type": "object",
              "properties": { "a": { "type": "string" } },
              "required": ["a"]
            },
            "Derived": {
              "allOf": [
                { "$ref": "#/$defs/Base" },
                { "type": "object", "properties": { "b": { "type": "integer" } } }
              ]
            }
          }
        }
        """;
        // 'Base' is used as a base type of 'Derived' — marking it value-object must fail.
        var ex = Assert.Throws<NoJsonSchema.Core.Schema.SchemaLoadException>(() =>
            Compile(schema, "VoConflict", "Base"));
        Assert.Contains("Base", ex.Message);
        Assert.Contains("base type", ex.Message);
    }

    [Fact]
    public void ValueObject_WithAllOf_Throws()
    {
        const string schema = """
        {
          "$defs": {
            "Base": { "type": "object", "properties": { "a": { "type": "string" } } },
            "Vo": {
              "allOf": [
                { "$ref": "#/$defs/Base" },
                { "type": "object", "properties": { "b": { "type": "integer" } } }
              ]
            }
          }
        }
        """;
        // 'Vo' has its own allOf base — also conflicting.
        var ex = Assert.Throws<NoJsonSchema.Core.Schema.SchemaLoadException>(() =>
            Compile(schema, "VoAllOf", "Vo"));
        Assert.Contains("Vo", ex.Message);
    }
}
