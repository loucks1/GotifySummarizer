using System.Text.Json.Serialization;

public class GotifyMessagesResponse
{
    public List<GotifyMessage> Messages { get; set; } = new();
    public GotifyPaging Paging { get; set; } = new();
}

public class GotifyPaging
{
    public int Size { get; set; }
    public long Since { get; set; }
    public long? Limit { get; set; }
    public string? Next { get; set; }
}

public class GotifyMessage
{
    public long Id { get; set; }

    [JsonPropertyName("appid")]
    public int AppId { get; set; }           // Gotify returns "appid", not "app"

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Priority { get; set; }

    public string Date { get; set; } = string.Empty;   // Keep as string (ISO format)
    public string Digest { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime DateParsed => DateTime.TryParse(Date, out var dt) ? dt : DateTime.UtcNow;

    public Dictionary<string, object>? Extras { get; set; }
}

public class IgnorableMessage
{
    public string Subject { get; set; } = string.Empty;
    public string? detailRegex { get; set; } = string.Empty;
}

public class AppRule
{
    public int? AppId { get; set; }                    // Changed to match real API

    public IgnorableMessage[] IgnorableMessages { get; set; } = Array.Empty<IgnorableMessage>();
}