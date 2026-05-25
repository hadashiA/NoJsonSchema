using System.Globalization;
using System.Text.Json;

namespace NoJsonSchema.Core.Schema;

/// <summary>
/// Reads a JSON Schema document into <see cref="JsonSchemaDocument"/> form.
/// Performs no <c>$ref</c> resolution and no composition lowering — purely structural.
/// </summary>
public static class JsonSchemaLoader
{
    static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    static JsonSchemaLoader()
    {
        // Touch CultureInfo to silence trim warnings on legacy targets — no-op at runtime.
        _ = CultureInfo.InvariantCulture;
    }

    public static JsonSchemaDocument Load(string json, string? sourcePath = null)
    {
        using var doc = JsonDocument.Parse(json, ReaderOptions);
        return Build(doc.RootElement, sourcePath);
    }

    public static JsonSchemaDocument Load(Stream stream, string? sourcePath = null)
    {
        using var doc = JsonDocument.Parse(stream, ReaderOptions);
        return Build(doc.RootElement, sourcePath);
    }

    public static JsonSchemaDocument Load(ReadOnlyMemory<byte> utf8Json, string? sourcePath = null)
    {
        using var doc = JsonDocument.Parse(utf8Json, ReaderOptions);
        return Build(doc.RootElement, sourcePath);
    }

    static JsonSchemaDocument Build(JsonElement root, string? sourcePath)
    {
        string? dialect = null;
        string? id = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("$schema", out var dialectEl) && dialectEl.ValueKind == JsonValueKind.String)
                dialect = dialectEl.GetString();
            if (root.TryGetProperty("$id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString();
        }

        var rootNode = LoadNode(root, JsonPointer.Root);

        return new JsonSchemaDocument
        {
            Root = rootNode,
            Dialect = dialect,
            Id = id,
            SourcePath = sourcePath,
        };
    }

