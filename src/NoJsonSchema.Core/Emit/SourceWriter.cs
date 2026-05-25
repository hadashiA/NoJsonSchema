using System.Text;

namespace NoJsonSchema.Core.Emit;

/// <summary>
/// Minimal indent-aware text builder used by the emitters. Writes 4-space indents and tracks
/// whether the current position is at the start of a line so that <see cref="Write"/>/<see cref="WriteLine"/>
/// know when to emit the indent prefix.
/// </summary>
public sealed class SourceWriter
{
    sealed class Closer(SourceWriter w) : IDisposable
    {
        public void Dispose()
        {
            w.Outdent();
            w.WriteLine("}");
        }
    }

    readonly StringBuilder sb = new();
    int indent;
    bool atLineStart = true;

    public int IndentLevel => indent;

    public void Indent() => indent++;

    public void Outdent()
    {
        if (indent == 0) throw new InvalidOperationException("Outdent below zero.");
        indent--;
    }

    public IDisposable Block(string header)
    {
        WriteLine(header);
        WriteLine("{");
        Indent();
        return new Closer(this);
    }

    public IDisposable BraceBlock() => Block(string.Empty);

    public void WriteLine() { EnsureIndent(); sb.Append('\n'); atLineStart = true; }

    public void WriteLine(string text)
    {
        if (text.Length == 0) { WriteLine(); return; }
        WriteMultiline(text);
        sb.Append('\n');
        atLineStart = true;
    }

    public void Write(string text)
    {
        if (text.Length == 0) return;
        WriteMultiline(text);
    }

    public override string ToString() => sb.ToString();

    void WriteMultiline(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                EnsureIndent();
                sb.Append(text, start, i - start);
                sb.Append('\n');
                atLineStart = true;
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            EnsureIndent();
            sb.Append(text, start, text.Length - start);
            atLineStart = false;
        }
    }

    void EnsureIndent()
    {
        if (!atLineStart) return;
        for (var i = 0; i < indent; i++) sb.Append("    ");
        atLineStart = false;
    }
}
