using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary>One NDJSON line of <c>GET /events</c>; <c>status</c>/<c>id</c>/<c>from</c> are the legacy aliases.</summary>
public sealed class EventMessage
{
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public EventActor Actor { get; set; } = new();

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "local";

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("timeNano")]
    public long TimeNano { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }
}

/// <summary><c>EventMessage.Actor</c>.</summary>
public sealed class EventActor
{
    public string ID { get; set; } = "";
    public Dictionary<string, string> Attributes { get; set; } = [];
}
