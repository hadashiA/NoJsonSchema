using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class M6InheritanceTests(ITestOutputHelper output)
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
            "M6Generated_" + Guid.NewGuid().ToString("N"), trees, refs,
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

    static byte[] Serialize(Assembly asm, string ns, string typeName, object value)
    {
        var formatter = asm.GetType($"{ns}.{typeName}Formatter")!;
        var optsType = asm.GetType($"{ns}.NoJsonSerializerOptions")!;
        var method = formatter.GetMethod("SerializeToUtf8Bytes",
            BindingFlags.Public | BindingFlags.Static, null,
            [value.GetType(), optsType], null)
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
    public void AllOfInherit_PocoExtendsBase()
    {
        const string schema = """
        {
          "$defs": {
            "Base": {
              "type": "object",
              "properties": { "id": { "type": "string" } },
              "required": ["id"]
            },
            "Derived": {
              "allOf": [
                { "$ref": "#/$defs/Base" },
                {
                  "type": "object",
                  "properties": { "extra": { "type": "integer" } },
                  "required": ["extra"]
                }
              ]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "AllOfInh");
        Dump(gen);

        var baseType = asm.GetType("AllOfInh.Base")!;
        var derivedType = asm.GetType("AllOfInh.Derived")!;
        Assert.Equal(baseType, derivedType.BaseType);

        var instance = Activator.CreateInstance(derivedType)!;
        derivedType.GetProperty("Id")!.SetValue(instance, "abc");
        derivedType.GetProperty("Extra")!.SetValue(instance, 42L);

        var bytes = Serialize(asm, "AllOfInh", "Derived", instance);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);
        Assert.Contains("\"id\":\"abc\"", json);
        Assert.Contains("\"extra\":42", json);

        var decoded = Deserialize(asm, "AllOfInh", "Derived", bytes);
        Assert.Equal("abc", derivedType.GetProperty("Id")!.GetValue(decoded));
        Assert.Equal(42L, derivedType.GetProperty("Extra")!.GetValue(decoded));
    }

    [Fact]
    public void AllOfInherit_DapShapedRequest_RoundTrips()
    {
        const string schema = """
        {
          "$defs": {
            "ProtocolMessage": {
              "type": "object",
              "properties": {
                "seq":  { "type": "integer" },
                "type": { "type": "string" }
              },
              "required": ["seq", "type"]
            },
            "Request": {
              "allOf": [
                { "$ref": "#/$defs/ProtocolMessage" },
                {
                  "type": "object",
                  "properties": {
                    "type":    { "type": "string", "const": "request" },
                    "command": { "type": "string" }
                  },
                  "required": ["command"]
                }
              ]
            },
            "InitializeRequest": {
              "allOf": [
                { "$ref": "#/$defs/Request" },
                {
                  "type": "object",
                  "properties": {
                    "command":   { "type": "string", "const": "initialize" },
                    "arguments": {
                      "type": "object",
                      "properties": { "adapterID": { "type": "string" } },
                      "required": ["adapterID"]
                    }
                  },
                  "required": ["arguments"]
                }
              ]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "Dap");
        Dump(gen);

        var protocolType = asm.GetType("Dap.ProtocolMessage")!;
        var requestType  = asm.GetType("Dap.Request")!;
        var initType     = asm.GetType("Dap.InitializeRequest")!;
        var argsType     = asm.GetType("Dap.InitializeRequestArguments")!;

        Assert.Equal(protocolType, requestType.BaseType);
        Assert.Equal(requestType,  initType.BaseType);

        var args = Activator.CreateInstance(argsType)!;
        argsType.GetProperty("AdapterID")!.SetValue(args, "vscode");

        var initRequest = Activator.CreateInstance(initType)!;
        initType.GetProperty("Seq")!.SetValue(initRequest, 1L);
        initType.GetProperty("Type")!.SetValue(initRequest, "request");
        initType.GetProperty("Command")!.SetValue(initRequest, "initialize");
        initType.GetProperty("Arguments")!.SetValue(initRequest, args);

        var bytes = Serialize(asm, "Dap", "InitializeRequest", initRequest);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine("emitted: " + json);
        Assert.Contains("\"seq\":1", json);
        Assert.Contains("\"type\":\"request\"", json);
        Assert.Contains("\"command\":\"initialize\"", json);
        Assert.Contains("\"adapterID\":\"vscode\"", json);

        var decoded = Deserialize(asm, "Dap", "InitializeRequest", bytes);
        Assert.Equal(1L, initType.GetProperty("Seq")!.GetValue(decoded));
        Assert.Equal("request", initType.GetProperty("Type")!.GetValue(decoded));
        Assert.Equal("initialize", initType.GetProperty("Command")!.GetValue(decoded));
        var decodedArgs = initType.GetProperty("Arguments")!.GetValue(decoded);
        Assert.NotNull(decodedArgs);
        Assert.Equal("vscode", argsType.GetProperty("AdapterID")!.GetValue(decodedArgs));
    }

    [Fact]
    public void AllOfFlatten_MergedInline_NoBaseClass()
    {
        const string schema = """
        {
          "$defs": {
            "Merged": {
              "allOf": [
                { "type": "object", "properties": { "a": { "type": "string" } }, "required": ["a"] },
                { "type": "object", "properties": { "b": { "type": "integer" } } }
              ]
            }
          }
        }
        """;
        var (asm, gen) = Compile(schema, "Merged");
        Dump(gen);

        var t = asm.GetType("Merged.Merged")!;
        Assert.Equal(typeof(object), t.BaseType);

        var instance = Activator.CreateInstance(t)!;
        t.GetProperty("A")!.SetValue(instance, "hello");
        t.GetProperty("B")!.SetValue(instance, 7L);

        var bytes = Serialize(asm, "Merged", "Merged", instance);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"a\":\"hello\"", json);
        Assert.Contains("\"b\":7", json);

        var decoded = Deserialize(asm, "Merged", "Merged", bytes);
        Assert.Equal("hello", t.GetProperty("A")!.GetValue(decoded));
        Assert.Equal(7L, t.GetProperty("B")!.GetValue(decoded));
    }
}
