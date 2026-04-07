public class GotifyOptions
{
    public string BaseUrl { get; set; } = "http://gotify";
    public string ClientToken { get; set; } = string.Empty;      // Client token (for reading + deleting)
    public string SummaryAppToken { get; set; } = string.Empty;  // Application token to SEND the summary to
    public string AppRulesJson { get; set; } = "[]";             // JSON array of rules (see below)
    public bool PerformDelete { get; set; } = false;              // Whether to actually perform deletions or just log them   
}