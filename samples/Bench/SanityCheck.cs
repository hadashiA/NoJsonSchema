using System.Text;
using System.Text.Json;
using NoJsonBench;
using StjBench;

namespace NoJsonSchema.Benchmarks;

static class SanityCheck
{
    public static void Run()
    {
        var u = new User
        {
            Id = 42,
            Name = "Ada",
            Email = "ada@example.com",
            Active = true,
            Score = 99.5,
            Address = new Address { Street = "1 Loop", City = "Cupertino", Zip = "95014" },
            Tags = ["x", "y"],
            Scores = [1, 2, 3],
        };
        var s = new StjUser
        {
            Id = 42, Name = "Ada", Email = "ada@example.com", Active = true, Score = 99.5,
            Address = new StjAddress { Street = "1 Loop", City = "Cupertino", Zip = "95014" },
            Tags = ["x", "y"], Scores = [1, 2, 3],
        };

        var njs = Encoding.UTF8.GetString(NoJsonBenchSerializer.SerializeToUtf8Bytes(u));
        var stj = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(s, StjContext.Default.StjUser));
        Console.WriteLine("NoJsonSchema: " + njs);
        Console.WriteLine("STJ        : " + stj);

        var njsBack = NoJsonBenchSerializer.Deserialize<User>(Encoding.UTF8.GetBytes(njs));
        var stjBack = JsonSerializer.Deserialize(stj, StjContext.Default.StjUser);
        Console.WriteLine($"NJS  decode: id={njsBack.Id}, addr.city={njsBack.Address?.City}");
        Console.WriteLine($"STJ  decode: id={stjBack!.Id}, addr.city={stjBack.Address?.City}");
    }
}
