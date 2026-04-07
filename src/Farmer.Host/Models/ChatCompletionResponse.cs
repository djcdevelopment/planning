using System.Text.Json.Serialization;

namespace Farmer.Host.Models;

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "farmer-v1";

    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice> Choices { get; set; } = [];

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    public static ChatCompletionResponse ForRun(string runId) => new()
    {
        Id = $"farmer-{runId}",
        RunId = runId,
        Choices =
        [
            new ChatCompletionChoice
            {
                Index = 0,
                Message = new ChatMessage
                {
                    Role = "assistant",
                    Content = $"Workflow started. Run ID: {runId}"
                },
                FinishReason = "stop"
            }
        ]
    };
}

public sealed class ChatCompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";
}

// ChatMessage is defined in ChatCompletionRequest.cs (same namespace)
