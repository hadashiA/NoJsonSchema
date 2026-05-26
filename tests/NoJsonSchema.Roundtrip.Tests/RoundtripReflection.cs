using System.Reflection;
using System.Text;

namespace NoJsonSchema.Roundtrip.Tests;

/// <summary>
/// Reflection helpers that target the **public** namespace-wide Serializer (the user-facing API).
/// The per-type Formatter is internal-by-design (static-method bag); tests deliberately avoid
/// calling into it directly so they exercise the supported surface.
/// </summary>
static class RoundtripReflection
{
    /// <summary>Resolve the generated namespace-wide Serializer type (e.g. <c>NsX.NsXSerializer</c>).</summary>
    public static Type Serializer(Assembly asm, string ns)
        => asm.GetType($"{ns}.{ns}Serializer")
            ?? throw new InvalidOperationException($"Serializer type '{ns}.{ns}Serializer' not found in {asm}");

    /// <summary>Deserialize <paramref name="bytes"/> into the named type via <c>Serializer.Deserialize&lt;T&gt;(byte[])</c>.</summary>
    public static object Deserialize(Assembly asm, string ns, string typeName, byte[] bytes)
    {
        var serializer = Serializer(asm, ns);
        var targetType = asm.GetType($"{ns}.{typeName}")
            ?? throw new InvalidOperationException($"Target type '{ns}.{typeName}' not found");
        var open = serializer.GetMethods()
            .First(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition
                && m.GetParameters() is var p && p.Length == 2
                && p[0].ParameterType == typeof(byte[]));
        var closed = open.MakeGenericMethod(targetType);
        return closed.Invoke(null, [bytes, null])
            ?? throw new InvalidOperationException("Deserialize returned null for reference type");
    }

    /// <summary>Serialize <paramref name="value"/> to a UTF-8 byte array via <c>Serializer.SerializeToUtf8Bytes&lt;T&gt;</c>.</summary>
    public static byte[] SerializeToUtf8Bytes(Assembly asm, string ns, Type targetType, object value)
    {
        var serializer = Serializer(asm, ns);
        var open = serializer.GetMethods()
            .First(m => m.Name == "SerializeToUtf8Bytes" && m.IsGenericMethodDefinition);
        var closed = open.MakeGenericMethod(targetType);
        return (byte[])closed.Invoke(null, [value, null])!;
    }

    /// <summary>Deserialize then immediately re-serialize, returning the round-tripped JSON as a UTF-8 string.</summary>
    public static (object instance, string serialized) Roundtrip(Assembly asm, string ns, string typeName, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var instance = Deserialize(asm, ns, typeName, bytes);
        var targetType = asm.GetType($"{ns}.{typeName}")!;
        var roundtripped = SerializeToUtf8Bytes(asm, ns, targetType, instance);
        return (instance, Encoding.UTF8.GetString(roundtripped));
    }
}
