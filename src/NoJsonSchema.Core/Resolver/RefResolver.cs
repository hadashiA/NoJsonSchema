using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.Core.Resolver;

/// <summary>
/// Maps JSON-Pointer references to the C# type names produced by <see cref="Naming.NameFactory"/>.
/// Only intra-document references (those starting with <c>#</c>) are supported for now.
/// </summary>
public sealed class RefResolver
{
    readonly Dictionary<string, string> pointerToName = new(StringComparer.Ordinal);

    public void Register(string pointer, string typeName)
    {
        pointerToName[pointer] = typeName;
    }

    public string ResolveToName(string refString, string sourcePointer)
    {
        if (string.IsNullOrEmpty(refString) || refString[0] != '#')
        {
            throw new SchemaLoadException(
                $"External $ref is not supported: '{refString}'.", sourcePointer);
        }

        if (!pointerToName.TryGetValue(refString, out var name))
        {
            throw new SchemaLoadException(
                $"Unresolved $ref '{refString}'. Only references into '$defs' / 'definitions' are supported.",
                sourcePointer);
        }

        return name;
    }

    public bool TryResolve(string refString, out string name) =>
        pointerToName.TryGetValue(refString, out name!);
}
