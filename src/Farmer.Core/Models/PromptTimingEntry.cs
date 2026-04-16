using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

/// <summary>
/// One line of <c>output/per-prompt-timing.jsonl</c>, written by worker.sh after
/// each prompt completes. Parsed by CollectStage on the host and reconstructed
/// into back-dated OTel spans so the Jaeger waterfall shows per-prompt detail
/// inside the otherwise-opaque <c>workflow.stage.Dispatch</c> window.
/// </summary>
public sealed class PromptTimingEntry
{
    [JsonPropertyName("prompt_index")]
    public int PromptIndex { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("start_ts")]
    public DateTimeOffset StartTs { get; set; }

    [JsonPropertyName("end_ts")]
    public DateTimeOffset EndTs { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdout_bytes")]
    public long StdoutBytes { get; set; }

    [JsonPropertyName("stderr_bytes")]
    public long StderrBytes { get; set; }
}
