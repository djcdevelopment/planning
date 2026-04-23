using System.Text.Json;
using System.Text.Json.Nodes;
using Farmer.Host.Services;
using Xunit;

namespace Farmer.Tests.Integration;

/// <summary>
/// Phase Demo v2 Stream 3 — coverage for the X-Farmer-User-Id header / body
/// merge precedence on <c>POST /trigger</c>. The helper is small but the
/// contract matters: body wins, header is a convenience, malformed inputs
/// fall through untouched.
/// </summary>
public class TriggerBodyEnricherTests
{
    [Fact]
    public void Merge_AddsUserId_WhenBodyHasNone()
    {
        var input = """{"work_request_name":"demo","source":"api"}""";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "alice-oid");

        var parsed = JsonNode.Parse(out1)!.AsObject();
        Assert.Equal("alice-oid", (string?)parsed["user_id"]);
        Assert.Equal("demo", (string?)parsed["work_request_name"]);
    }

    [Fact]
    public void Merge_BodyWins_WhenBothPresent()
    {
        // Precedence: the body's user_id beats the header. The header is a
        // curl convenience; the JSON contract is the canonical source.
        var input = """{"user_id":"body-wins","work_request_name":"demo"}""";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "header-alice");

        var parsed = JsonNode.Parse(out1)!.AsObject();
        Assert.Equal("body-wins", (string?)parsed["user_id"]);
    }

    [Fact]
    public void Merge_EmptyHeader_IsNoOp()
    {
        var input = """{"work_request_name":"demo"}""";
        Assert.Equal(input, TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, null));
        Assert.Equal(input, TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, ""));
        Assert.Equal(input, TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "   "));
    }

    [Fact]
    public void Merge_EmptyBody_YieldsUserIdOnlyObject()
    {
        // Empty body + header = a minimal JSON object carrying just the user_id.
        // Downstream deserializer (InboxTrigger -> RunRequest) then fills in
        // the usual defaults (source = "inbox", etc.).
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody("", "alice-oid");
        var parsed = JsonNode.Parse(out1)!.AsObject();
        Assert.Equal("alice-oid", (string?)parsed["user_id"]);
    }

    [Fact]
    public void Merge_BlankUserIdInBody_TreatedAsMissing_HeaderFillsIn()
    {
        // Belt-and-suspenders: a client that sends "user_id": "" or whitespace
        // gets the header value filled in. The alternative (treat the empty
        // string as "explicit opt-out") is strictly worse for the demo use case.
        var input = """{"user_id":"  ","work_request_name":"demo"}""";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "alice-oid");
        var parsed = JsonNode.Parse(out1)!.AsObject();
        Assert.Equal("alice-oid", (string?)parsed["user_id"]);
    }

    [Fact]
    public void Merge_MalformedBody_ReturnedUntouched()
    {
        // Don't swallow the client's parse error — let the downstream
        // deserializer surface it with the exact same bytes.
        var input = "{not valid json";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "alice-oid");
        Assert.Equal(input, out1);
    }

    [Fact]
    public void Merge_NonObjectBody_ReturnedUntouched()
    {
        // A JSON array or scalar at the root is wire-level wrong for /trigger,
        // but we still don't mutate it — the deserializer can complain with
        // an accurate message.
        var input = "[1,2,3]";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "alice-oid");
        Assert.Equal(input, out1);
    }

    [Fact]
    public void Merge_PreservesOtherFields()
    {
        // Round-trip sanity: shape of the original body is preserved
        // verbatim after the merge.
        var input = """{"work_request_name":"demo","prompts_inline":[{"filename":"1.md","content":"x"}],"source":"phone"}""";
        var out1 = TriggerBodyEnricher.MergeHeaderUserIdIntoBody(input, "alice-oid");
        var parsed = JsonSerializer.Deserialize<JsonElement>(out1);
        Assert.Equal("demo", parsed.GetProperty("work_request_name").GetString());
        Assert.Equal("alice-oid", parsed.GetProperty("user_id").GetString());
        Assert.Equal("phone", parsed.GetProperty("source").GetString());
        Assert.Equal(1, parsed.GetProperty("prompts_inline").GetArrayLength());
    }
}
