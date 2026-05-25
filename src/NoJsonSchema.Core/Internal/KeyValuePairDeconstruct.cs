#if NETSTANDARD2_0
// KeyValuePair<TKey, TValue>.Deconstruct(out, out) ships with .NET Standard 2.1+. Provide a shim
// so our `foreach (var (k, v) in dict)` patterns keep working on netstandard2.0.
namespace System.Collections.Generic;

static class KeyValuePairDeconstructExtensions
{
    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
    {
        key = kv.Key;
        value = kv.Value;
    }
}
#endif
