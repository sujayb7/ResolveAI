using System.Security.Cryptography;
using System.Text;
using ResolveAI.App.Data;
using ResolveAI.App.Llm;
using ResolveAI.Contracts;

namespace ResolveAI.App.Services;

/// <summary>A failure with an HTTP status and a message that is safe to show. The UI shows the message; the API returns both.</summary>
public sealed class AppError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record Providers(ILlmProvider Recorded, ILlmProvider? Live);

public sealed record KnowledgeBody(string Id, string Title, string DocType, string? ServiceName, string Body);

/// <summary>Who is asking: both the HTTP API and the Blazor UI supply this, so the same checks apply to each.</summary>
public sealed record Caller(string? AccessCode, bool IsLocal);

/// <summary>
/// Everything the app does. The HTTP routes and the Blazor components both call this, so the access code, the daily live
/// limit, input limits and ownership checks can't be bypassed from either side.
/// </summary>
public sealed class AppService(Db db, Providers providers, Systems? systems, AppSettings settings, TeamsNotifier? notifier = null, LlmSettingsStore? llmStore = null)
{
    private static readonly HashSet<string> ReviewStates = ["none", "pending", "verified", "active"];

    /// <summary>
    /// The live provider and default mode in use right now. Starts from .env; the Settings page replaces it after a
    /// passing connection test, or drops to recorded after a failing one. Swapped as one object so readers never see half a change.
    /// </summary>
    private sealed record LlmState(ILlmProvider? Live, string Mode, LlmStatus Status);

    private volatile LlmState? _llmState;

    // Built on first use rather than in a field initializer, so the constructor parameters are only captured, never copied.
    private LlmState Llm => _llmState ??= new LlmState(providers.Live, settings.LlmMode, EnvStatus());

    private LlmStatus EnvStatus() => (LlmKeys.FromEnvironment(settings), providers.Live) switch
    {
        ({ } env, { } live) => new LlmStatus(settings.LlmMode, env.Provider, live.Model, LlmKeys.Hint(env.Key), ".env", null, null),
        (null, { } live) => new LlmStatus(settings.LlmMode, null, live.Model, null, "server", null, null),
        _ => new LlmStatus("recorded", null, null, null, "none", null, null),
    };

    private ILlmProvider? Live => Llm.Live;
    private string LlmMode => Llm.Mode;
    private ILlmProvider DefaultProvider => LlmMode == "live" && Live is not null ? Live : providers.Recorded;
    private bool LiveAvailable => Live is not null && (LlmMode == "live" || !string.IsNullOrEmpty(settings.LiveAccessCode));

    private static byte[] Digest(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private bool HasAccessCode(Caller caller) =>
        !string.IsNullOrEmpty(settings.LiveAccessCode) && !string.IsNullOrEmpty(caller.AccessCode)
        && CryptographicOperations.FixedTimeEquals(Digest(caller.AccessCode), Digest(settings.LiveAccessCode));

    /// <summary>
    /// Picks the provider for one model call. Live costs money, so unless the server runs live by default
    /// it needs the access code, and every live call counts against the daily limit.
    /// </summary>
    private ILlmProvider ProviderFor(Caller caller, string mode)
    {
        if (mode == "recorded") return providers.Recorded;
        if (Live is not { } live) throw new AppError(400, "Live mode is not configured on this server (no API key).");
        if (LlmMode != "live")
        {
            if (string.IsNullOrEmpty(settings.LiveAccessCode)) throw new AppError(400, "Live mode is not enabled on this server (no access code configured).");
            if (!HasAccessCode(caller)) throw new AppError(403, "Live mode needs a valid access code. Enter it under Settings.");
        }
        if (Investigations.LiveRunsToday(db) >= settings.LiveDailyLimit)
        {
            throw new AppError(429, $"Today's live limit ({settings.LiveDailyLimit} model calls) is used up. Use recorded mode or try again tomorrow (UTC).");
        }
        return live;
    }

    private Incident IncidentOr404(string idOrKey) => Incidents.Find(db, idOrKey) ?? throw new AppError(404, "Incident not found");

    public AppConfig Config() => new()
    {
        LlmMode = DefaultProvider.Mode,
        Model = DefaultProvider.Mode == "live" ? DefaultProvider.Model ?? "configured live model" : "recorded fixtures",
        LiveAvailable = LiveAvailable,
        LiveModel = Live?.Model,
        LiveDailyLimit = settings.LiveDailyLimit,
        LiveRunsToday = Investigations.LiveRunsToday(db),
        DemoMode = settings.DemoMode,
        DataSource = "Demo dataset",
        MaxFollowUpCalls = systems?.MaxFollowUps ?? 0,
    };

    public SystemsResponse Systems()
    {
        var statuses = systems?.Hub.Status() ?? [];
        var database = statuses.FirstOrDefault(s => s.System == "database");
        if (database is not null)
        {
            database.Status = "connected";
            database.Detail = "Demo SQL Server connected; read-only mock checks are enabled.";
        }

        return new SystemsResponse { Systems = statuses, Enabled = systems is not null };
    }

    public IncidentList ListIncidents(string? status, string? severity, string? service, string? q, string? review, int page, int pageSize)
    {
        var filters = new IncidentFilters(
            string.IsNullOrEmpty(status) ? null : status, string.IsNullOrEmpty(severity) ? null : severity, string.IsNullOrEmpty(service) ? null : service,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(), review is not null && ReviewStates.Contains(review) ? review : null,
            Math.Max(1, page), Math.Min(100, Math.Max(1, pageSize)));
        var list = Incidents.List(db, filters);
        list.Services = Incidents.Services(db);
        return list;
    }

    public IncidentDetail IncidentDetail(string idOrKey)
    {
        var incident = IncidentOr404(idOrKey);
        return new IncidentDetail
        {
            Incident = incident,
            LatestInvestigationId = Investigations.LatestId(db, incident.Id),
            Investigations = Investigations.ListRuns(db, incident.Id),
            RcaDrafts = Rca.List(db, incident.Id),
        };
    }

    public IncidentEvidence Evidence(string idOrKey) => Pipeline.EvidenceLoader.Load(db, IncidentOr404(idOrKey));

    public Incident CreateIncident(string title, string serviceName, string severity, string environment, string description, string impactSummary)
    {
        static string Required(string name, string value, int max)
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.Length == 0) throw new AppError(400, $"Invalid request: {name} is required.");
            if (trimmed.Length > max) throw new AppError(400, $"Invalid request: {name} must be at most {max} characters.");
            return trimmed;
        }

