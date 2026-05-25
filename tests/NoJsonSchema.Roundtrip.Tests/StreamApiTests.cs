using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.Roundtrip.Tests;

public class StreamApiTests(ITestOutputHelper output)
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER"]);

    static (Assembly, GenerationResult) Compile(string schema, string ns)
    {
        var pipeline = new GeneratorPipeline();
        var result = pipeline.Generate(schema, new GenerationOptions { Namespace = ns });

        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, ParseOptions, f.FileName)).ToList();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "StreamGenerated_" + Guid.NewGuid().ToString("N"), trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder("Compilation failed:\n");
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) sb.AppendLine(d.ToString());
            foreach (var f in result.Files) { sb.AppendLine($"=== {f.FileName} ==="); sb.AppendLine(f.SourceText); }
            throw new InvalidOperationException(sb.ToString());
        }
        return (Assembly.Load(ms.ToArray()), result);
    }

    const string Schema = """
    {
      "$defs": {
        "User": {
          "type": "object",
          "properties": {
            "id":   { "type": "integer", "format": "int32" },
            "name": { "type": "string" }
          },
          "required": ["id", "name"]
        }
      }
    }
    """;

    [Fact]
    public void Stream_Sync_RoundTrips()
    {
        var (asm, _) = Compile(Schema, "StreamSync");
        var userType = asm.GetType("StreamSync.User")!;
        var formatter = asm.GetType("StreamSync.UserFormatter")!;
        var optsType = asm.GetType("StreamSync.NoJsonSerializerOptions")!;

        var instance = Activator.CreateInstance(userType)!;
        userType.GetProperty("Id")!.SetValue(instance, 7);
        userType.GetProperty("Name")!.SetValue(instance, "Hopper");

        var serialize = formatter.GetMethod("Serialize",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Stream), userType, optsType], null)!;
        using var memOut = new MemoryStream();
        serialize.Invoke(null, [memOut, instance, null]);

        var bytes = memOut.ToArray();
        output.WriteLine(Encoding.UTF8.GetString(bytes));
        Assert.Equal("{\"id\":7,\"name\":\"Hopper\"}", Encoding.UTF8.GetString(bytes));

        // And Deserialize(Stream)
        using var memIn = new MemoryStream(bytes);
        var deserialize = formatter.GetMethod("Deserialize",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Stream), optsType], null)!;
        var decoded = deserialize.Invoke(null, [memIn, null])!;
        Assert.Equal(7, userType.GetProperty("Id")!.GetValue(decoded));
        Assert.Equal("Hopper", userType.GetProperty("Name")!.GetValue(decoded));
    }

    [Fact]
    public async Task Stream_Async_RoundTrips()
    {
        var (asm, _) = Compile(Schema, "StreamAsync");
        var userType = asm.GetType("StreamAsync.User")!;
        var formatter = asm.GetType("StreamAsync.UserFormatter")!;
        var optsType = asm.GetType("StreamAsync.NoJsonSerializerOptions")!;

        var instance = Activator.CreateInstance(userType)!;
        userType.GetProperty("Id")!.SetValue(instance, 42);
        userType.GetProperty("Name")!.SetValue(instance, "Ada");

        var serializeAsync = formatter.GetMethod("SerializeAsync",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Stream), userType, optsType, typeof(CancellationToken)], null)!;
        using var memOut = new MemoryStream();
        // ValueTask is the return type; reflection gives us back a boxed ValueTask, await via task.AsTask().
        var task = (System.Threading.Tasks.ValueTask)serializeAsync.Invoke(null, [memOut, instance, null, CancellationToken.None])!;
        await task;

        var bytes = memOut.ToArray();
        Assert.Equal("{\"id\":42,\"name\":\"Ada\"}", Encoding.UTF8.GetString(bytes));

        using var memIn = new MemoryStream(bytes);
        var deserializeAsync = formatter.GetMethod("DeserializeAsync",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Stream), optsType, typeof(CancellationToken)], null)!;
        var resultBox = deserializeAsync.Invoke(null, [memIn, null, CancellationToken.None])!;
        // ValueTask<User> via reflection: convert to Task<object> via AsTask + cast.
        var asTaskMethod = resultBox.GetType().GetMethod("AsTask")!;
        var taskObj = (System.Threading.Tasks.Task)asTaskMethod.Invoke(resultBox, null)!;
        await taskObj;
        var decoded = taskObj.GetType().GetProperty("Result")!.GetValue(taskObj)!;
        Assert.Equal(42, userType.GetProperty("Id")!.GetValue(decoded));
        Assert.Equal("Ada", userType.GetProperty("Name")!.GetValue(decoded));
    }

    [Fact]
    public async Task DeserializeAsync_RespectsCancellation()
    {
        var (asm, _) = Compile(Schema, "StreamCancel");
        var formatter = asm.GetType("StreamCancel.UserFormatter")!;
        var optsType = asm.GetType("StreamCancel.NoJsonSerializerOptions")!;
        var deserializeAsync = formatter.GetMethod("DeserializeAsync",
            BindingFlags.Public | BindingFlags.Static, null,
            [typeof(Stream), optsType, typeof(CancellationToken)], null)!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A blocking stream so the cancellation has something to interrupt during CopyToAsync.
        using var slow = new SlowStream();
        var resultBox = deserializeAsync.Invoke(null, [slow, null, cts.Token])!;
        var task = (System.Threading.Tasks.Task)resultBox.GetType().GetMethod("AsTask")!.Invoke(resultBox, null)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void GenericSerializer_Stream_RoundTrips()
    {
        var (asm, _) = Compile(Schema, "StreamGen");
        var userType = asm.GetType("StreamGen.User")!;
        var serializer = asm.GetType("StreamGen.StreamGenSerializer")!;

        var instance = Activator.CreateInstance(userType)!;
        userType.GetProperty("Id")!.SetValue(instance, 3);
        userType.GetProperty("Name")!.SetValue(instance, "G");

        // Pick the Serialize<T>(Stream, T, NoJsonSerializerOptions?) overload.
        var serializeT = serializer.GetMethods()
            .First(m => m.Name == "Serialize"
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 3
                && m.GetParameters()[0].ParameterType == typeof(Stream))
            .MakeGenericMethod(userType);
        using var memOut = new MemoryStream();
        serializeT.Invoke(null, [memOut, instance, null]);
        var bytes = memOut.ToArray();
        Assert.Equal("{\"id\":3,\"name\":\"G\"}", Encoding.UTF8.GetString(bytes));

        using var memIn = new MemoryStream(bytes);
        var deserializeT = serializer.GetMethods()
            .First(m => m.Name == "Deserialize"
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(Stream))
            .MakeGenericMethod(userType);
        var decoded = deserializeT.Invoke(null, [memIn, null])!;
        Assert.Equal(3, userType.GetProperty("Id")!.GetValue(decoded));
    }

    sealed class SlowStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Block long enough for an already-cancelled token to bite.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
