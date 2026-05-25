using NoJsonSchema.Core.Schema;
using Xunit;

namespace NoJsonSchema.Core.Tests.Schema;

/// <summary>
/// Sanity checks against the kind of constructs the Debug Adapter Protocol schema actually contains:
/// allOf-based "extends", $ref into $defs, const + property-based discrimination, _enum extension.
/// </summary>
public class DapSampleLoaderTests
{
    const string Json = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Debug Adapter Protocol (subset)",
      "$defs": {
        "ProtocolMessage": {
          "type": "object",
          "title": "Base class of requests, responses, and events.",
          "properties": {
            "seq":  { "type": "integer" },
            "type": { "type": "string", "_enum": ["request", "response", "event"] }
          },
          "required": ["seq", "type"]
        },
        "Request": {
          "allOf": [
            { "$ref": "#/$defs/ProtocolMessage" },
            {
              "type": "object",
              "properties": {
                "type":      { "type": "string", "const": "request" },
                "command":   { "type": "string" },
                "arguments": {}
              },
              "required": ["type", "command"]
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
                "arguments": { "$ref": "#/$defs/InitializeRequestArguments" }
              },
              "required": ["command", "arguments"]
            }
          ]
        },
        "InitializeRequestArguments": {
          "type": "object",
          "properties": {
            "clientID":   { "type": "string" },
            "clientName": { "type": "string" },
            "adapterID":  { "type": "string" },
            "locale":     { "type": "string" },
            "linesStartAt1":   { "type": "boolean" },
            "columnsStartAt1": { "type": "boolean" },
            "pathFormat":      { "type": "string", "_enum": ["path", "uri"] }
          },
          "required": ["adapterID"]
        }
      }
    }
    """;

    [Fact]
    public void LoadsWithoutError()
    {
        var doc = JsonSchemaLoader.Load(Json);
        Assert.Equal(4, doc.Root.Defs.Count);
    }

    [Fact]
    public void ProtocolMessage_Type_HasOpenEnumExtension()
    {
        var doc = JsonSchemaLoader.Load(Json);
        var typeProp = doc.Root.Defs["ProtocolMessage"].Properties["type"];
        Assert.True(typeProp.Extensions.ContainsKey("_enum"));
    }

    [Fact]
    public void Request_AllOf_HasRefBranchAndInlineBranch()
    {
        var doc = JsonSchemaLoader.Load(Json);
        var req = doc.Root.Defs["Request"];

        Assert.Equal(2, req.AllOf.Count);
        Assert.Equal("#/$defs/ProtocolMessage", req.AllOf[0].Ref);
        Assert.Contains("command", req.AllOf[1].Properties.Keys);
    }

    [Fact]
    public void InitializeRequest_Const_DiscriminatesByCommand()
    {
        var doc = JsonSchemaLoader.Load(Json);
        var init = doc.Root.Defs["InitializeRequest"];
        var inline = init.AllOf[1];
        var commandConst = Assert.IsType<JsonValue.String>(inline.Properties["command"].Const);
        Assert.Equal("initialize", commandConst.Value);
    }

    [Fact]
    public void Pointer_ReflectsDefsPath()
    {
        var doc = JsonSchemaLoader.Load(Json);
        var args = doc.Root.Defs["InitializeRequestArguments"];
        Assert.Equal("#/$defs/InitializeRequestArguments", args.Pointer);

        var locale = args.Properties["locale"];
        Assert.Equal("#/$defs/InitializeRequestArguments/properties/locale", locale.Pointer);
    }
}