        severity = Required("severity", severity, 10);
        if (severity is not ("SEV-1" or "SEV-2" or "SEV-3" or "SEV-4")) throw new AppError(400, "Invalid request: severity is not valid.");
        var now = Js.IsoNow();
        var incident = new Incident
        {
            Id = Guid.NewGuid().ToString(),
            Key = $"INC-{db.Get<int>("SELECT COALESCE(MAX(CAST(SUBSTR(key, 5) AS INTEGER)), 0) + 1 FROM incidents WHERE key LIKE 'INC-%'"):D4}",
            Title = Required("title", title, 200),
            ServiceName = Required("service name", serviceName, 100),
            Severity = severity,
            Status = "open",
            Environment = Required("environment", environment, 100),
            StartedAt = now,
            DetectedAt = now,
            Description = Required("description", description, 5000),
            ImpactSummary = Required("impact summary", impactSummary, 1000),
        };
        db.Run(
            """
            INSERT INTO incidents (id, key, title, service_name, severity, status, environment, started_at, detected_at, description, impact_summary, created_at)
            VALUES (@Id, @Key, @Title, @ServiceName, @Severity, @Status, @Environment, @StartedAt, @DetectedAt, @Description, @ImpactSummary, @CreatedAt)
            """, new { incident.Id, incident.Key, incident.Title, incident.ServiceName, incident.Severity, incident.Status, incident.Environment, incident.StartedAt, incident.DetectedAt, incident.Description, incident.ImpactSummary, CreatedAt = now });
        return incident;
    }

    public (Incident Incident, string InvestigationId, string Status) CreateIncidentAndStart(CreateIncidentBody body, Caller caller)
    {
        if (body.Question?.Length > 500) throw new AppError(400, "Invalid request: question must be at most 500 characters.");
        var incident = CreateIncident(body.Title, string.IsNullOrWhiteSpace(body.ApplicationName) ? "user-reported" : body.ApplicationName!, body.Severity, body.Environment, body.Description, body.ImpactSummary);
        try
        {
            var (id, status) = StartInvestigation(incident.Id, new StartInvestigationBody { Question = body.Question, Mode = body.Mode }, caller);
            return (incident, id, status);
        }
        catch
        {
            db.Run("DELETE FROM incidents WHERE id = @id", new { id = incident.Id });
            throw;
        }
    }

    public (string InvestigationId, string Status) StartInvestigation(string idOrKey, StartInvestigationBody body, Caller caller)
    {
        var incident = IncidentOr404(idOrKey);
        if (body.Question?.Length > 500) throw new AppError(400, "Invalid request: question must be at most 500 characters.");
        if (body.Mode is not (null or "recorded" or "live")) throw new AppError(400, "Invalid request: mode must be recorded or live.");
        var running = db.Get<string>("SELECT id FROM investigations WHERE incident_id = @id AND status IN ('queued', 'running') LIMIT 1", new { id = incident.Id });
        if (running is not null) return (running, "running");
        var llm = ProviderFor(caller, body.Mode ?? DefaultProvider.Mode);
        var question = string.IsNullOrWhiteSpace(body.Question) ? null : body.Question.Trim();
        db.Run("UPDATE incidents SET status = 'investigating' WHERE id = @id", new { id = incident.Id });
        return (Investigations.Start(db, llm, systems, incident, question, notifier), "queued");
    }

    public Investigation Investigation(string id) => Investigations.Get(db, id) ?? throw new AppError(404, "Investigation not found");

    public string AddFeedback(string investigationId, FeedbackBody body)
    {
        var row = Investigations.GetRow(db, investigationId) ?? throw new AppError(404, "Investigation not found");
        if (body.Comment?.Length > 2000) throw new AppError(400, "Invalid request: comment must be at most 2000 characters.");
        if (body.VerifiedRootCause?.Length > 2000) throw new AppError(400, "Invalid request: verified_root_cause must be at most 2000 characters.");
        if (body.HypothesisId is not null
            && db.Get<int>("SELECT COUNT(*) FROM hypotheses WHERE id = @hypothesis AND investigation_id = @investigation", new { hypothesis = body.HypothesisId, investigation = row.Id }) == 0)
        {
            throw new AppError(400, "Hypothesis does not belong to this investigation");
        }
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        var verified = string.IsNullOrWhiteSpace(body.VerifiedRootCause) ? null : body.VerifiedRootCause.Trim();
        if (body.Helpful is null && comment is null && verified is null) throw new AppError(400, "Feedback needs a helpful flag, a comment or a verified root cause");
        var id = Guid.NewGuid().ToString();
        db.Run(
            """
            INSERT INTO feedback (id, investigation_id, hypothesis_id, helpful, comment, verified_root_cause, created_at)
            VALUES (@id, @investigation, @hypothesis, @helpful, @comment, @verified, @now)
            """,
            new { id, investigation = row.Id, hypothesis = body.HypothesisId, helpful = body.Helpful is null ? (int?)null : body.Helpful.Value ? 1 : 0, comment, verified, now = Js.IsoNow() });
        return id;
    }

    public async Task<RcaDraft> CreateRca(string idOrKey, string investigationId, Caller caller)
    {
        var incident = IncidentOr404(idOrKey);
        var inv = Investigations.GetRow(db, investigationId);
        if (inv is null || inv.IncidentId != incident.Id) throw new AppError(400, "That investigation does not belong to this incident");
        if (inv.Status != "completed") throw new AppError(409, "Run an investigation to completion before drafting an RCA");
        // The draft is written in the same mode as the investigation it summarises.
        var llm = ProviderFor(caller, inv.LlmMode);
        return await Rca.Create(db, llm, incident, investigationId);
    }

    public RcaDraft GetRca(string id) => Rca.Get(db, id) ?? throw new AppError(404, "RCA draft not found");

    public RcaDraft PatchRca(string id, PatchRcaBody patch)
    {
        static void Text(string name, string? value, int max)
        {
            if (value?.Length > max) throw new AppError(400, $"Invalid request: {name} must be at most {max} characters.");
        }
        static void Items(string name, List<string>? values)
        {
            if (values is null) return;
            if (values.Count > 30) throw new AppError(400, $"Invalid request: {name} must have at most 30 items.");
            if (values.Any(v => v.Length > 1000)) throw new AppError(400, $"Invalid request: {name} items must be at most 1000 characters.");
        }
        Text("impact", patch.Impact, 5000);
        Text("root_cause", patch.RootCause, 5000);
        Items("contributing_factors", patch.ContributingFactors);
        Items("resolution_steps", patch.ResolutionSteps);
        Items("prevention_actions", patch.PreventionActions);
        if (patch.Status is not (null or "draft" or "reviewed")) throw new AppError(400, "Invalid request: status must be draft or reviewed.");
        var draft = Rca.Patch(db, id, patch) ?? throw new AppError(404, "RCA draft not found");
        if (string.Equals(patch.Status, "reviewed", StringComparison.OrdinalIgnoreCase))
        {
            // Stored as "resolved" (shown as "Reviewed"): the schema's CHECK constraint has no "reviewed" status.
            db.Run("UPDATE incidents SET status = 'resolved', resolved_at = COALESCE(resolved_at, @now) WHERE id = @id", new { id = draft.IncidentId, now = Js.IsoNow() });
        }
        return draft;
    }

    public (string FileName, string Markdown) ExportRca(string id)
    {
        var rca = GetRca(id);
        var incident = IncidentOr404(rca.IncidentId);
        return ($"{incident.Key}-rca-v{rca.Version}.md", Rca.Markdown(rca, incident));
    }

    public (string FileName, byte[] Content) ExportRcaPdf(string id)
    {
        var rca = GetRca(id);
        var incident = IncidentOr404(rca.IncidentId);
        return ($"{incident.Key}-rca-v{rca.Version}.pdf", Rca.Pdf(rca, incident));
    }

    public KnowledgeBody Knowledge(string id) =>
        db.Get<KnowledgeBody>("SELECT id, title, doc_type, service_name, body FROM knowledge_documents WHERE id = @id", new { id }) ?? throw new AppError(404, "Document not found");

    // Settings → AI model and keys

    private void RequireAdmin(Caller caller)
    {
        if (!caller.IsLocal && !HasAccessCode(caller))
        {
            throw new AppError(403, "Changing the AI model needs localhost or the access code (Settings → Access code).");
        }
    }

    private ILlmProvider Metered(ILlmProvider provider, string name, string key) => new MeteredProvider(provider, db, LlmKeys.Identity(name, key));

    public LlmStatus GetLlmStatus() => Llm.Status;

    public List<LlmUsageRow> LlmUsage() => llmStore?.Usage() ?? [];

    /// <summary>Called once at startup: a model saved on the Settings page (it passed a connection test) wins over .env.</summary>
    public void RestoreSavedLlm()
    {
        if (llmStore is null) return;
        var (saved, unreadable) = llmStore.Load();
        if (unreadable)
        {
            var current = Llm;
            _llmState = current with { Status = current.Status with { Warning = "The key saved on the Settings page can't be decrypted on this server any more. Enter it again." } };
            return;
        }
        if (saved is null) return;
        var live = Metered(LlmKeys.Build(saved.Provider, saved.Key, saved.Model, saved.BaseUrl), saved.Provider, saved.Key);
        _llmState = new LlmState(live, "live", new LlmStatus("live", saved.Provider, live.Model, LlmKeys.Hint(saved.Key), "Settings page", saved.TestedAt, null));
    }

    /// <summary>An empty key field reuses the saved key, or the .env key, for the same provider.</summary>
    private string? ReuseKey(string provider)
    {
        if (llmStore?.Load().Settings is { } saved && saved.Provider == provider) return saved.Key;
        return LlmKeys.FromEnvironment(settings) is { } env && env.Provider == provider ? env.Key : null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, List<LlmModelOption> Models)> _modelLists = new();

    /// <summary>
    /// The models a key can use, for the Settings page's model list. An empty key reuses the saved or .env key for that provider.
    /// Returns an empty list when there is no key yet. Lists are cached for ten minutes per key.
    /// </summary>
    public async Task<List<LlmModelOption>> ListLlmModels(string provider, string? apiKey, string? baseUrl, Caller caller)
    {
        RequireAdmin(caller);
        if (provider is not ("github" or "anthropic")) throw new AppError(400, "Choose GitHub Copilot or Anthropic Claude.");
        var key = apiKey?.Trim() is { Length: > 0 } typed ? typed : ReuseKey(provider);
        if (key is null) return [];
        if (key.Length > 500) throw new AppError(400, "Invalid request: the API key is too long.");
        var gateway = provider == "anthropic" && !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.Trim().TrimEnd('/') : null;
        var cacheKey = $"{provider}|{LlmKeys.Fingerprint(key)}|{gateway}";
        if (_modelLists.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Models;

        if (LlmKeys.Build(provider, key, "", gateway) is not IModelCatalog catalog) return [];
        var models = await Investigations.WithTimeout(ct => catalog.ListModels(ct), TimeSpan.FromSeconds(45), "Listing models took longer than 45 seconds.");
        _modelLists[cacheKey] = (DateTimeOffset.UtcNow, models);
        return models;
    }

    /// <summary>
    /// Makes one tiny call with the given provider, model and key. If it answers, the settings are saved (key encrypted) and new
    /// investigations use them at once. If not, nothing is saved: a model that already passed its test keeps running, otherwise
    /// ResolveAI drops to recorded mode, so a demo never runs on a broken key.
    /// </summary>
    public async Task<LlmTestResult> TestAndSaveLlm(string provider, string? model, string? apiKey, string? baseUrl, Caller caller)
    {
        RequireAdmin(caller);
        if (provider is not ("github" or "anthropic")) throw new AppError(400, "Choose GitHub Copilot or Anthropic Claude.");
        var modelName = model?.Trim() ?? "";
        if (modelName.Length > 100) throw new AppError(400, "Invalid request: model must be at most 100 characters.");
        var gateway = provider == "anthropic" && !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.Trim() : null;
        if (gateway is not null && !Uri.TryCreate(gateway, UriKind.Absolute, out _)) throw new AppError(400, "Invalid request: the base URL is not a valid URL.");
        var key = apiKey?.Trim() ?? "";
        if (key.Length == 0) key = ReuseKey(provider) ?? throw new AppError(400, "Enter an API key.");
        if (key.Length > 500) throw new AppError(400, "Invalid request: the API key is too long.");

        var candidate = LlmKeys.Build(provider, key, modelName, gateway);
        if (candidate is not IConnectionTest test) throw new AppError(400, "This provider can't be tested.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        LlmResult<string> result;
        try
        {
            result = await Investigations.WithTimeout(ct => test.TestConnection(ct), TimeSpan.FromSeconds(60), "The connection test took longer than a minute.");
        }
        catch (Exception err)
        {
            var reason = err is LlmError ? err.Message : "The connection test failed unexpectedly.";
            if (err is not LlmError) Console.Error.WriteLine($"connection test failed: {err}");
            // A failing test saves nothing. A model that already passed its own test keeps running; otherwise recorded analysis is used.
            var current = Llm;
            if (current.Mode == "live" && current.Live is not null)
            {
                return new LlmTestResult(false, $"{reason} Nothing was saved; still using {LlmKeys.Label(current.Status.Provider)} ({current.Status.Model ?? "default model"}).",
                    0, 0, false, timer.ElapsedMilliseconds);
            }
            _llmState = new LlmState(null, "recorded",
                new LlmStatus("recorded", provider, modelName, LlmKeys.Hint(key), "none", null, $"Last connection test failed: {reason}"));
            return new LlmTestResult(false, $"{reason} Nothing was saved; ResolveAI is using recorded mode.", 0, 0, false, timer.ElapsedMilliseconds);
        }

        var id = LlmKeys.Identity(provider, key);
        var estimated = result.Usage is null;
        var input = result.Usage?.GetValueOrDefault("input_tokens") ?? UsageLog.Estimate(120);
        var output = result.Usage?.GetValueOrDefault("output_tokens") ?? UsageLog.Estimate((result.Output ?? "").Length + 12);
        UsageLog.Record(db, id, result.Model ?? candidate.Model, "connection_test", input, output, estimated);

        var now = Js.IsoNow();
        llmStore?.Save(provider, modelName, gateway, key, now);
        _llmState = new LlmState(new MeteredProvider(candidate, db, id), "live",
            new LlmStatus("live", provider, candidate.Model, id.Hint, llmStore is null ? "this session" : "Settings page", now, null));
        return new LlmTestResult(true, $"Connected to {LlmKeys.Label(provider)} ({candidate.Model ?? "default model"}). Saved; new investigations use it now.",
            input, output, estimated, timer.ElapsedMilliseconds);
    }

    /// <summary>Forgets the saved key and runs recorded analysis only (no model calls, no cost).</summary>
    public void UseRecordedLlm(Caller caller)
    {
        RequireAdmin(caller);
        llmStore?.Clear();
        _llmState = new LlmState(null, "recorded", new LlmStatus("recorded", null, null, null, "none", null, null));
    }

    private volatile string? _showModelName;

    /// <summary>Whether AI results name the model that wrote them. Off by default; set on the Settings page for every viewer.</summary>
    public bool ShowModelName => (_showModelName ??= db.Get<string>("SELECT value FROM app_prefs WHERE name = 'show_model_name'") ?? "0") == "1";

    public void SetShowModelName(bool show, Caller caller)
    {
        RequireAdmin(caller);
        var value = show ? "1" : "0";
        db.Run("INSERT OR REPLACE INTO app_prefs (name, value) VALUES ('show_model_name', @value)", new { value });
        _showModelName = value;
    }

    public void DemoReset(Caller caller)
    {
        if (!settings.DemoMode) throw new AppError(404, "Not found");
        if (!settings.DemoResetAnyIp && !caller.IsLocal && !HasAccessCode(caller))
        {
            throw new AppError(403, "Demo reset is only available from localhost or with the access code");
        }
        if (db.Get<int>("SELECT COUNT(*) FROM investigations WHERE status IN ('queued', 'running')") > 0)
        {
            throw new AppError(409, "An investigation is still running; wait for it to finish before resetting");
        }
        Seed.Demo(db);
    }
}
