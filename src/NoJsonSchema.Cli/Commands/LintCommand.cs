using System.CommandLine;
using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.Cli.Commands;

static class LintCommand
{
    public static Command Build()
    {
        var inputOption = new Option<FileInfo>(
            aliases: ["--input", "-i"],
            description: "Path to the JSON Schema file.")
        {
            IsRequired = true,
        };

        var command = new Command("lint", "Parse a schema and report structural problems without generating code.")
        {
            inputOption,
        };

        command.SetHandler(async context =>
        {
            var input = context.ParseResult.GetValueForOption(inputOption)!;
            await using var stream = input.OpenRead();
            JsonSchemaDocument doc;
            try
            {
                doc = JsonSchemaLoader.Load(stream, input.FullName);
            }
            catch (SchemaLoadException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            Console.WriteLine($"file:        {doc.SourcePath}");
            Console.WriteLine($"$schema:     {doc.Dialect ?? "(none)"}");
            Console.WriteLine($"$id:         {doc.Id ?? "(none)"}");
            Console.WriteLine($"root kind:   {doc.Root.Kind}");
            Console.WriteLine($"definitions: {doc.Root.Defs.Count}");
            Console.WriteLine($"properties:  {doc.Root.Properties.Count} (on root)");
        });

        return command;
    }
}
