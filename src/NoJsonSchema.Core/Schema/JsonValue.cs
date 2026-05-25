namespace NoJsonSchema.Core.Schema;

/// <summary>
/// A literal JSON value extracted from <c>const</c>, <c>enum</c>, <c>default</c> or unknown extension keys.
/// Numeric values are split into <see cref="Integer"/> (fits in <see cref="long"/>) and <see cref="Number"/> (everything else).
/// </summary>
public abstract record JsonValue
{
    public sealed record String(string Value) : JsonValue;
    public sealed record Integer(long Value) : JsonValue;
    public sealed record Number(double Value) : JsonValue;
    public sealed record Boolean(bool Value) : JsonValue;
    public sealed record Array(IReadOnlyList<JsonValue> Items) : JsonValue;
    public sealed record Object(IReadOnlyDictionary<string, JsonValue> Properties) : JsonValue;
    public sealed record Null : JsonValue
    {
        public static readonly Null Instance = new();
    }
}
