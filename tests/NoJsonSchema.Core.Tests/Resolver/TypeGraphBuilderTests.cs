using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Resolver;
using NoJsonSchema.Core.Schema;
using Xunit;

namespace NoJsonSchema.Core.Tests.Resolver;

public class TypeGraphBuilderTests
{
    static TypeGraph Build(string json, params string[] reservedNames)
    {
        var doc = JsonSchemaLoader.Load(json);
        return new TypeGraphBuilder().Build(doc, reservedNames);
    }

    [Fact]
    public void SingleDefsEntry_BecomesObjectDescriptor()
    {
        var graph = Build("""
        {
          "$defs": {
            "Person": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age":  { "type": "integer" }
              },
              "required": ["name"]
            }
          }
        }
        """);

        var person = Assert.IsType<ObjectTypeDescriptor>(graph.Types["Person"]);
        Assert.Equal(2, person.Properties.Count);

        var name = person.Properties[0];
        Assert.Equal("Name", name.Name);
        Assert.Equal("name", name.JsonName);
        Assert.Equal(TypeRef.PrimitiveString, name.Type);
        Assert.True(name.IsRequired);

        var age = person.Properties[1];
        Assert.Equal("Age", age.Name);
        Assert.Equal(TypeRef.PrimitiveInt64, age.Type);  // default integer width
        Assert.False(age.IsRequired);
    }

    [Fact]
    public void Ref_ResolvesToNamedType()
    {
        var graph = Build("""
        {
          "$defs": {
            "Address": { "type": "object", "properties": { "city": { "type": "string" } } },
            "User":    {
              "type": "object",
              "properties": { "address": { "$ref": "#/$defs/Address" } }
            }
          }
        }
        """);

        var user = Assert.IsType<ObjectTypeDescriptor>(graph.Types["User"]);
        var addressProp = user.Properties[0];
        var named = Assert.IsType<TypeRef.Named>(addressProp.Type);
        Assert.Equal("Address", named.Name);
    }

    [Fact]
    public void InlineObject_GetsSynthesisedNameFromParentAndProperty()
    {
        var graph = Build("""
        {
          "$defs": {
            "User": {
              "type": "object",
              "properties": {
                "address": {
                  "type": "object",
                  "properties": { "city": { "type": "string" } }
                }
              }
            }
          }
        }
        """);

        var user = Assert.IsType<ObjectTypeDescriptor>(graph.Types["User"]);
        var addressType = Assert.IsType<TypeRef.Named>(user.Properties[0].Type);
        Assert.Equal("UserAddress", addressType.Name);

        var inline = Assert.IsType<ObjectTypeDescriptor>(graph.Types["UserAddress"]);
        Assert.Single(inline.Properties);
        Assert.Equal("City", inline.Properties[0].Name);
    }

    [Fact]
    public void ArrayProperty_BecomesArrayTypeRef()
    {
        var graph = Build("""
        {
          "$defs": {
            "Tagged": {
              "type": "object",
              "properties": {
                "tags": { "type": "array", "items": { "type": "string" } }
              }
            }
          }
        }
        """);

        var tags = ((ObjectTypeDescriptor)graph.Types["Tagged"]).Properties[0];
        var array = Assert.IsType<TypeRef.Array>(tags.Type);
        Assert.Equal(TypeRef.PrimitiveString, array.Element);
    }

    [Fact]
    public void NullableTypeUnion_ProducesNullableWrapper()
    {
        var graph = Build("""
        {
          "$defs": {
            "Foo": {
              "type": "object",
              "properties": {
                "name": { "type": ["string", "null"] }
              }
            }
          }
        }
        """);

        var prop = ((ObjectTypeDescriptor)graph.Types["Foo"]).Properties[0];
        Assert.True(prop.IsNullable);
        var nullable = Assert.IsType<TypeRef.Nullable>(prop.Type);
        Assert.Equal(TypeRef.PrimitiveString, nullable.Inner);
    }

    [Fact]
    public void AdditionalPropertiesSchema_BecomesDictionaryRef()
    {
        var graph = Build("""
        {
          "$defs": {
            "Bag": {
              "type": "object",
              "additionalProperties": { "type": "integer" }
            }
          }
        }
        """);

        var bag = Assert.IsType<ObjectTypeDescriptor>(graph.Types["Bag"]);
        Assert.Equal(TypeRef.PrimitiveInt64, bag.AdditionalProperties);
    }

