using System.CommandLine;
using NoJsonSchema.Cli.Commands;

var root = new RootCommand("NoJsonSchema – generate zero-dependency C# parsers from JSON Schema.");
root.AddCommand(GenerateCommand.Build());
root.AddCommand(LintCommand.Build());

return await root.InvokeAsync(args);
