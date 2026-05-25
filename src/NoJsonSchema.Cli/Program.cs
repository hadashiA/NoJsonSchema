using ConsoleAppFramework;
using NoJsonSchema.Cli;

// ConsoleAppFramework's source generator wires up parsing for every public method on the registered
// type. Commands resolve by method name (auto-kebab-cased): Generate -> "generate", Lint -> "lint".
// `Task<int>` return values propagate as the process exit code via Environment.ExitCode.
var app = ConsoleApp.Create();
app.Add<NoJsonSchemaCommands>();
await app.RunAsync(args).ConfigureAwait(false);
return Environment.ExitCode;
