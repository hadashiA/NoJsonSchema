using System.CommandLine;
using NoJsonSchema.Core;

namespace NoJsonSchema.Cli.Commands;

static class GenerateCommand
{
    public static Command Build()
    {
        var inputOption = new Option<FileInfo>(
            aliases: ["--input", "-i"],
            description: "Path to the JSON Schema file.")
        {
            IsRequired = true,
        };

        var outputOption = new Option<DirectoryInfo>(
            aliases: ["--output", "-o"],
            description: "Directory to write generated .cs files into.")
        {
            IsRequired = true,
        };

        var namespaceOption = new Option<string>(
            aliases: ["--namespace", "-n"],
            description: "Namespace for generated types.",
            getDefaultValue: () => "Generated");

        var rootOption = new Option<string?>(
            aliases: ["--root-type"],
            description: "Optional root type name override.");

        var styleOption = new Option<TypeStyle>(
            aliases: ["--type-style"],
            description: "Type style: Class or Record.",
            getDefaultValue: () => TypeStyle.Class);

        var allofOption = new Option<AllOfStrategy>(
            aliases: ["--allof-strategy"],
            description: "How to represent allOf composition.",
            getDefaultValue: () => AllOfStrategy.Inherit);

        var strictExtra = new Option<bool>(
            aliases: ["--strict-extra"],
            description: "Treat unknown JSON properties as errors.",
            getDefaultValue: () => false);

        var command = new Command("generate", "Generate C# code from a JSON Schema.")
        {
            inputOption,
            outputOption,
            namespaceOption,
            rootOption,
            styleOption,
            allofOption,
            strictExtra,
        };

        command.SetHandler(async context =>
        {
            var input = context.ParseResult.GetValueForOption(inputOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var options = new GenerationOptions
            {
                Namespace = context.ParseResult.GetValueForOption(namespaceOption)!,
                RootTypeName = context.ParseResult.GetValueForOption(rootOption),
                TypeStyle = context.ParseResult.GetValueForOption(styleOption),
                AllOfStrategy = context.ParseResult.GetValueForOption(allofOption),
                StrictExtraProperties = context.ParseResult.GetValueForOption(strictExtra),
            };

            var schemaJson = await File.ReadAllTextAsync(input.FullName).ConfigureAwait(false);

            var pipeline = new GeneratorPipeline();
            var result = pipeline.Generate(schemaJson, options);

            Directory.CreateDirectory(output.FullName);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(output.FullName, file.FileName);
                await File.WriteAllTextAsync(path, file.SourceText).ConfigureAwait(false);
                Console.WriteLine($"wrote {path}");
            }

            Console.WriteLine($"generated {result.Files.Count} file(s).");
        });

        return command;
    }
}
