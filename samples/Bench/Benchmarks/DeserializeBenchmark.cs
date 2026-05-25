using System.Text.Json;
using BenchmarkDotNet.Attributes;
using NoJsonBench;
using StjBench;

namespace NoJsonSchema.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class DeserializeBenchmark
{
    byte[] bytes = default!;

    [GlobalSetup]
    public void Setup()
    {
        var stj = new StjUser
        {
            Id = 42,
            Name = "Ada Lovelace",
            Email = "ada@example.com",
            Active = true,
            Score = 99.5,
            Address = new StjAddress { Street = "1 Infinite Loop", City = "Cupertino", Zip = "95014" },
            Tags = ["math", "engineering", "punchcards"],
            Scores = [100, 95, 88, 92, 100],
        };
        bytes = JsonSerializer.SerializeToUtf8Bytes(stj, StjContext.Default.StjUser);
    }

    [Benchmark]
    public User NoJsonSchema() => UserFormatter.Deserialize(bytes);

    [Benchmark(Baseline = true)]
    public StjUser SystemTextJson() => JsonSerializer.Deserialize(bytes, StjContext.Default.StjUser)!;
}
