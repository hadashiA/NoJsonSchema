using System.Text.Json;
using BenchmarkDotNet.Attributes;
using NoJsonBench;
using StjBench;

namespace NoJsonSchema.Benchmarks;

/// <summary>
/// Deserialize a payload that actually exercises the SIMD hot paths in the generated tokenizer:
/// long string fields (so <c>ReadStringValue</c> scans 16-byte blocks) and, in the indented
/// variant, whitespace between every token (so <c>SkipWhitespace</c> scans 16-byte blocks).
/// Contrast with <see cref="DeserializeBenchmark"/>, whose tiny compact payload has short strings
/// and no whitespace and so never triggers the vectorized path.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DeserializeLargeBenchmark
{
    byte[] compact = default!;
    byte[] indented = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Long string fields + many tags -> string scans span multiple 16-byte blocks.
        var user = new StjUser
        {
            Id = 42,
            Name = new string('A', 256),
            Email = new string('b', 128) + "@example.com",
            Active = true,
            Score = 99.5,
            Address = new StjAddress
            {
                Street = new string('S', 200),
                City = new string('C', 200),
                Zip = "95014",
            },
            Tags = [.. Enumerable.Range(0, 32).Select(i => new string('t', 24) + i)],
            Scores = [100, 95, 88, 92, 100, 73, 64, 51, 42, 37],
        };

        compact = JsonSerializer.SerializeToUtf8Bytes(
            user, new JsonSerializerOptions(StjContext.Default.Options) { WriteIndented = false });
        // Indented output inserts newlines + indentation between every token -> whitespace runs to skip.
        indented = JsonSerializer.SerializeToUtf8Bytes(
            user, new JsonSerializerOptions(StjContext.Default.Options) { WriteIndented = true });
    }

    [Benchmark]
    public User NoJsonSchema_Compact() => NoJsonBenchSerializer.Deserialize<User>(compact);

    [Benchmark(Baseline = true)]
    public StjUser SystemTextJson_Compact() => JsonSerializer.Deserialize(compact, StjContext.Default.StjUser)!;

    [Benchmark]
    public User NoJsonSchema_Indented() => NoJsonBenchSerializer.Deserialize<User>(indented);

    [Benchmark]
    public StjUser SystemTextJson_Indented() => JsonSerializer.Deserialize(indented, StjContext.Default.StjUser)!;
}
