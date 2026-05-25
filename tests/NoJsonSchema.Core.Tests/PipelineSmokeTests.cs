using NoJsonSchema.Core;
using Xunit;

namespace NoJsonSchema.Core.Tests;

public class PipelineSmokeTests
{
    [Fact]
    public void EmptySchema_StillEmitsNamespaceSerializerFile()
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate("{}", new GenerationOptions { Namespace = "Smoke" });
        Assert.Contains(result.Files, f => f.FileName == "SmokeSerializer.g.cs");
    }

    [Fact]
    public void SingleObjectSchema_EmitsThreeFiles()
    {
        const string schema = """
        {
          "$defs": {
            "Person": {
              "type": "object",
              "properties": { "name": { "type": "string" } }
            }
          }
        }
        """;
        var result = new GeneratorPipeline().Generate(schema, new GenerationOptions { Namespace = "Smoke" });

        Assert.Contains(result.Files, f => f.FileName == "SmokeSerializer.g.cs");
        Assert.Contains(result.Files, f => f.FileName == "Person.g.cs");
        Assert.Contains(result.Files, f => f.FileName == "PersonFormatter.g.cs");
    }
}
