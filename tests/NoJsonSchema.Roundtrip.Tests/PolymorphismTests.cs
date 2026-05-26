using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class PolymorphismTests(ITestOutputHelper output)
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
            "PolyGenerated_" + Guid.NewGuid().ToString("N"), trees, refs,
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

    [Fact]
    public void OpenApiDiscriminator_PetExample_RoundTrips()
    {
        const string doc = """
        {
          "openapi": "3.0.3",
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "discriminator": {
                  "propertyName": "petType",
                  "mapping": {
                    "cat": "#/components/schemas/Cat",
                    "dog": "#/components/schemas/Dog"
                  }
                },
                "oneOf": [
                  { "$ref": "#/components/schemas/Cat" },
                  { "$ref": "#/components/schemas/Dog" }
                ]
              },
              "Cat": {
                "type": "object",
                "properties": {
                  "petType": { "type": "string" },
                  "name":    { "type": "string" },
                  "purrs":   { "type": "boolean" }
                },
                "required": ["petType", "name"]
              },
              "Dog": {
                "type": "object",
                "properties": {
                  "petType":  { "type": "string" },
                  "name":     { "type": "string" },
                  "barksLoud": { "type": "boolean" }
                },
                "required": ["petType", "name"]
              }
            }
          }
        }
        """;

        var (asm, gen) = Compile(doc, "Pets");
        foreach (var f in gen.Files) output.WriteLine($"--- {f.FileName} ---\n{f.SourceText}");

        var petType = asm.GetType("Pets.Pet")!;
        var catType = asm.GetType("Pets.Cat")!;
        var dogType = asm.GetType("Pets.Dog")!;

        // Inheritance wiring
        Assert.True(petType.IsAbstract);
        Assert.Equal(petType, catType.BaseType);
        Assert.Equal(petType, dogType.BaseType);

        // Build a Cat instance and serialize through the polymorphic base formatter.
        var cat = Activator.CreateInstance(catType)!;
        catType.GetProperty("PetType")!.SetValue(cat, "cat");
        catType.GetProperty("Name")!.SetValue(cat, "Whiskers");
        catType.GetProperty("Purrs")!.SetValue(cat, true);

        // Serialize via the polymorphic base (Pet), but pass the Cat instance — the Serializer<T>
        // dispatches polymorphically to CatFormatter.Serialize.
        var bytes = RoundtripReflection.SerializeToUtf8Bytes(asm, "Pets", petType, cat);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);
        Assert.Contains("\"petType\":\"cat\"", json);
        Assert.Contains("\"purrs\":true", json);

        // Deserialize back via the polymorphic base — must hand us a Cat.
        var decoded = RoundtripReflection.Deserialize(asm, "Pets", "Pet", bytes);
        Assert.IsType(catType, decoded);
        Assert.Equal("Whiskers", catType.GetProperty("Name")!.GetValue(decoded));

        // The other branch: Dog
        var dogJson = "{\"petType\":\"dog\",\"name\":\"Rex\",\"barksLoud\":true}";
        var decodedDog = RoundtripReflection.Deserialize(asm, "Pets", "Pet", Encoding.UTF8.GetBytes(dogJson));
        Assert.IsType(dogType, decodedDog);
        Assert.Equal("Rex", dogType.GetProperty("Name")!.GetValue(decodedDog));
    }

    [Fact]
    public void OpenApiDiscriminator_NoMapping_UsesShortName()
    {
        // No "mapping" → discriminator value defaults to the schema's short name (Cat, Dog).
        const string doc = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "Shape": {
                "type": "object",
                "discriminator": { "propertyName": "kind" },
                "oneOf": [
                  { "$ref": "#/components/schemas/Circle" },
                  { "$ref": "#/components/schemas/Square" }
                ]
              },
              "Circle": {
                "type": "object",
                "properties": { "kind": { "type": "string" }, "radius": { "type": "integer", "format": "int32" } },
                "required": ["kind", "radius"]
              },
              "Square": {
                "type": "object",
                "properties": { "kind": { "type": "string" }, "side": { "type": "integer", "format": "int32" } },
                "required": ["kind", "side"]
              }
            }
          }
        }
        """;

        var (asm, gen) = Compile(doc, "Shapes");
        foreach (var f in gen.Files.Where(f => f.FileName.Contains("Shape"))) output.WriteLine($"--- {f.FileName} ---\n{f.SourceText}");

        // The implicit discriminator value is the short ref name.
        var decoded = RoundtripReflection.Deserialize(asm, "Shapes", "Shape",
            Encoding.UTF8.GetBytes("{\"kind\":\"Circle\",\"radius\":5}"));
        Assert.Equal("Circle", decoded.GetType().Name);
    }

    [Fact]
    public void PolymorphicBase_WithOwnPropertiesAndAllOfInheritance_IsCarriedToBranches()
    {
        // Regression: a polymorphic base used to be emitted as `abstract class Base { }` with
        // properties + allOf-inherited members silently dropped. Now `Base` must carry both —
        // and the branches inherit through it. This is the DAP `Request` shape:
        //   ProtocolMessage  →  Request (abstract, polymorphic by `command`)  →  InitializeRequest / AttachRequest.
        const string doc = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "ProtocolMessage": {
                "type": "object",
                "properties": {
                  "seq":  { "type": "integer", "format": "int32" },
                  "type": { "type": "string" }
                },
                "required": ["seq", "type"]
              },
              "Request": {
                "allOf": [
                  { "$ref": "#/components/schemas/ProtocolMessage" },
                  {
                    "type": "object",
                    "properties": {
                      "command":   { "type": "string" },
                      "arguments": { "type": "string" }
                    },
                    "required": ["command"]
                  }
                ],
                "discriminator": { "propertyName": "command" },
                "oneOf": [
                  { "$ref": "#/components/schemas/InitializeRequest" },
                  { "$ref": "#/components/schemas/AttachRequest" }
                ]
              },
              "InitializeRequest": {
                "type": "object",
                "properties": {
                  "command":  { "type": "string" },
                  "clientID": { "type": "string" }
                },
                "required": ["command", "clientID"]
              },
              "AttachRequest": {
                "type": "object",
                "properties": {
                  "command": { "type": "string" }
                },
                "required": ["command"]
              }
            }
          }
        }
        """;

        var (asm, gen) = Compile(doc, "DapLike");
        foreach (var f in gen.Files.Where(f => f.FileName.EndsWith("Request.g.cs") || f.FileName.EndsWith("ProtocolMessage.g.cs")))
            output.WriteLine($"--- {f.FileName} ---\n{f.SourceText}");

        var protocolMessage = asm.GetType("DapLike.ProtocolMessage")!;
        var request = asm.GetType("DapLike.Request")!;
        var initRequest = asm.GetType("DapLike.InitializeRequest")!;

        // Inheritance chain survived: Request : ProtocolMessage, InitializeRequest : Request.
        Assert.Equal(protocolMessage, request.BaseType);
        Assert.Equal(request, initRequest.BaseType);
        Assert.True(request.IsAbstract);

        // Request itself owns `command`/`arguments` (its own + allOf inline), so the abstract base
        // doesn't lose them — the previous bug.
        Assert.NotNull(request.GetProperty("Command"));
        Assert.NotNull(request.GetProperty("Arguments"));
        // And the ProtocolMessage chain still gives Seq/Type, reachable through the abstract base.
        Assert.NotNull(initRequest.GetProperty("Seq"));
        Assert.NotNull(initRequest.GetProperty("Type"));

        // Round-trip through the polymorphic base — must reconstruct as InitializeRequest with the
        // full property set (seq + type + command + clientID).
        var json = "{\"seq\":1,\"type\":\"request\",\"command\":\"InitializeRequest\",\"clientID\":\"vscode\"}";
        var decoded = RoundtripReflection.Deserialize(asm, "DapLike", "Request", Encoding.UTF8.GetBytes(json));
        Assert.Equal(initRequest, decoded.GetType());
        Assert.Equal(1, initRequest.GetProperty("Seq")!.GetValue(decoded));
        Assert.Equal("request", initRequest.GetProperty("Type")!.GetValue(decoded));
        Assert.Equal("InitializeRequest", initRequest.GetProperty("Command")!.GetValue(decoded));
        Assert.Equal("vscode", initRequest.GetProperty("ClientID")!.GetValue(decoded));
    }

    [Fact]
    public void PolymorphicDispatch_UnknownDiscriminator_Throws()
    {
        const string doc = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "Animal": {
                "type": "object",
                "discriminator": { "propertyName": "species" },
                "oneOf": [{ "$ref": "#/components/schemas/Bee" }]
              },
              "Bee": {
                "type": "object",
                "properties": { "species": { "type": "string" } },
                "required": ["species"]
              }
            }
          }
        }
        """;
        var (asm, _) = Compile(doc, "Zoo");

        var ex = Assert.ThrowsAny<Exception>(() =>
            RoundtripReflection.Deserialize(asm, "Zoo", "Animal", Encoding.UTF8.GetBytes("{\"species\":\"shark\"}")));
        var inner = ex.InnerException ?? ex;
        Assert.Contains("shark", inner.Message);
    }
}
