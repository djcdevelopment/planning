using System.Text.Json.Serialization;

namespace Farmer.Host.Models;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "farmer-v1";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
