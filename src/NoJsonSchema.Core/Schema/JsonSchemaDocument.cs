namespace NoJsonSchema.Core.Schema;

/// <summary>
/// Top-level schema document. Wraps the root <see cref="SchemaNode"/> with document-level metadata.
/// </summary>
public sealed class JsonSchemaDocument
{
    public required SchemaNode Root { get; init; }

    /// <summary>Value of <c>$schema</c> if specified (e.g. "https://json-schema.org/draft/2020-12/schema").</summary>
    public string? Dialect { get; init; }

    /// <summary>Value of <c>$id</c> if specified.</summary>
    public string? Id { get; init; }

    /// <summary>Source path used to load this document. Optional, informational only.</summary>
    public string? SourcePath { get; init; }
}
