using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;

namespace NoJsonSchema.Core.Emit;

/// <summary>Emits <c>{Enum}.g.cs</c>: a plain C# <c>enum</c> mapped to the schema's string values.</summary>
public static class EnumTypeEmitter
{
    public static string Emit(EnumTypeDescriptor type, GenerationOptions options)
    {
        var w = new SourceWriter();
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine($"namespace {options.Namespace};");
        w.WriteLine();

        TypeEmitter.EmitXmlDoc(w, type.Description);
        using (w.Block($"public enum {NameFactory.EscapeIfReserved(type.Name)}"))
        {
            foreach (var m in type.Members)
            {
                TypeEmitter.EmitXmlDoc(w, m.Description);
                w.WriteLine(NameFactory.EscapeIfReserved(m.Name) + ",");
            }
        }

        return w.ToString();
    }
}
