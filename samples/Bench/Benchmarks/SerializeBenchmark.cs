using System.Text.Json;
using BenchmarkDotNet.Attributes;
using NoJsonBench;
using StjBench;

namespace NoJsonSchema.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SerializeBenchmark
{
    User user = default!;
    StjUser stj = default!;

    [GlobalSetup]
    public void Setup()
    {
        user = new User
        {
            Id = 42,
            Name = "Ada Lovelace",
            Email = "ada@example.com",
            Active = true,
            Score = 99.5,
            Address = new Address { Street = "1 Infinite Loop", City = "Cupertino", Zip = "95014" },
            Tags = ["math", "engineering", "punchcards"],
            Scores = [100, 95, 88, 92, 100],
        };
        stj = new StjUser
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
    }

    [Benchmark]
    public byte[] NoJsonSchema() => NoJsonBenchSerializer.SerializeToUtf8Bytes(user);

    [Benchmark(Baseline = true)]
    public byte[] SystemTextJson() => JsonSerializer.SerializeToUtf8Bytes(stj, StjContext.Default.StjUser);
}
