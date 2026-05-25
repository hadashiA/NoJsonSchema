using System.Text;

namespace NoJsonSchema.Core.Schema;

/// <summary>
/// Helpers for building JSON Pointer strings (<a href="https://datatracker.ietf.org/doc/html/rfc6901">RFC 6901</a>).
/// Loader uses these to attach source locations to each <see cref="SchemaNode"/>.
/// </summary>
static class JsonPointer
{
    public const string Root = "#";

    static readonly char[] EscapeChars = ['~', '/'];

    public static string Append(string pointer, string segment)
    {
        var escaped = Escape(segment);
        return pointer.Length == 0 || pointer == Root
            ? Root + "/" + escaped
            : pointer + "/" + escaped;
    }

    public static string Append(string pointer, int index)
    {
        return pointer.Length == 0 || pointer == Root
            ? Root + "/" + index
            : pointer + "/" + index;
    }

    static string Escape(string segment)
    {
        if (segment.IndexOfAny(EscapeChars) < 0)
        {
            return segment;
        }

        var sb = new StringBuilder(segment.Length + 4);
        foreach (var c in segment)
        {
            switch (c)
            {
                case '~': sb.Append("~0"); break;
                case '/': sb.Append("~1"); break;
                default:  sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