    [Fact]
    public void IntegerFormat_NarrowsToInt32()
    {
        var graph = Build("""
        {
          "$defs": {
            "X": { "type": "object", "properties": { "n": { "type": "integer", "format": "int32" } } }
          }
        }
        """);

        var n = ((ObjectTypeDescriptor)graph.Types["X"]).Properties[0];
        Assert.Equal(TypeRef.PrimitiveInt32, n.Type);
    }

    [Fact]
    public void OneOf_ProducesOpaqueForNow()
    {
        var graph = Build("""
        {
          "$defs": {
            "Choice": { "oneOf": [{ "type": "string" }, { "type": "integer" }] }
          }
        }
        """);

        var choice = Assert.IsType<OpaqueTypeDescriptor>(graph.Types["Choice"]);
        Assert.Equal("oneOf", choice.Reason);
    }

    [Fact]
    public void AllOf_BaseRef_PlusInline_Inherits()
    {
        var graph = Build("""
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
                { "type": "object", "properties": { "extra": { "type": "integer" } }, "required": ["extra"] }
              ]
            }
          }
        }
        """);

        var derived = Assert.IsType<ObjectTypeDescriptor>(graph.Types["Derived"]);
        Assert.Equal("Base", derived.BaseTypeName);
        Assert.Single(derived.Properties);
        Assert.Equal("Extra", derived.Properties[0].Name);
        Assert.True(derived.Properties[0].IsRequired);
    }

    [Fact]
    public void AllOf_InlineOnly_Flattens()
    {
        // No $ref base, only inline branches → flat merge, BaseTypeName stays null.
        var graph = Build("""
        {
          "$defs": {
            "Combined": {
              "allOf": [
                { "type": "object", "properties": { "a": { "type": "string" } } },
                { "type": "object", "properties": { "b": { "type": "integer" } } }
              ]
            }
          }
        }
        """);

        var combined = Assert.IsType<ObjectTypeDescriptor>(graph.Types["Combined"]);
        Assert.Null(combined.BaseTypeName);
        Assert.Equal(2, combined.Properties.Count);
    }

    [Fact]
    public void CyclicRef_DoesNotLoop()
    {
        var graph = Build("""
        {
          "$defs": {
            "Node": {
              "type": "object",
              "properties": {
                "value": { "type": "integer" },
                "next":  { "$ref": "#/$defs/Node" }
              }
            }
          }
        }
        """);

        var node = Assert.IsType<ObjectTypeDescriptor>(graph.Types["Node"]);
        var next = node.Properties[1];
        Assert.Equal("Node", Assert.IsType<TypeRef.Named>(next.Type).Name);
    }

    [Fact]
    public void ReservedNames_AreSkippedForGeneratedTypes()
    {
        // Simulate the namespace-wide Serializer name being reserved up-front.
        var graph = Build("""
        {
          "$defs": {
            "DapSerializer": { "type": "object", "properties": { "x": { "type": "string" } } }
          }
        }
        """, "DapSerializer");

        Assert.True(graph.Types.ContainsKey("DapSerializer2"));
        Assert.False(graph.Types.ContainsKey("DapSerializer"));
    }

    [Fact]
    public void RootObjectWithProperties_BecomesNamedRoot()
    {
        var graph = Build("""
        {
          "type": "object",
          "properties": {
            "ok": { "type": "boolean" }
          }
        }
        """);

        // Root inline object materialises as a fresh named type.
        var named = Assert.IsType<TypeRef.Named>(graph.Root);
        Assert.True(graph.Types.ContainsKey(named.Name));
        var root = Assert.IsType<ObjectTypeDescriptor>(graph.Types[named.Name]);
        Assert.Single(root.Properties);
    }

    [Fact]
    public void UnresolvedExternalRef_Throws()
    {
        Assert.Throws<SchemaLoadException>(() => Build("""
        {
          "$defs": {
            "X": { "$ref": "external.json#/Foo" }
          }
        }
        """));
    }
}
