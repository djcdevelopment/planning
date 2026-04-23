using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farmer.Host.Services;

/// <summary>
/// Phase Demo v2 Stream 3 — pure helpers for mutating the raw <c>/trigger</c>
/// JSON body before it's handed to the <see cref="RetryDriver"/>. Lives as a
/// separate class (not inline in Program.cs) so the merge precedence is
/// unit-testable without bootstrapping a web host.
/// </summary>
public static class TriggerBodyEnricher
{
    /// <summary>
    /// Splice an <c>X-Farmer-User-Id</c> header value into the JSON body's
    /// <c>user_id</c> field ONLY when the body doesn't already carry one.
    /// <para>
    /// Precedence: body wins. The header is a convenience for curl; the JSON
    /// contract is the canonical source. When both are present, the header is
    /// silently ignored so clients that think they're sending identity on the
    /// body actually do.
    /// </para>
    /// <para>
    /// Malformed / non-object bodies are returned untouched — the downstream
    /// deserializer will surface the same parse error it would have without
    /// the header. Don't swallow a client's bug.
    /// </para>
    /// </summary>
    public static string MergeHeaderUserIdIntoBody(string body, string? headerUserId)
    {
        if (string.IsNullOrWhiteSpace(headerUserId)) return body;
        if (string.IsNullOrWhiteSpace(body)) body = "{}";

        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject obj) return body;

            // Body already has a non-empty user_id -> body wins.
            if (obj.TryGetPropertyValue("user_id", out var existing)
                && existing is JsonValue v
                && v.TryGetValue<string>(out var s)
                && !string.IsNullOrWhiteSpace(s))
            {
                return body;
            }

            obj["user_id"] = headerUserId;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