    static SchemaNode LoadNode(JsonElement element, string pointer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return SchemaNode.AlwaysTrue;
            case JsonValueKind.False:
                return SchemaNode.AlwaysFalse;
            case JsonValueKind.Object:
                return LoadObjectNode(element, pointer);
            default:
                throw new SchemaLoadException(
                    $"Schema must be an object or boolean, got {element.ValueKind}.", pointer);
        }
    }

    static SchemaNode LoadObjectNode(JsonElement element, string pointer)
    {
        string? id = null;
        string? anchor = null;
        string? title = null;
        string? description = null;
        var deprecated = false;
        string? @ref = null;

        IReadOnlyList<JsonSchemaType>? types = null;
        IReadOnlyList<JsonValue>? enumValues = null;
        JsonValue? constValue = null;

        IReadOnlyDictionary<string, SchemaNode>? properties = null;
        IReadOnlyList<string>? required = null;
        SchemaNode? additionalProperties = null;

        SchemaNode? items = null;
        IReadOnlyList<SchemaNode>? prefixItems = null;

        string? format = null;
        string? pattern = null;
        long? minLength = null;
        long? maxLength = null;
        double? minimum = null;
        double? maximum = null;
        double? exclusiveMinimum = null;
        double? exclusiveMaximum = null;
        double? multipleOf = null;
        long? minItems = null;
        long? maxItems = null;
        bool? uniqueItems = null;

        IReadOnlyList<SchemaNode>? allOf = null;
        IReadOnlyList<SchemaNode>? oneOf = null;
        IReadOnlyList<SchemaNode>? anyOf = null;
        SchemaNode? not = null;

        JsonValue? @default = null;
        IReadOnlyList<JsonValue>? examples = null;

        Dictionary<string, SchemaNode>? defs = null;
        Dictionary<string, JsonValue>? extensions = null;

        foreach (var p in element.EnumerateObject())
        {
            switch (p.Name)
            {
                // Metadata
                case "$id":          id = ReadString(p.Value, pointer, p.Name); break;
                case "$anchor":      anchor = ReadString(p.Value, pointer, p.Name); break;
                case "$schema":      break; // handled at document level
                case "title":        title = ReadString(p.Value, pointer, p.Name); break;
                case "description":  description = ReadString(p.Value, pointer, p.Name); break;
                case "deprecated":   deprecated = ReadBool(p.Value, pointer, p.Name); break;

                // Reference
                case "$ref":         @ref = ReadString(p.Value, pointer, p.Name); break;

                // Type
                case "type":
                    types = ReadTypes(p.Value, JsonPointer.Append(pointer, "type"));
                    break;

                // Enum / const
                case "enum":
                    enumValues = ReadValueArray(p.Value, JsonPointer.Append(pointer, "enum"));
                    break;
                case "const":
                    constValue = ReadValue(p.Value);
                    break;

                // Object
                case "properties":
                    properties = ReadProperties(p.Value, JsonPointer.Append(pointer, "properties"));
                    break;
                case "required":
                    required = ReadStringArray(p.Value, JsonPointer.Append(pointer, "required"));
                    break;
                case "additionalProperties":
                    additionalProperties = LoadNode(p.Value, JsonPointer.Append(pointer, "additionalProperties"));
                    break;

                // Array
                case "items":
                    items = LoadNode(p.Value, JsonPointer.Append(pointer, "items"));
                    break;
                case "prefixItems":
                    prefixItems = ReadNodeArray(p.Value, JsonPointer.Append(pointer, "prefixItems"));
                    break;

                // String constraints
                case "format":     format = ReadString(p.Value, pointer, p.Name); break;
                case "pattern":    pattern = ReadString(p.Value, pointer, p.Name); break;
                case "minLength":  minLength = ReadInt64(p.Value, pointer, p.Name); break;
                case "maxLength":  maxLength = ReadInt64(p.Value, pointer, p.Name); break;

                // Number constraints
                case "minimum":          minimum = ReadDouble(p.Value, pointer, p.Name); break;
                case "maximum":          maximum = ReadDouble(p.Value, pointer, p.Name); break;
                case "exclusiveMinimum": exclusiveMinimum = ReadDouble(p.Value, pointer, p.Name); break;
                case "exclusiveMaximum": exclusiveMaximum = ReadDouble(p.Value, pointer, p.Name); break;
                case "multipleOf":       multipleOf = ReadDouble(p.Value, pointer, p.Name); break;

                // Array constraints
                case "minItems":    minItems = ReadInt64(p.Value, pointer, p.Name); break;
                case "maxItems":    maxItems = ReadInt64(p.Value, pointer, p.Name); break;
                case "uniqueItems": uniqueItems = ReadBool(p.Value, pointer, p.Name); break;

                // Composition
                case "allOf": allOf = ReadNodeArray(p.Value, JsonPointer.Append(pointer, "allOf")); break;
                case "oneOf": oneOf = ReadNodeArray(p.Value, JsonPointer.Append(pointer, "oneOf")); break;
                case "anyOf": anyOf = ReadNodeArray(p.Value, JsonPointer.Append(pointer, "anyOf")); break;
                case "not":   not = LoadNode(p.Value, JsonPointer.Append(pointer, "not")); break;

                // Defaults / examples
                case "default":  @default = ReadValue(p.Value); break;
                case "examples": examples = ReadValueArray(p.Value, JsonPointer.Append(pointer, "examples")); break;

                // Definitions (both flavors land in the same bucket)
                case "$defs":
                case "definitions":
                    defs ??= new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
                    var defsPtr = JsonPointer.Append(pointer, p.Name);
                    foreach (var def in p.Value.EnumerateObject())
                    {
                        defs[def.Name] = LoadNode(def.Value, JsonPointer.Append(defsPtr, def.Name));
                    }
                    break;

                default:
                    extensions ??= new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    extensions[p.Name] = ReadValue(p.Value);
                    break;
            }
        }

        return new SchemaNode
        {
            Pointer = pointer,
            Id = id,
            Anchor = anchor,
            Title = title,
            Description = description,
            Deprecated = deprecated,
            Ref = @ref,
            Types = types ?? [],
            Enum = enumValues,
            Const = constValue,
            Properties = properties ?? SchemaNode.EmptyProperties,
            Required = required ?? [],
            AdditionalProperties = additionalProperties,
            Items = items,
            PrefixItems = prefixItems ?? [],
            Format = format,
            Pattern = pattern,
            MinLength = minLength,
            MaxLength = maxLength,
            Minimum = minimum,
            Maximum = maximum,
            ExclusiveMinimum = exclusiveMinimum,
            ExclusiveMaximum = exclusiveMaximum,
            MultipleOf = multipleOf,
            MinItems = minItems,
            MaxItems = maxItems,
            UniqueItems = uniqueItems,
            AllOf = allOf ?? [],
            OneOf = oneOf ?? [],
            AnyOf = anyOf ?? [],
            Not = not,
            Default = @default,
            Examples = examples ?? [],
            Defs = defs ?? SchemaNode.EmptyProperties,
            Extensions = extensions ?? SchemaNode.EmptyExtensions,
        };
    }

    static IReadOnlyList<JsonSchemaType> ReadTypes(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return [ParseType(element.GetString()!, pointer)];
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<JsonSchemaType>(element.GetArrayLength());
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new SchemaLoadException(
                        "Each entry of 'type' array must be a string.", JsonPointer.Append(pointer, i));
                }
                list.Add(ParseType(item.GetString()!, JsonPointer.Append(pointer, i)));
                i++;
            }
            return list;
        }

        throw new SchemaLoadException("'type' must be a string or array of strings.", pointer);
    }

    static JsonSchemaType ParseType(string name, string pointer) => name switch
    {
        "string"  => JsonSchemaType.String,
        "integer" => JsonSchemaType.Integer,
        "number"  => JsonSchemaType.Number,
        "boolean" => JsonSchemaType.Boolean,
        "object"  => JsonSchemaType.Object,
        "array"   => JsonSchemaType.Array,
        "null"    => JsonSchemaType.Null,
        _ => throw new SchemaLoadException($"Unknown JSON Schema type '{name}'.", pointer),
    };

    static IReadOnlyDictionary<string, SchemaNode> ReadProperties(JsonElement element, string pointer)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SchemaLoadException("'properties' must be an object.", pointer);
        }

        var result = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
        {
            result[p.Name] = LoadNode(p.Value, JsonPointer.Append(pointer, p.Name));
        }
        return result;
    }

    static IReadOnlyList<SchemaNode> ReadNodeArray(JsonElement element, string pointer)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaLoadException("Expected an array of schemas.", pointer);
        }

        var list = new List<SchemaNode>(element.GetArrayLength());
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(LoadNode(item, JsonPointer.Append(pointer, i)));
            i++;
        }
        return list;
    }

    static IReadOnlyList<string> ReadStringArray(JsonElement element, string pointer)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaLoadException("Expected an array of strings.", pointer);
        }

        var list = new List<string>(element.GetArrayLength());
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new SchemaLoadException("Expected string.", JsonPointer.Append(pointer, i));
            }
            list.Add(item.GetString()!);
            i++;
        }
        return list;
    }

    static IReadOnlyList<JsonValue> ReadValueArray(JsonElement element, string pointer)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaLoadException("Expected an array.", pointer);
        }

        var list = new List<JsonValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ReadValue(item));
        }
        return list;
    }

    static JsonValue ReadValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return new JsonValue.String(element.GetString()!);
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    return new JsonValue.Integer(l);
                return new JsonValue.Number(element.GetDouble());
            case JsonValueKind.True:
                return new JsonValue.Boolean(true);
            case JsonValueKind.False:
                return new JsonValue.Boolean(false);
            case JsonValueKind.Null:
                return JsonValue.Null.Instance;
            case JsonValueKind.Array:
            {
                var items = new List<JsonValue>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(ReadValue(item));
                }
                return new JsonValue.Array(items);
            }
            case JsonValueKind.Object:
            {
                var props = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                {
                    props[p.Name] = ReadValue(p.Value);
                }
                return new JsonValue.Object(props);
            }
            default:
                throw new InvalidOperationException($"Unexpected JsonValueKind {element.ValueKind}");
        }
    }

    static string ReadString(JsonElement element, string parentPointer, string keyName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new SchemaLoadException($"'{keyName}' must be a string.", JsonPointer.Append(parentPointer, keyName));
        }
        return element.GetString()!;
    }

    static bool ReadBool(JsonElement element, string parentPointer, string keyName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SchemaLoadException($"'{keyName}' must be a boolean.", JsonPointer.Append(parentPointer, keyName)),
        };
    }

    static long ReadInt64(JsonElement element, string parentPointer, string keyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var v))
        {
            throw new SchemaLoadException($"'{keyName}' must be an integer.", JsonPointer.Append(parentPointer, keyName));
        }
        return v;
    }

    static double ReadDouble(JsonElement element, string parentPointer, string keyName)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new SchemaLoadException($"'{keyName}' must be a number.", JsonPointer.Append(parentPointer, keyName));
        }
        return element.GetDouble();
    }
}
