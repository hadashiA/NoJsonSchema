using NoJsonSchema.Core.Schema;
using Xunit;

namespace NoJsonSchema.Core.Tests.Schema;

public class JsonSchemaLoaderTests
{
    [Fact]
    public void BooleanRoot_True_LoadsAsAlwaysTrue()
    {
        var doc = JsonSchemaLoader.Load("true");
        Assert.Same(SchemaNode.AlwaysTrue, doc.Root);
    }

    [Fact]
    public void BooleanRoot_False_LoadsAsAlwaysFalse()
    {
        var doc = JsonSchemaLoader.Load("false");
        Assert.Same(SchemaNode.AlwaysFalse, doc.Root);
    }

    [Fact]
    public void EmptyObject_LoadsAsObjectNodeWithNoConstraints()
    {
        var doc = JsonSchemaLoader.Load("{}");
        Assert.Equal(SchemaNodeKind.Object, doc.Root.Kind);
        Assert.Empty(doc.Root.Types);
        Assert.Empty(doc.Root.Properties);
        Assert.Empty(doc.Root.Required);
    }

    [Fact]
    public void DocumentMetadata_IsCaptured()
    {
        const string json = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.com/foo",
          "title": "Foo"
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", doc.Dialect);
        Assert.Equal("https://example.com/foo", doc.Id);
        Assert.Equal("Foo", doc.Root.Title);
    }

    [Fact]
    public void Object_PropertiesAndRequired()
    {
        const string json = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "age":  { "type": "integer" }
          },
          "required": ["name"]
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        var root = doc.Root;

        Assert.Equal([JsonSchemaType.Object], root.Types);
        Assert.Equal(2, root.Properties.Count);
        Assert.Equal(JsonSchemaType.String, root.Properties["name"].Types[0]);
        Assert.Equal(JsonSchemaType.Integer, root.Properties["age"].Types[0]);
        Assert.Equal<IReadOnlyList<string>>(["name"], root.Required);
    }

    [Fact]
    public void TypeArray_ProducesMultipleTypes()
    {
        var doc = JsonSchemaLoader.Load("""{ "type": ["string", "null"] }""");
        Assert.Equal([JsonSchemaType.String, JsonSchemaType.Null], doc.Root.Types);
    }

    [Fact]
    public void Ref_IsCapturedVerbatim()
    {
        var doc = JsonSchemaLoader.Load("""{ "$ref": "#/$defs/Foo" }""");
        Assert.Equal("#/$defs/Foo", doc.Root.Ref);
    }

    [Fact]
    public void Defs_BothCanonicalAndLegacyKeysMerge()
    {
        const string json = """
        {
          "$defs":       { "A": { "type": "string" } },
          "definitions": { "B": { "type": "integer" } }
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.Equal(2, doc.Root.Defs.Count);
        Assert.Equal(JsonSchemaType.String, doc.Root.Defs["A"].Types[0]);
        Assert.Equal(JsonSchemaType.Integer, doc.Root.Defs["B"].Types[0]);
    }

    [Fact]
    public void EnumAndConst_AreParsed()
    {
        const string json = """
        {
          "type": "string",
          "enum": ["red", "green", "blue"],
          "const": "red"
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.NotNull(doc.Root.Enum);
        Assert.Collection(doc.Root.Enum!,
            v => Assert.Equal("red",   ((JsonValue.String)v).Value),
            v => Assert.Equal("green", ((JsonValue.String)v).Value),
            v => Assert.Equal("blue",  ((JsonValue.String)v).Value));
        Assert.Equal("red", Assert.IsType<JsonValue.String>(doc.Root.Const).Value);
    }

    [Fact]
    public void AllOf_OneOf_AnyOf_AreLoaded()
    {
        const string json = """
        {
          "allOf": [{ "type": "object" }, { "type": "object" }],
          "oneOf": [{ "type": "string" }, { "type": "integer" }],
          "anyOf": [{ "type": "boolean" }]
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.Equal(2, doc.Root.AllOf.Count);
        Assert.Equal(2, doc.Root.OneOf.Count);
        Assert.Single(doc.Root.AnyOf);
    }

    [Fact]
    public void AdditionalProperties_True_BecomesAlwaysTrueNode()
    {
        var doc = JsonSchemaLoader.Load("""{ "type": "object", "additionalProperties": true }""");
        Assert.Same(SchemaNode.AlwaysTrue, doc.Root.AdditionalProperties);
    }

    [Fact]
    public void AdditionalProperties_False_BecomesAlwaysFalseNode()
    {
        var doc = JsonSchemaLoader.Load("""{ "type": "object", "additionalProperties": false }""");
        Assert.Same(SchemaNode.AlwaysFalse, doc.Root.AdditionalProperties);
    }

    [Fact]
    public void AdditionalProperties_Schema_IsLoaded()
    {
        const string json = """
        { "type": "object", "additionalProperties": { "type": "string" } }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.NotNull(doc.Root.AdditionalProperties);
        Assert.Equal(JsonSchemaType.String, doc.Root.AdditionalProperties!.Types[0]);
    }

    [Fact]
    public void UnknownVocabularyKeys_PreservedAsExtensions()
    {
        // DAP uses keys like _enum to indicate open string enums.
        const string json = """
        {
          "type": "string",
          "_enum": ["launch", "attach"]
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        Assert.True(doc.Root.Extensions.ContainsKey("_enum"));
        var array = Assert.IsType<JsonValue.Array>(doc.Root.Extensions["_enum"]);
        Assert.Equal(2, array.Items.Count);
    }

    [Fact]
    public void Pointer_TracksNestedLocation()
    {
        const string json = """
        {
          "properties": {
            "a/b~c": { "type": "string" }
          }
        }
        """;
        var doc = JsonSchemaLoader.Load(json);
        var p = doc.Root.Properties["a/b~c"];
        // '/' → ~1 and '~' → ~0 per RFC 6901
        Assert.Equal("#/properties/a~1b~0c", p.Pointer);
    }

    [Fact]
    public void InvalidType_Throws()
    {
        var ex = Assert.Throws<SchemaLoadException>(() =>
            JsonSchemaLoader.Load("""{ "type": "stringy" }"""));
        Assert.Contains("stringy", ex.Message);
    }

    [Fact]
    public void InvalidSchemaValue_Throws()
    {
        var ex = Assert.Throws<SchemaLoadException>(() =>
            JsonSchemaLoader.Load("""{ "properties": { "x": 1 } }"""));
        Assert.Contains("Schema must be an object or boolean", ex.Message);
        Assert.Equal("#/properties/x", ex.Pointer);
    }
}
