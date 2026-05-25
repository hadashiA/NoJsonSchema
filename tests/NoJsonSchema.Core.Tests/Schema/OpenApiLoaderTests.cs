using NoJsonSchema.Core.Schema;
using Xunit;

namespace NoJsonSchema.Core.Tests.Schema;

public class OpenApiLoaderTests
{
    [Fact]
    public void OpenApi3_0_LiftsComponentsSchemasAsDefs()
    {
        const string doc = """
        {
          "openapi": "3.0.3",
          "info": { "title": "x", "version": "1.0" },
          "paths": {},
          "components": {
            "schemas": {
              "User": {
                "type": "object",
                "properties": {
                  "id":   { "type": "integer", "format": "int32" },
                  "name": { "type": "string" }
                },
                "required": ["id", "name"]
              },
              "Tag": {
                "type": "string",
                "enum": ["red", "green", "blue"]
              }
            }
          }
        }
        """;
        var loaded = JsonSchemaLoader.Load(doc);

        Assert.Equal("openapi-3.0.3", loaded.Dialect);
        Assert.Equal(2, loaded.Root.Defs.Count);
        Assert.True(loaded.Root.Defs.ContainsKey("User"));
        Assert.True(loaded.Root.Defs.ContainsKey("Tag"));

        var user = loaded.Root.Defs["User"];
        Assert.Equal("#/components/schemas/User", user.Pointer);
        Assert.Equal(2, user.Properties.Count);
    }

    [Fact]
    public void OpenApi_NullableTrue_NormalisedToNullInTypeSet()
    {
        const string doc = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "Optional": {
                "type": "object",
                "properties": {
                  "label": { "type": "string", "nullable": true }
                }
              }
            }
          }
        }
        """;
        var loaded = JsonSchemaLoader.Load(doc);
        var label = loaded.Root.Defs["Optional"].Properties["label"];

        Assert.Equal([JsonSchemaType.String, JsonSchemaType.Null], label.Types);
    }

    [Fact]
    public void OpenApi_NullableTrue_IsIdempotentWithExistingNull()
    {
        const string doc = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "X": {
                "type": "object",
                "properties": {
                  "v": { "type": ["string", "null"], "nullable": true }
                }
              }
            }
          }
        }
        """;
        var loaded = JsonSchemaLoader.Load(doc);
        var v = loaded.Root.Defs["X"].Properties["v"];
        // type already contains "null"; nullable: true must not duplicate it.
        Assert.Equal([JsonSchemaType.String, JsonSchemaType.Null], v.Types);
    }

    [Fact]
    public void RegularJsonSchema_StillLoadsWithoutOpenApiPath()
    {
        // Sanity check that plain JSON Schema documents are unaffected by the OpenAPI shortcut.
        const string doc = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": { "Foo": { "type": "string" } }
        }
        """;
        var loaded = JsonSchemaLoader.Load(doc);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", loaded.Dialect);
        Assert.True(loaded.Root.Defs.ContainsKey("Foo"));
    }
}
