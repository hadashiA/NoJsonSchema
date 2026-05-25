using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using NoJsonSchema.Benchmarks;

if (args.Length > 0 && args[0] == "--sanity")
{
    SanityCheck.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, DefaultConfig.Instance);
