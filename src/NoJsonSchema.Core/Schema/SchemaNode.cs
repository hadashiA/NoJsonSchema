namespace NoJsonSchema.Core.Schema;

public enum SchemaNodeKind
{
    /// <summary>Regular object schema (the default).</summary>
    Object,

    /// <summary>The JSON value <c>true</c> used as a schema — matches anything.</summary>
    AlwaysTrue,

    /// <summary>The JSON value <c>false</c> used as a schema — matches nothing.</summary>
    AlwaysFalse,
}

/// <summary>
/// A single node in a JSON Schema document. Closely mirrors the on-wire structure — no $ref resolution
/// or composition lowering happens here. The Resolver / Lowering passes consume this and produce IR.
/// </summary>
public sealed class SchemaNode
{
    internal static readonly IReadOnlyDictionary<string, SchemaNode> EmptyProperties
        = new Dictionary<string, SchemaNode>(0);
    internal static readonly IReadOnlyDictionary<string, JsonValue> EmptyExtensions
        = new Dictionary<string, JsonValue>(0);

    public static readonly SchemaNode AlwaysTrue = new() { Kind = SchemaNodeKind.AlwaysTrue };
    public static readonly SchemaNode AlwaysFalse = new() { Kind = SchemaNodeKind.AlwaysFalse };

    public SchemaNodeKind Kind { get; init; } = SchemaNodeKind.Object;

    /// <summary>JSON Pointer to this node within the source document (e.g. <c>#/definitions/Foo</c>).</summary>
    public string Pointer { get; init; } = "#";

    // --- Metadata ---
    public string? Id { get; init; }
    public string? Anchor { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Deprecated { get; init; }

    // --- Reference ---
    /// <summary>The literal <c>$ref</c> string, unresolved. Resolver pass turns this into a node link.</summary>
    public string? Ref { get; init; }

    // --- Type ---
    /// <summary>One or more JSON Schema primitive types. Empty means "any type".</summary>
    public IReadOnlyList<JsonSchemaType> Types { get; init; } = [];

    // --- Enum / const ---
    public IReadOnlyList<JsonValue>? Enum { get; init; }
    public JsonValue? Const { get; init; }

    // --- Object ---
    public IReadOnlyDictionary<string, SchemaNode> Properties { get; init; } = EmptyProperties;
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>
    /// Tri-state: <c>null</c> = unspecified (default allow + untyped),
    /// <see cref="AlwaysTrue"/> = explicit allow, <see cref="AlwaysFalse"/> = explicit deny,
    /// any other node = schema applied to extra properties.
    /// </summary>
    public SchemaNode? AdditionalProperties { get; init; }

    // --- Array ---
    public SchemaNode? Items { get; init; }
    public IReadOnlyList<SchemaNode> PrefixItems { get; init; } = [];

    // --- String / number constraints (kept for validation pass, not yet wired into emit) ---
    public string? Format { get; init; }
    public string? Pattern { get; init; }
    public long? MinLength { get; init; }
    public long? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? ExclusiveMinimum { get; init; }
    public double? ExclusiveMaximum { get; init; }
    public double? MultipleOf { get; init; }
    public long? MinItems { get; init; }
    public long? MaxItems { get; init; }
    public bool? UniqueItems { get; init; }

    // --- Composition ---
    public IReadOnlyList<SchemaNode> AllOf { get; init; } = [];
    public IReadOnlyList<SchemaNode> OneOf { get; init; } = [];
    public IReadOnlyList<SchemaNode> AnyOf { get; init; } = [];
    public SchemaNode? Not { get; init; }

    /// <summary>OpenAPI <c>discriminator</c> block, when present.</summary>
    public DiscriminatorRaw? Discriminator { get; init; }

    // --- Defaults / examples ---
    public JsonValue? Default { get; init; }
    public IReadOnlyList<JsonValue> Examples { get; init; } = [];

    // --- Definitions (both 2020-12 $defs and legacy "definitions" land here) ---
    public IReadOnlyDictionary<string, SchemaNode> Defs { get; init; } = EmptyProperties;

    /// <summary>
    /// Unknown vocabulary keys preserved verbatim. DAP uses keys like <c>_enum</c> / <c>_int</c> here.
    /// </summary>
    public IReadOnlyDictionary<string, JsonValue> Extensions { get; init; } = EmptyExtensions;
}
