using ResolveAI.App.Data;
using ResolveAI.Contracts;

namespace ResolveAI.App.Llm;

/// <summary>Which key paid for a call: a fingerprint and a masked hint, never the key itself.</summary>
public sealed record KeyIdentity(string Provider, string Fingerprint, string Hint);

/// <summary>Writes one row per live model call into llm_usage. Recording never fails the call it measures.</summary>
public static class UsageLog
{
    public static void Record(Db db, KeyIdentity key, string? model, string purpose, long input, long output, bool estimated)
    {
        try
        {
            db.Run(
                """
                INSERT INTO llm_usage (key_fingerprint, key_hint, provider, model, purpose, input_tokens, output_tokens, estimated, created_at)
                VALUES (@fingerprint, @hint, @provider, @model, @purpose, @input, @output, @estimated, @now)
                """,
                new { fingerprint = key.Fingerprint, hint = key.Hint, provider = key.Provider, model, purpose, input, output, estimated = estimated ? 1 : 0, now = Js.IsoNow() });
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"token usage not recorded: {err.Message}");
        }
    }

    /// <summary>About four characters per token: good enough to compare keys and days, not to bill from.</summary>
    public static long Estimate(int characters) => (characters + 3) / 4;
}

/// <summary>
/// Wraps a live provider and records the tokens of every successful call against the key that paid for it.
/// Claude reports exact usage; the GitHub Copilot SDK reports none, so its tokens are estimated from the text sent and received.
/// </summary>
public sealed class MeteredProvider(ILlmProvider inner, Db db, KeyIdentity key) : ILlmProvider
{
    public KeyIdentity Key => key;
    public string Mode => inner.Mode;
    public string? Model => inner.Model;

    public async Task<LlmResult<InvestigationOutput>> Investigate(EvidencePacket packet, CancellationToken ct)
    {
        var result = await inner.Investigate(packet, ct);
        Record("investigation", result.Model, result.Usage,
            () => (Prompts.InvestigationSystem.Length + Prompts.InvestigationUserMessage(packet).Length, Json.Serialize(result.Output).Length));
        return result;
    }

    public async Task<LlmResult<RcaOutput>> DraftRca(RcaInput input, CancellationToken ct)
    {
        var result = await inner.DraftRca(input, ct);
        Record("rca", result.Model, result.Usage,
            () => (Prompts.RcaSystem.Length + Prompts.RcaUserMessage(input).Length, Json.Serialize(result.Output).Length));
        return result;
    }

    public FollowUp? FollowUp => inner.FollowUp is { } followUp ? Metered(followUp) : null;

    private FollowUp Metered(FollowUp followUp) => async (packet, tools, call, maxCalls, ct) =>
    {
        var usage = await followUp(packet, tools, call, maxCalls, ct);
        if (usage is not null)
        {
            UsageLog.Record(db, key, inner.Model, "follow_up",
                usage.GetValueOrDefault("follow_up_input_tokens"), usage.GetValueOrDefault("follow_up_output_tokens"), estimated: false);
        }
        return usage;
    };

    private void Record(string purpose, string? model, Dictionary<string, long>? usage, Func<(int Prompt, int Output)> characters)
    {
        if (usage is not null && (usage.ContainsKey("input_tokens") || usage.ContainsKey("output_tokens")))
        {
            UsageLog.Record(db, key, model ?? inner.Model, purpose, usage.GetValueOrDefault("input_tokens"), usage.GetValueOrDefault("output_tokens"), estimated: false);
            return;
        }
        var (prompt, output) = characters();
        UsageLog.Record(db, key, model ?? inner.Model, purpose, UsageLog.Estimate(prompt), UsageLog.Estimate(output), estimated: true);
    }
}
