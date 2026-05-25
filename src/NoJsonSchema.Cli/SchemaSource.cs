using System.Net.Http;
using System.Net.Http.Headers;

namespace NoJsonSchema.Cli;

/// <summary>
/// Resolves <c>--input</c> values that may be either a local file path or an http(s) URL.
/// HTTP responses are checked for <c>2xx</c> + treated as text regardless of <c>Content-Type</c>
/// (some servers serve schemas as <c>application/octet-stream</c> or <c>text/plain</c>).
/// </summary>
static class SchemaSource
{
    // Reused across the (typically single) call site — HttpClient is safe to share, and creating
    // one per invocation would leak sockets if the CLI is reused as a long-running tool.
    static readonly HttpClient Http = CreateClient();

    public static async Task<string> ReadAsync(string input, CancellationToken cancellationToken)
    {
        if (IsHttpUrl(input))
        {
            using var resp = await Http.GetAsync(input, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return await File.ReadAllTextAsync(input, cancellationToken).ConfigureAwait(false);
    }

    static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoJsonSchema", "0.1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/schema+json"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
        return c;
    }
}
