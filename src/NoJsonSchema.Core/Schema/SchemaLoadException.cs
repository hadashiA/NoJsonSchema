namespace NoJsonSchema.Core.Schema;

public sealed class SchemaLoadException : Exception
{
    public SchemaLoadException(string message, string pointer)
        : base($"{message} (at {pointer})")
    {
        Pointer = pointer;
    }

    public SchemaLoadException(string message, string pointer, Exception inner)
        : base($"{message} (at {pointer})", inner)
    {
        Pointer = pointer;
    }

    public string Pointer { get; }
}
