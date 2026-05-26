using System.Text;
using Dap;
using Xunit;
using Xunit.Abstractions;

namespace NoJsonSchema.DapIntegration.Tests;

/// <summary>
/// End-to-end checks against the real Debug Adapter Protocol schema
/// (<c>samples/Dap/debugAdapterProtocol.json</c>, 192 definitions, ~250 generated types).
/// We pick a handful of well-known messages and confirm they serialize / deserialize cleanly.
/// </summary>
public class DapRoundtripTests(ITestOutputHelper output)
{
    [Fact]
    public void InitializeRequest_Serialize_ProducesExpectedJson()
    {
        var req = new InitializeRequest
        {
            Seq = 1,
            Type = "request",
            Command = "initialize",
            Arguments = new InitializeRequestArguments
            {
                AdapterID = "vscode-mock",
                ClientID = "vscode",
                ClientName = "VS Code",
                Locale = "en-US",
                LinesStartAt1 = true,
                ColumnsStartAt1 = true,
                PathFormat = "path",
            },
        };

        var bytes = DapSerializer.SerializeToUtf8Bytes(req);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);

        Assert.Contains("\"seq\":1", json);
        Assert.Contains("\"type\":\"request\"", json);
        Assert.Contains("\"command\":\"initialize\"", json);
        Assert.Contains("\"adapterID\":\"vscode-mock\"", json);
        Assert.Contains("\"pathFormat\":\"path\"", json);
    }

    [Fact]
    public void InitializeRequest_RoundTrip_PreservesAllFields()
    {
        var req = new InitializeRequest
        {
            Seq = 7,
            Type = "request",
            Command = "initialize",
            Arguments = new InitializeRequestArguments
            {
                AdapterID = "mock",
                LinesStartAt1 = false,
                SupportsRunInTerminalRequest = true,
                SupportsVariableType = true,
            },
        };

        var bytes = DapSerializer.SerializeToUtf8Bytes(req);
        var roundtrip = DapSerializer.Deserialize<InitializeRequest>(bytes);

        Assert.Equal(7, roundtrip.Seq);
        Assert.Equal("request", roundtrip.Type);
        Assert.Equal("initialize", roundtrip.Command);
        Assert.NotNull(roundtrip.Arguments);
        Assert.Equal("mock", roundtrip.Arguments!.AdapterID);
        Assert.Equal(false, roundtrip.Arguments.LinesStartAt1);
        Assert.Equal(true, roundtrip.Arguments.SupportsRunInTerminalRequest);
        Assert.Equal(true, roundtrip.Arguments.SupportsVariableType);
    }

    [Fact]
    public void StoppedEvent_RoundTrips()
    {
        var ev = new StoppedEvent
        {
            Seq = 42,
            Type = "event",
            EventValue = "stopped",
            Body = new StoppedEventBody
            {
                Reason = "breakpoint",
                Description = "Paused on breakpoint",
                ThreadId = 1,
                AllThreadsStopped = true,
                HitBreakpointIds = (int[])[101, 102],
            },
        };

        var bytes = DapSerializer.SerializeToUtf8Bytes(ev);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);

        Assert.Contains("\"reason\":\"breakpoint\"", json);
        Assert.Contains("\"threadId\":1", json);
        Assert.Contains("\"hitBreakpointIds\":[101,102]", json);

        var roundtrip = DapSerializer.Deserialize<StoppedEvent>(bytes);
        Assert.Equal("event", roundtrip.Type);
        Assert.Equal("stopped", roundtrip.EventValue);
        Assert.NotNull(roundtrip.Body);
        Assert.Equal("breakpoint", roundtrip.Body!.Reason);
        Assert.Equal(1, roundtrip.Body.ThreadId);
        Assert.Equal(true, roundtrip.Body.AllThreadsStopped);
        Assert.Equal<int[]>([101, 102], roundtrip.Body.HitBreakpointIds!);
    }

    [Fact]
    public void StackTraceResponse_WithStackFrames_RoundTrips()
    {
        var resp = new StackTraceResponse
        {
            Seq = 5,
            Type = "response",
            RequestSeq = 3,
            Success = true,
            Command = "stackTrace",
            Body = new StackTraceResponseBody
            {
                StackFrames =
                [
                    new StackFrame
                    {
                        Id = 1,
                        Name = "Program.Main",
                        Line = 10L,
                        Column = 5L,
                        Source = new Source { Name = "Program.cs", Path = "/work/Program.cs" },
                    },
                    new StackFrame
                    {
                        Id = 2,
                        Name = "Foo.Bar",
                        Line = 25L,
                        Column = 1L,
                    },
                ],
                TotalFrames = 2u,
            },
        };

        var bytes = DapSerializer.SerializeToUtf8Bytes(resp);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);

        Assert.Contains("\"command\":\"stackTrace\"", json);
        Assert.Contains("\"Program.Main\"", json);
        Assert.Contains("\"path\":\"/work/Program.cs\"", json);

        var roundtrip = DapSerializer.Deserialize<StackTraceResponse>(bytes);
        Assert.True(roundtrip.Success);
        Assert.NotNull(roundtrip.Body);
        Assert.Equal(2, roundtrip.Body!.StackFrames.Length);
        Assert.Equal("Program.Main", roundtrip.Body.StackFrames[0].Name);
        Assert.Equal("/work/Program.cs", roundtrip.Body.StackFrames[0].Source!.Path);
    }

    [Fact]
    public void ClosedEnum_ChecksumAlgorithm_RoundTrips()
    {
        // DAP has a closed enum: ChecksumAlgorithm = { MD5, SHA1, SHA256, "timestamp" }
        // "checksum" inside type Checksum collides with the type name, so the property is renamed ChecksumValue.
        var c = new Checksum { Algorithm = ChecksumAlgorithm.SHA256, ChecksumValue = "abc123" };
        var bytes = DapSerializer.SerializeToUtf8Bytes(c);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);

        Assert.Contains("\"algorithm\":\"SHA256\"", json);
        Assert.Contains("\"checksum\":\"abc123\"", json);

        var roundtrip = DapSerializer.Deserialize<Checksum>(bytes);
        Assert.Equal(ChecksumAlgorithm.SHA256, roundtrip.Algorithm);
        Assert.Equal("abc123", roundtrip.ChecksumValue);
    }

    [Fact]
    public void Generic_GraphSerializer_DispatchesByType()
    {
        // Verify the namespace-wide DapSerializer<T> dispatch picks the right formatter.
        var capabilities = new Capabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsConditionalBreakpoints = true,
        };

        var bytes = DapSerializer.SerializeToUtf8Bytes(capabilities);
        var json = Encoding.UTF8.GetString(bytes);
        output.WriteLine(json);
        Assert.Contains("\"supportsConfigurationDoneRequest\":true", json);

        var roundtrip = DapSerializer.Deserialize<Capabilities>(bytes);
        Assert.Equal(true, roundtrip.SupportsConfigurationDoneRequest);
        Assert.Equal(true, roundtrip.SupportsConditionalBreakpoints);
    }
}
