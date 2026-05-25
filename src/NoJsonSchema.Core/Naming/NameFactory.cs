using System.Globalization;
using System.Text;

namespace NoJsonSchema.Core.Naming;

/// <summary>
/// Centralised naming: turns arbitrary JSON keys/pointers into legal C# identifiers and
/// guarantees uniqueness within a single graph.
/// </summary>
public sealed class NameFactory
{
    // C# 12 reserved keywords (subset relevant to identifiers). PascalCase removes most clashes already.
    static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    readonly HashSet<string> usedTypeNames = new(StringComparer.Ordinal);

    public static string ToTypeIdentifier(string raw) => PascalCase(raw, isType: true);
    public static string ToPropertyIdentifier(string raw) => PascalCase(raw, isType: false);

    /// <summary>Whether the given identifier is a C# reserved keyword that must be escaped with <c>@</c>.</summary>
    public static bool IsReservedKeyword(string name) => ReservedKeywords.Contains(name);

    /// <summary>Returns <paramref name="name"/>, prefixed with <c>@</c> if it collides with a C# keyword.</summary>
    public static string EscapeIfReserved(string name) =>
        ReservedKeywords.Contains(name) ? "@" + name : name;

    /// <summary>
    /// Property names share the per-type namespace, so siblings are passed in explicitly.
    /// Reserved words are emitted with an <c>@</c> prefix at code-gen time, not here.
    /// </summary>
    public static string MakeUniquePropertyName(string jsonName, ISet<string> siblings)
    {
        var baseName = ToPropertyIdentifier(jsonName);
        if (siblings.Add(baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var candidate = baseName + i.ToString(CultureInfo.InvariantCulture);
            if (siblings.Add(candidate)) return candidate;
        }
    }

    /// <summary>Register a name as already taken (e.g. the generated Serializer class).</summary>
    public void ReserveTypeName(string name) => usedTypeNames.Add(name);

    /// <summary>Allocate a unique type name based on <paramref name="raw"/>, applying PascalCase.</summary>
    public string MakeUniqueTypeName(string raw)
    {
        var baseName = ToTypeIdentifier(raw);
        if (usedTypeNames.Add(baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var candidate = baseName + i.ToString(CultureInfo.InvariantCulture);
            if (usedTypeNames.Add(candidate)) return candidate;
        }
    }

    static string PascalCase(string raw, bool isType)
    {
        if (string.IsNullOrEmpty(raw)) return isType ? "Unnamed" : "Value";

        var sb = new StringBuilder(raw.Length);
        var capitaliseNext = true;
        foreach (var c in raw)
        {
            if (c is '_' or '-' or '.' or ' ' or '/' or '\\' or ':' or '$' or '#')
            {
                capitaliseNext = true;
                continue;
            }

            if (sb.Length == 0 && !IsIdentifierStart(c)) sb.Append('_');

            if (!IsIdentifierPart(c))
            {
                sb.Append('_');
                capitaliseNext = true;
                continue;
            }

            sb.Append(capitaliseNext ? char.ToUpperInvariant(c) : c);
            capitaliseNext = false;
        }

        if (sb.Length == 0) sb.Append(isType ? "Unnamed" : "Value");
        return sb.ToString();
    }

    static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
