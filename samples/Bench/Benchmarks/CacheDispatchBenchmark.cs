using System.Text.Json;
using BenchmarkDotNet.Attributes;
using NoJsonBench;

namespace NoJsonSchema.Benchmarks;

/// <summary>
/// Verifies the <c>Cache&lt;T&gt;</c> dispatch path is competitive with calling the per-type
/// Formatter directly. Direct = no interface call; Generic = one interface call after a single
/// static field read. The gap should be tiny (a few ns) because the JIT specialises
/// <c>Cache&lt;T&gt;</c> per generic instantiation.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CacheDispatchBenchmark
{
    byte[] bytes = default!;
    User user = default!;

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
        bytes = UserFormatter.SerializeToUtf8Bytes(user);
    }

    [Benchmark(Baseline = true)]
    public User DeserializeDirect() => UserFormatter.Deserialize(bytes);

    [Benchmark]
    public User DeserializeGeneric() => NoJsonBenchSerializer.Deserialize<User>(bytes);

    [Benchmark]
    public byte[] SerializeDirect() => UserFormatter.SerializeToUtf8Bytes(user);

    [Benchmark]
    public byte[] SerializeGeneric() => NoJsonBenchSerializer.SerializeToUtf8Bytes(user);
}
