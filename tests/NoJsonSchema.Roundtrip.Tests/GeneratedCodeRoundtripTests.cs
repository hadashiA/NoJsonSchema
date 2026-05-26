using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class GeneratedCodeRoundtripTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static (Assembly Asm, GenerationResult Generated) Compile(string schemaJson, GenerationOptions options)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schemaJson, options);

        var trees = result.Files
            .Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, path: f.FileName))
            .ToList();

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "NoJsonSchemaGenerated_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Compilation failed:");
            foreach (var d in errors) sb.AppendLine(d.ToString());
            sb.AppendLine();
            foreach (var f in result.Files)
            {
                sb.AppendLine($"=== {f.FileName} ===");
                sb.AppendLine(f.SourceText);
            }
            throw new InvalidOperationException(sb.ToString());
        }

        ms.Position = 0;
        return (Assembly.Load(ms.ToArray()), result);
    }

    static byte[] Serialize(Assembly asm, string ns, string typeName, object value)
        => RoundtripReflection.SerializeToUtf8Bytes(asm, ns, asm.GetType($"{ns}.{typeName}")!, value);

    static object Deserialize(Assembly asm, string ns, string typeName, byte[] bytes)
        => RoundtripReflection.Deserialize(asm, ns, typeName, bytes);

    [Fact]
    public void Object_WithPrimitives_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Person": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age":  { "type": "integer" },
                "active": { "type": "boolean" }
              },
              "required": ["name", "age", "active"]
            }
          }
        }
        """;
        const string Ns = "RtPersonNs";
        var (asm, generated) = Compile(schema, new GenerationOptions { Namespace = Ns });
        DumpOnFailure(generated);

        var person = asm.GetType($"{Ns}.Person")!;
        var instance = Activator.CreateInstance(person)!;
        person.GetProperty("Name")!.SetValue(instance, "Alice");
        person.GetProperty("Age")!.SetValue(instance, 30L);
        person.GetProperty("Active")!.SetValue(instance, true);

        var bytes = Serialize(asm, Ns, "Person", instance);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);

        var decoded = Deserialize(asm, Ns, "Person", bytes);
        Assert.Equal("Alice", person.GetProperty("Name")!.GetValue(decoded));
        Assert.Equal(30L, person.GetProperty("Age")!.GetValue(decoded));
        Assert.Equal(true, person.GetProperty("Active")!.GetValue(decoded));
    }

    [Fact]
    public void Object_WithRefAndArray_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "Address": {
              "type": "object",
              "properties": { "city": { "type": "string" } },
              "required": ["city"]
            },
            "User": {
              "type": "object",
              "properties": {
                "name":      { "type": "string" },
                "addresses": { "type": "array", "items": { "$ref": "#/$defs/Address" } },
                "tags":      { "type": "array", "items": { "type": "string" } }
              },
              "required": ["name"]
            }
          }
        }
        """;
        const string Ns = "RtUserNs";
        var (asm, generated) = Compile(schema, new GenerationOptions { Namespace = Ns });
        DumpOnFailure(generated);

        var addressType = asm.GetType($"{Ns}.Address")!;
        var userType = asm.GetType($"{Ns}.User")!;

        var address = Activator.CreateInstance(addressType)!;
        addressType.GetProperty("City")!.SetValue(address, "Tokyo");

        var addressArray = Array.CreateInstance(addressType, 1);
        addressArray.SetValue(address, 0);

        var user = Activator.CreateInstance(userType)!;
        userType.GetProperty("Name")!.SetValue(user, "Bob");
        userType.GetProperty("Addresses")!.SetValue(user, addressArray);
        userType.GetProperty("Tags")!.SetValue(user, (string[])["x", "y"]);

        var bytes = Serialize(asm, Ns, "User", user);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);

        Assert.Contains("\"name\":\"Bob\"", json);
        Assert.Contains("\"city\":\"Tokyo\"", json);
        Assert.Contains("\"tags\":[\"x\",\"y\"]", json);

        var decoded = Deserialize(asm, Ns, "User", bytes);
        Assert.Equal("Bob", userType.GetProperty("Name")!.GetValue(decoded));
        var decodedAddresses = (Array)userType.GetProperty("Addresses")!.GetValue(decoded)!;
        Assert.Single(decodedAddresses);
        Assert.Equal("Tokyo", addressType.GetProperty("City")!.GetValue(decodedAddresses.GetValue(0)));
        var decodedTags = (string[])userType.GetProperty("Tags")!.GetValue(decoded)!;
        Assert.Equal<string[]>(["x", "y"], decodedTags);
    }

    [Fact]
    public void Object_WithOptionalProperties_OmitsNulls()
    {
        const string schema = """
        {
          "$defs": {
            "Foo": {
              "type": "object",
              "properties": {
                "a": { "type": "string" },
                "b": { "type": "integer" }
              },
              "required": ["a"]
            }
          }
        }
        """;
        const string Ns = "RtOptNs";
        var (asm, generated) = Compile(schema, new GenerationOptions { Namespace = Ns });
        DumpOnFailure(generated);

        var foo = asm.GetType($"{Ns}.Foo")!;
        var instance = Activator.CreateInstance(foo)!;
        foo.GetProperty("A")!.SetValue(instance, "hi");

        var bytes = Serialize(asm, Ns, "Foo", instance);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);
        Assert.Equal("{\"a\":\"hi\"}", json);

        var decoded = Deserialize(asm, Ns, "Foo", bytes);
        Assert.Equal("hi", foo.GetProperty("A")!.GetValue(decoded));
        Assert.Null(foo.GetProperty("B")!.GetValue(decoded));
    }

    void DumpOnFailure(GenerationResult r)
    {
        foreach (var f in r.Files)
        {
            output.WriteLine($"--- {f.FileName} ---");
            output.WriteLine(f.SourceText);
        }
    }
}
