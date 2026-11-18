using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using ResolveAI.App.Data;
using ResolveAI.App.Llm;

namespace ResolveAI.App.Services;

/// <summary>What the Settings page shows about the active model. Never contains the key.</summary>
public sealed record LlmStatus(string Mode, string? Provider, string? Model, string? KeyHint, string Source, string? TestedAt, string? Warning);

/// <summary>The outcome of Settings → Test connection.</summary>
public sealed record LlmTestResult(bool Ok, string Message, long InputTokens, long OutputTokens, bool Estimated, long LatencyMs);

/// <summary>Token totals for one key.</summary>
public sealed class LlmUsageRow
{
    public string KeyHint { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Model { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TodayInputTokens { get; set; }
    public long TodayOutputTokens { get; set; }
    public bool Estimated { get; set; }
    public string LastUsed { get; set; } = "";
}

public static class LlmKeys
{
    public static string Fingerprint(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))[..16].ToLowerInvariant();

    /// <summary>Enough to recognise a key ("github_pat_…7NE7"), not enough to use it.</summary>
    public static string Hint(string key)
    {
        var k = key.Trim();
        return k.Length < 16 ? "••••" : $"{k[..Math.Min(11, k.Length - 8)]}…{k[^4..]}";
    }

    public static KeyIdentity Identity(string provider, string key) => new(provider, Fingerprint(key), Hint(key));

    public static string Label(string? provider) => provider switch
    {
        "github" => "GitHub Copilot",
        "anthropic" => "Anthropic Claude",
        _ => "Recorded",
    };

    /// <summary>Builds the live provider for a key and model. Empty model: Copilot's default, or the app's Claude default.</summary>
    public static ILlmProvider Build(string provider, string key, string model, string? baseUrl) => provider switch
    {
        "anthropic" => new ClaudeProvider(key, string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model.Trim(),
            baseUrl: string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/')),
        "github" => new GitHubCopilotProvider(key, model.Trim()),
        _ => throw new AppError(400, "Choose GitHub Copilot or Anthropic Claude."),
    };

    /// <summary>The key and provider the server was started with (.env / appsettings), chosen the same way AppDeps chooses.</summary>
    public static (string Provider, string Key, string Model)? FromEnvironment(AppSettings s) => s.Provider switch
    {
        "anthropic" when s.ClaudeApiKey is not null => ("anthropic", s.ClaudeApiKey, s.ClaudeModel),
        "github" when s.GitHubToken is not null => ("github", s.GitHubToken, s.CopilotModel),
        "auto" when s.GitHubToken is not null => ("github", s.GitHubToken, s.CopilotModel),
        "auto" when s.ClaudeApiKey is not null => ("anthropic", s.ClaudeApiKey, s.ClaudeModel),
        _ => null,
    };
}

/// <summary>The model saved on the Settings page, with its key encrypted by ASP.NET Data Protection.</summary>
public sealed class LlmSettingsStore(Db db, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("ResolveAI.LlmSettings.v1");

    public sealed record Saved(string Provider, string Model, string? BaseUrl, string Key, string TestedAt);

    private sealed class Row
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string? BaseUrl { get; set; }
        public string ApiKeyProtected { get; set; } = "";
        public string TestedAt { get; set; } = "";
    }

    /// <summary>The saved model, or null. Unreadable means the encryption keys changed (e.g. a rebuilt container): enter the key again.</summary>
    public (Saved? Settings, bool Unreadable) Load()
    {
        var row = db.Get<Row>("SELECT provider, model, base_url, api_key_protected, tested_at FROM llm_settings WHERE id = 1");
        if (row is null) return (null, false);
        try
        {
            return (new Saved(row.Provider, row.Model, row.BaseUrl, _protector.Unprotect(row.ApiKeyProtected), row.TestedAt), false);
        }
        catch (CryptographicException)
        {
            return (null, true);
        }
    }

    public void Save(string provider, string model, string? baseUrl, string key, string testedAt) => db.Run(
        """
        INSERT OR REPLACE INTO llm_settings (id, provider, model, base_url, api_key_protected, key_hint, key_fingerprint, tested_at)
        VALUES (1, @provider, @model, @baseUrl, @protectedKey, @hint, @fingerprint, @testedAt)
        """,
        new { provider, model, baseUrl, protectedKey = _protector.Protect(key), hint = LlmKeys.Hint(key), fingerprint = LlmKeys.Fingerprint(key), testedAt });

    public void Clear() => db.Run("DELETE FROM llm_settings");

    public List<LlmUsageRow> Usage()
    {
        var since = $"{Contracts.Js.IsoNow()[..10]}T00:00:00.000Z";
        return db.All<LlmUsageRow>(
            """
            SELECT key_hint, provider, MAX(model) AS model, COUNT(*) AS calls,
                   SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
                   SUM(CASE WHEN created_at >= @since THEN input_tokens ELSE 0 END) AS today_input_tokens,
                   SUM(CASE WHEN created_at >= @since THEN output_tokens ELSE 0 END) AS today_output_tokens,
                   MAX(estimated) AS estimated, MAX(created_at) AS last_used
              FROM llm_usage
             GROUP BY key_fingerprint, key_hint, provider
             ORDER BY last_used DESC
            """,
            new { since });
    }
}
