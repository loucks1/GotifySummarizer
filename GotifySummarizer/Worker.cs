using GotifySummarizer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Net.Mime.MediaTypeNames;

namespace GotifySummarizer
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly GotifyOptions _options;
        private readonly HttpClient _httpClient;
        private readonly List<AppRule> _rules;
        private readonly TimeZoneInfo centralTZI = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        public Worker(ILogger<Worker> logger, IOptions<GotifyOptions> options)
        {
            _logger = logger;
            _options = options.Value;
            _httpClient = new HttpClient();

            _rules = string.IsNullOrWhiteSpace(_options.AppRulesJson)
                ? new List<AppRule>()
                : JsonSerializer.Deserialize<List<AppRule>>(_options.AppRulesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? new List<AppRule>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Gotify Daily Summarizer (per-day window) starting...");

            // On startup: Process all historical complete days + current day only if past 6:01 AM
            await ProcessMessagesAsync(stoppingToken);

            // Then schedule daily runs at 6:01 AM UTC
            while (!stoppingToken.IsCancellationRequested)
            {
                var nextRun = GetNextRunTimeAtSixOneAM();
                _logger.LogInformation("Next daily summary scheduled for {time} UTC", nextRun);

                await Task.Delay(nextRun - DateTime.UtcNow, stoppingToken);

                await ProcessMessagesAsync(stoppingToken);
            }
        }

        // ====================== DAILY SCHEDULED RUN ======================
        private DateTime GetNextRunTimeAtSixOneAM()
        {
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, centralTZI).DateTime;

            var nextRunLocal = nowLocal.Date.AddHours(6).AddMinutes(1);  // Today at 6:01 AM

            if (nowLocal > nextRunLocal)
                nextRunLocal = nextRunLocal.AddDays(1);   // Tomorrow at 6:01 AM

            return TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, centralTZI);
        }

        private class DataStructure
        {
            public int ignoreCount = 0;
            public List<GotifyMessage> deletableMessages = new List<GotifyMessage>();
        }

        // ====================== CORE PROCESSING ======================
        private async Task ProcessMessagesAsync(CancellationToken ct)
        {
            try
            {
                var byDay = new Dictionary<DateTime, DataStructure>();

                var allMessagesTask = GetAllMessagesAsync(ct);
                var appNamesTask = GetAppNamesAndIdsAsync();

                List<GotifyMessage>? messagesInWindow = null;

                var allMessages = await allMessagesTask;

                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, centralTZI).DateTime;
                if (nowLocal.Hour < 6)
                    messagesInWindow = [.. allMessages.Where(m => DateTime.TryParse(m.Date, out var msgTime) && msgTime < nowLocal.Date)];

                foreach (var msg in messagesInWindow ?? allMessages)
                {
                    var rule = FindRuleForMessage(msg);
                    if (rule == null) continue;

                    DateTime.TryParse(msg.Date, out var msgTime);
                    msgTime = TimeZoneInfo.ConvertTime(msgTime.ToUniversalTime(), centralTZI);

                    var targetDate = msgTime.Date;
                    if (!byDay.ContainsKey(targetDate))
                        byDay[targetDate] = new DataStructure();

                    var dataStructure = byDay[targetDate];

                    bool isIgnorable = false;

                    foreach (var ignorableMessage in rule.IgnorableMessages)
                    {
                        var canIgnore = msg.Title.Contains(ignorableMessage.Subject);
                        if (canIgnore && !String.IsNullOrEmpty(ignorableMessage.detailRegex))
                        {
                            msg.Digest += ExtractDetailsSection(msg.Message, ignorableMessage.detailRegex);
                        }
                        isIgnorable |= canIgnore;
                    }
                    if (isIgnorable)
                    {
                        dataStructure.deletableMessages.Add(msg);
                    }
                    else
                    {
                        dataStructure.ignoreCount++;
                    }
                }

                foreach (var group in byDay.OrderByDescending(g => g.Key))
                {
                    _logger.LogInformation("Day {date} summary: Deletable Messages={s} | Ignored Messages={f}",
                         group.Key.ToString("yyyy-MM-dd"), group.Value.deletableMessages.Count, group.Value.ignoreCount);

                    await SendDailySummaryAsync(group.Key, group.Value, await appNamesTask, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during message processing.");
            }
        }

        private async Task<List<GotifyMessage>> GetAllMessagesAsync(CancellationToken ct)
        {
            var allMessages = new List<GotifyMessage>();

            var url = $"{_options.BaseUrl}/message?token={_options.ClientToken}&limit=200";

            do
            {
                var response = await _httpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<GotifyMessagesResponse>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.Messages != null)
                    allMessages.AddRange(result.Messages);

                url = result?.Paging?.Next;

            } while (!string.IsNullOrEmpty(url));


            return allMessages;
        }

        private AppRule? FindRuleForMessage(GotifyMessage msg)
        {
            return _rules.FirstOrDefault(r => r.AppId.HasValue && r.AppId == msg.AppId);
        }

        private async Task DeleteMessageAsync(IEnumerable<long> id, CancellationToken ct)
        {
            try
            {
                if (_options.PerformDelete)
                {
                    var tasks = id.Select(i => _httpClient.DeleteAsync($"{_options.BaseUrl}/message/{i}?token={_options.ClientToken}", ct));
                    await Task.WhenAll(tasks);
                    _logger.LogInformation("Deleted messages: {id}", string.Join(',', id));
                }
                else
                {
                    _logger.LogInformation("PerformDelete is false. Skipping actual deletion of messages: {id}", string.Join(',', id));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete all messages {id}", string.Join(',', id));
            }
        }

        private async Task SendDailySummaryAsync(DateTime date, DataStructure dataStructure, Dictionary<int, string> appNames, CancellationToken ct)
        {
            var orderedMessages = dataStructure.deletableMessages.OrderBy(ds => ds.AppId);
            var tableRows = orderedMessages.Select(m => $"| {appNames[m.AppId]} | {EscapeMarkdown(m.Title)} |");

            var markdown = $"""
                # Daily Summary for {date:yyyy-MM-dd} (00:00–06:00 UTC)

                **Total Deleted:** {dataStructure.deletableMessages.Count}  
                **Total Ignored:** {dataStructure.ignoreCount}  

                ### Deleted Message Subjects
                | Application | Subject |
                |-------------|---------|
                {string.Join("\n", tableRows)}
                """;

            foreach (var msg in orderedMessages.Where(om => !string.IsNullOrEmpty(om.Digest)))
            {
                markdown += $"\n\n{appNames[msg.AppId]}\n";
                markdown += $"```\n{msg.Digest}\n```";
            }

            var payload = new
            {
                title = $"Daily Summary – {date:yyyy-MM-dd}",
                message = markdown,
                priority = 5,
                extras = new Dictionary<string, object>
                {
                    ["client::display"] = new { contentType = "text/markdown" }
                }
            };

            var url = $"{_options.BaseUrl}/message?token={_options.SummaryAppToken}";
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Daily summary for {date} sent successfully.", date.ToString("yyyy-MM-dd"));
                await DeleteMessageAsync(dataStructure.deletableMessages.Select(m => m.Id), ct);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Failed to send summary for {date}. Status: {status} - {error}",
                    date.ToString("yyyy-MM-dd"), response.StatusCode, error);
            }
        }

        private static string EscapeMarkdown(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace("|", "\\|");
        }

        public static string ExtractDetailsSection(string fullLog, string regex)
        {
            if (string.IsNullOrWhiteSpace(fullLog))
                return string.Empty;

            var match = Regex.Match(fullLog, regex);

            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private async Task<Dictionary<int, string>> GetAppNamesAndIdsAsync()
        {
            using var client = new HttpClient();

            client.DefaultRequestHeaders.Add("X-Gotify-Key", _options.ClientToken);

            var response = await client.GetAsync($"{_options.BaseUrl.TrimEnd('/')}/application");

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var apps = JsonSerializer.Deserialize<List<GotifyApplication>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return apps.ToDictionary(a => a.Id, a => a.Name);
        }
    }
}