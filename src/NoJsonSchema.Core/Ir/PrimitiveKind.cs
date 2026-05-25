namespace NoJsonSchema.Core.Ir;

/// <summary>
/// The C# primitive a JSON Schema scalar maps to. Driven by the schema's <c>type</c> +
/// <c>format</c> (and OpenAPI integer subtype hints). See
/// <see cref="NoJsonSchema.Core.Resolver.TypeGraphBuilder"/> for the dispatch table.
/// </summary>
public enum PrimitiveKind
{
    String,

    // Integer subtypes (default integer maps to Int64).
    SByte,    // format: int8
    Byte,     // format: uint8 / byte (integer ctx)
    Int16,    // format: int16
    UInt16,   // format: uint16
    Int32,    // format: int32
    UInt32,   // format: uint32
    Int64,    // (default) / format: int64
    UInt64,   // format: uint64

    // Floating point.
    Single,   // format: float / single
    Double,   // (default for number) / format: double

    Boolean,

    // Date / time / duration (.NET 6+ types — generated code targets net7+).
    DateTimeOffset, // format: date-time
    DateOnly,       // format: date
    TimeOnly,       // format: time
    TimeSpan,       // format: duration (ISO 8601 PT…)

    Guid,           // format: uuid

    Uri,            // format: uri / uri-reference → System.Uri

    ByteArray,      // string + format: byte | binary → byte[] (base64)
}
