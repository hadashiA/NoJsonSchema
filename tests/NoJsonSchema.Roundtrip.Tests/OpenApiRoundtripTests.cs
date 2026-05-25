using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class OpenApiRoundtripTests(ITestOutputHelper output)
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
            "OpenApiGenerated_" + Guid.NewGuid().ToString("N"), trees, refs,
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
    public void OpenApi30_GeneratesAndRoundTrips_WithNullable()
    {
        const string doc = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Bookstore", "version": "1.0" },
          "paths": {},
          "components": {
            "schemas": {
              "Author": {
                "type": "object",
                "properties": {
                  "id":       { "type": "integer", "format": "int32" },
                  "name":     { "type": "string" },
                  "nickname": { "type": "string", "nullable": true }
                },
                "required": ["id", "name"]
              },
              "Book": {
                "type": "object",
                "properties": {
                  "title":  { "type": "string" },
                  "author": { "$ref": "#/components/schemas/Author" },
                  "tags":   { "type": "array", "items": { "type": "string" } }
                },
                "required": ["title", "author"]
              }
            }
          }
        }
        """;
        var (asm, generated) = Compile(doc, "BookApi");
        foreach (var f in generated.Files) output.WriteLine($"--- {f.FileName} ---\n{f.SourceText}");

        var authorType = asm.GetType("BookApi.Author")!;
        var bookType = asm.GetType("BookApi.Book")!;

        var author = Activator.CreateInstance(authorType)!;
        authorType.GetProperty("Id")!.SetValue(author, 7);
        authorType.GetProperty("Name")!.SetValue(author, "Knuth");
        // Nickname left null on purpose

        var book = Activator.CreateInstance(bookType)!;
        bookType.GetProperty("Title")!.SetValue(book, "TAOCP");
        bookType.GetProperty("Author")!.SetValue(book, author);
        bookType.GetProperty("Tags")!.SetValue(book, (string[])["math", "algorithms"]);

        var formatter = asm.GetType("BookApi.BookFormatter")!;
        var optsType = asm.GetType("BookApi.NoJsonSerializerOptions")!;
        var serialize = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null, [bookType, optsType], null)!;
        var bytes = (byte[])serialize.Invoke(null, [book, null])!;
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);

        Assert.Contains("\"title\":\"TAOCP\"", json);
        Assert.Contains("\"name\":\"Knuth\"", json);
        Assert.DoesNotContain("\"nickname\"", json); // SkipNullProperties default

        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null, [typeof(byte[]), optsType], null)!;
        var decoded = deserialize.Invoke(null, [bytes, null])!;
        var decodedAuthor = bookType.GetProperty("Author")!.GetValue(decoded);
        Assert.Equal("Knuth", authorType.GetProperty("Name")!.GetValue(decodedAuthor));
        Assert.Null(authorType.GetProperty("Nickname")!.GetValue(decodedAuthor));
        Assert.Equal<string[]>(["math", "algorithms"], (string[])bookType.GetProperty("Tags")!.GetValue(decoded)!);
    }

    [Fact]
    public void OpenApi31_TreatsSchemaSubsetAsJsonSchema202012()
    {
        // OpenAPI 3.1 schemas are valid JSON Schema 2020-12.
        const string doc = """
        {
          "openapi": "3.1.0",
          "info": { "title": "X", "version": "1" },
          "paths": {},
          "components": {
            "schemas": {
              "Item": {
                "type": "object",
                "properties": {
                  "id":    { "type": "string", "format": "uuid" },
                  "value": { "type": ["string", "null"] }
                },
                "required": ["id"]
              }
            }
          }
        }
        """;
        var (asm, _) = Compile(doc, "Items");
        var itemType = asm.GetType("Items.Item")!;
        Assert.Equal(typeof(Guid), itemType.GetProperty("Id")!.PropertyType);

        var instance = Activator.CreateInstance(itemType)!;
        var g = Guid.Parse("00000000-0000-0000-0000-000000000001");
        itemType.GetProperty("Id")!.SetValue(instance, g);

        var formatter = asm.GetType("Items.ItemFormatter")!;
        var optsType = asm.GetType("Items.NoJsonSerializerOptions")!;
        var serialize = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null, [itemType, optsType], null)!;
        var bytes = (byte[])serialize.Invoke(null, [instance, null])!;
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);
        Assert.Contains("\"id\":\"00000000-0000-0000-0000-000000000001\"", json);
    }
}
