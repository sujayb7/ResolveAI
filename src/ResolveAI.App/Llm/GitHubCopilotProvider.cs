using GitHub.Copilot;
using ResolveAI.Contracts;

namespace ResolveAI.App.Llm;

/// <summary>Live provider backed by the official GitHub Copilot SDK.</summary>
public sealed class GitHubCopilotProvider : ILlmProvider, IConnectionTest, IModelCatalog
{
    private readonly string _token;
    private readonly string _model;

    public GitHubCopilotProvider(string token, string model)
    {
        _token = token;
        _model = model;
    }

    public string Mode => "live";
    public string? Model => string.IsNullOrWhiteSpace(_model) ? "Copilot default" : _model;
    public FollowUp? FollowUp => null;

    public Task<LlmResult<InvestigationOutput>> Investigate(EvidencePacket packet, CancellationToken ct) =>
        Call<InvestigationOutput>(Prompts.InvestigationSystem, Prompts.InvestigationUserMessage(packet), ValidateInvestigation, ct);

    public Task<LlmResult<RcaOutput>> DraftRca(RcaInput input, CancellationToken ct) =>
        Call<RcaOutput>(Prompts.RcaSystem, Prompts.RcaUserMessage(input), static _ => { }, ct);

    /// <summary>One tiny structured call: proves the token, Copilot access and the model name all work.</summary>
    public async Task<LlmResult<string>> TestConnection(CancellationToken ct)
    {
        var result = await Call<ConnectionCheckReply>(
            "This is a connectivity check. Answer exactly as asked.", "Reply with the JSON object {\"reply\": \"OK\"}.", static _ => { }, ct);
        return new LlmResult<string>(result.Output.Reply, result.Model);
    }

    /// <summary>The models this token's Copilot plan allows; models turned off by policy are left out.</summary>
    public async Task<List<LlmModelOption>> ListModels(CancellationToken ct)
    {
        try
        {
            await using var client = NewClient();
            await client.StartAsync();
            var models = await client.ListModelsAsync(ct);
            var usable = models.Where(m => m.Policy is null || string.Equals(m.Policy.State, "enabled", StringComparison.OrdinalIgnoreCase)).ToList();
            return (usable.Count > 0 ? usable : models)
                .Select(m => new LlmModelOption(m.Id, Describe(m)))
                .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception err)
        {
            throw new LlmError($"GitHub Copilot could not list models for this token: {SafeMessage(err)}");
        }
    }

    private static string Describe(ModelInfo m)
    {
        var name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name;
        return m.Billing?.Multiplier is { } x ? $"{name} · {x:0.##}x" : name;
    }

    private CopilotClient NewClient() => new(new CopilotClientOptions { GitHubToken = _token, UseLoggedInUser = false });

    private async Task<LlmResult<T>> Call<T>(string system, string prompt, Action<T> validate, CancellationToken ct)
    {
        try
        {
            await using var client = NewClient();
            await client.StartAsync();
            var sessionConfig = new SessionConfig
            {
                SystemMessage = new SystemMessageConfig { Content = system },
                Streaming = false,
            };
            if (!string.IsNullOrWhiteSpace(_model)) sessionConfig.Model = _model;
            await using var session = await client.CreateSessionAsync(sessionConfig);
#pragma warning disable GHCP001
            var result = await session.SendAndWaitAsync<T>(prompt, Json.Options, cancellationToken: ct);
#pragma warning restore GHCP001
            validate(result);
            // Model, not _model: with no model configured it reads "Copilot default" instead of an empty name.
            return new LlmResult<T>(result, Model);
        }
        catch (LlmError)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception err)
        {
            if (!string.IsNullOrWhiteSpace(_model) && err.Message.Contains("is not available", StringComparison.OrdinalIgnoreCase))
            {
                throw new LlmError($"The model \"{_model}\" isn't available to this GitHub token. Pick one from the model list, or use Provider default.");
            }
            throw new LlmError($"The GitHub Copilot call failed: {SafeMessage(err)}", retryable: true);
        }
    }

    private static void ValidateInvestigation(InvestigationOutput output)
    {
        if (output.Hypotheses.Any(h => h.Confidence is not ("low" or "medium" or "high")))
            throw new LlmError("The GitHub Copilot response contained an invalid confidence value.", retryable: true);
    }

    private static string SafeMessage(Exception err) => err.Message.Length <= 300 ? err.Message : err.Message[..300] + "…";
}
