using System.Text;
using ResolveAI.Contracts;

namespace ResolveAI.App.Services;

/// <summary>
/// Posts to a Microsoft Teams channel webhook. The URL starts as TEAMS_WEBHOOK_URL and can be replaced or switched off from the
/// Settings page (<see cref="Configure"/>), so it is read on every send.
/// </summary>
public sealed class TeamsNotifier(HttpClient http, string? webhookUrl)
{
    private readonly HttpClient _http = http;
    private volatile string? _webhookUrl = Clean(webhookUrl);
    private volatile bool _on = true;

    public bool Enabled => _on && _webhookUrl is not null;
    public string? Url => _webhookUrl;
    public bool On => _on;

    public void Configure(string? url, bool on)
    {
        _webhookUrl = Clean(url);
        _on = on;
    }

    private static string? Clean(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    /// <summary>Enough to recognise the webhook ("prod-12.westus…logic.azure.com/…Ab12cd"), not enough to post to it.</summary>
    public static string Hint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "••••";
        var host = u.Host.Length > 24 ? $"{u.Host[..10]}…{u.Host[^14..]}" : u.Host;
        return url.Length < 24 ? host : $"{host}/…{url[^6..]}";
    }

    /// <summary>Sends a small "test" card to <paramref name="url"/>; throws with the reason if Teams does not accept it.</summary>
    public async Task SendTest(string url, CancellationToken ct = default)
    {
        var card = Card(
            new { type = "TextBlock", size = "Medium", weight = "Bolder", text = "ResolveAI test message", wrap = true },
            new { type = "TextBlock", text = "Teams notifications are connected. You'll get a message here when an investigation completes.", wrap = true },
            new { type = "TextBlock", spacing = "Small", isSubtle = true, text = DateTimeOffset.UtcNow.ToString("u"), wrap = true });
        await Post(url, card, ct);
    }

    private static Dictionary<string, object?> Card(params object[] body) => new()
    {
        ["type"] = "AdaptiveCard",
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["version"] = "1.4",
        ["body"] = body,
    };

    private async Task Post(string url, object card, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Json.Serialize(card), Encoding.UTF8, "application/json"),
        };
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Teams returned {(int)res.StatusCode}: {(error.Length > 200 ? error[..200] + "…" : error)}");
        }
    }

    public async Task NotifyInvestigationCompleted(Incident incident, Investigation investigation, CancellationToken ct = default)
    {
        if (!Enabled) return;

        var top = investigation.Hypotheses.FirstOrDefault();
        var title = top?.Title ?? "No hypothesis generated";
        var confidence = top?.Confidence ?? "n/a";
        var status = investigation.Status;
        var summary = string.IsNullOrWhiteSpace(investigation.Summary) ? "(no summary)" : investigation.Summary;

        var summaryText = $"""
ResolveAI investigation completed
Incident: {incident.Key} - {incident.Title}
Service: {incident.ServiceName}
Severity: {incident.Severity}
Mode: {investigation.LlmMode}
Model: {investigation.ModelName ?? "recorded"}
Status: {status}
Top finding: {title} (confidence: {confidence})
Summary: {summary}
""";

        static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

        var card = new Dictionary<string, object?>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = new object[]
            {
                new { type = "TextBlock", size = "Medium", weight = "Bolder", text = "ResolveAI investigation completed", wrap = true },
                new { type = "TextBlock", text = $"Incident: {incident.Key} - {Clip(incident.Title, 200)}", wrap = true },
                new { type = "FactSet", facts = new object[]
                    {
                        new { title = "Service", value = Clip(incident.ServiceName, 100) },
                        new { title = "Severity", value = incident.Severity },
                        new { title = "Mode", value = investigation.LlmMode },
                        new { title = "Model", value = investigation.ModelName ?? "recorded" },
                        new { title = "Status", value = status },
                        new { title = "Top finding", value = Clip(title, 200) },
                        new { title = "Confidence", value = confidence },
                    }
                },
                new { type = "TextBlock", weight = "Bolder", text = "Summary", wrap = true },
                new { type = "TextBlock", text = Clip(summary, 1500), wrap = true },
                new { type = "TextBlock", spacing = "Small", isSubtle = true, text = DateTimeOffset.UtcNow.ToString("u"), wrap = true },
            }
        };

        await Post(_webhookUrl!, card, ct);
    }
}
