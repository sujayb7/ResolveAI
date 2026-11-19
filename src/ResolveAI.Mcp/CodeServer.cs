using System.Diagnostics;
using System.Text;
using ResolveAI.Contracts;

namespace ResolveAI.Mcp;

/// <summary>
/// Read-only access to one git repository's current code: search it and read a file. The repository is a shallow clone
/// kept under the cache folder; it is cloned on first use and refreshed best-effort after that. No tool can change it.
/// </summary>
public static class CodeServer
{
    private const int MaxFilesScanned = 4000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxReadLines = 400;
    private const int MaxMatchesPerFile = 4;

    private static readonly HashSet<string> SourceExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".php", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".go", ".rb", ".rs", ".kt", ".swift", ".c", ".cpp", ".h", ".sql", ".razor", ".vue",
    };

    /// <summary>0 = source code, 1 = other text (config, scripts), 2 = documentation.</summary>
    private static int Priority(string rel)
    {
        var ext = Path.GetExtension(rel);
        if (rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || ext is ".md" or ".txt" or ".rst") return 2;
        return SourceExt.Contains(ext) ? 0 : 1;
    }
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(25);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "vendor", "dist", "bin", "obj", ".vs" };
    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".zip", ".gz", ".tar", ".7z", ".mp4", ".mov", ".woff", ".woff2", ".ttf", ".eot", ".sqlite", ".db", ".exe", ".dll", ".so", ".dylib",
    };

    public static ToolServer Build(McpConfig cfg) => BuildWith(cfg.Code, (repo, args, ct) => Git(repo, args, ct));

    /// <summary>The same server over an injected git runner, so tests can avoid the network.</summary>
    public static ToolServer BuildWith(CodeConfig code, Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> git)
    {
        var url = code.RepoUrl;
        var cache = code.CacheDir;
        var branch = string.IsNullOrWhiteSpace(code.Branch) ? null : code.Branch.Trim();

        async Task<string> EnsureClone(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(cache))
                throw new ToolError("No repository is configured. Set code.repo_url in the MCP config.");
            if (Directory.Exists(Path.Combine(cache, ".git")))
            {
                try { await git(cache, ["fetch", "--depth", "1", "origin", branch ?? "HEAD"], ct); await git(cache, ["reset", "--hard", "FETCH_HEAD"], ct); }
                catch (ToolError) { /* keep the copy already cloned if a refresh fails */ }
                return cache;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            List<string> cloneArgs = ["clone", "--depth", "1"];
            if (branch is not null) { cloneArgs.Add("--branch"); cloneArgs.Add(branch); }
            cloneArgs.Add(url);
            cloneArgs.Add(cache);
            await git("", cloneArgs, ct);
            return cache;
        }

        static IEnumerable<string> Walk(string dir, int[] budget)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (budget[0] <= 0) yield break;
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (SkipDirs.Contains(name)) continue;
                    foreach (var f in Walk(entry, budget)) yield return f;
                }
                else
                {
                    budget[0]--;
                    yield return entry;
                }
            }
        }

        static bool Searchable(string path)
        {
            if (SkipExt.Contains(Path.GetExtension(path))) return false;
            try { return new FileInfo(path).Length <= MaxFileBytes; }
            catch { return false; }
        }

        // A caller-supplied path must land inside the clone and nowhere else (no .. escape, no symlink out).
        string Confine(string root, string input)
        {
            var rel = input.Replace('\\', '/').TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(root, rel));
            var real = PathGuard.IsInside(candidate, root) ? candidate : throw new ToolError($"Path is outside the repository: {input}");
            return real;
        }

        static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

        return new ToolServer("resolveai-code",
        [
            new ToolDef("code_search",
                "Search the repository's current code for a phrase (case-insensitive substring). Returns matching file, line number and line text. Optionally limit to paths under a sub-folder.",
                [Field.Str("phrase", min: 1, max: 200), Field.Str("path", "A sub-folder to limit the search to, e.g. src", required: false, max: 300), Field.Int("max_matches", 1, 200, 40)],
                async (args, ct) =>
                {
                    var root = await EnsureClone(ct);
                    var phrase = args.Str("phrase");
                    var sub = args.OptStr("path");
                    var start = string.IsNullOrWhiteSpace(sub) ? root : Confine(root, sub);
                    var max = args.Int("max_matches");
                    if (!Directory.Exists(start)) throw new ToolError($"No such folder in the repository: {sub}");
                    var budget = new[] { MaxFilesScanned };
                    var rows = new List<Dictionary<string, object?>>();
                    var scanned = 0;
                    // Source code first, then other text, docs last; and a few matches per file, so one file (or a README)
                    // can't use up the whole budget before the code that matters is reached.
                    var files = Walk(start, budget).Where(Searchable).OrderBy(f => Priority(Rel(root, f))).ThenBy(f => f, StringComparer.Ordinal).ToList();
                    foreach (var file in files)
                    {
                        if (rows.Count >= max) break;
                        scanned++;
                        string[] lines;
                        try { lines = await File.ReadAllLinesAsync(file, ct); }
                        catch { continue; }
                        var inFile = 0;
                        for (var i = 0; i < lines.Length && rows.Count < max && inFile < MaxMatchesPerFile; i++)
                        {
                            if (!lines[i].Contains(phrase, StringComparison.OrdinalIgnoreCase)) continue;
                            rows.Add(Row.Of(("file", Rel(root, file)), ("line", i + 1), ("text", Limits.Clip(lines[i].Trim()))));
                            inFile++;
                        }
                    }
                    var summary = rows.Count > 0
                        ? $"{rows.Count} line(s) containing \"{phrase}\"{(sub is null ? "" : $" under {sub}")} ({scanned} file(s) scanned)."
                        : $"No code lines contain \"{phrase}\"{(sub is null ? "" : $" under {sub}")} ({scanned} file(s) scanned).";
                    return ToolResult.Ok(summary, rows, sub is null ? "code" : $"code/{sub}", truncated: rows.Count >= max);
                }),

            new ToolDef("code_read",
                "Read a file from the repository by its path, with line numbers. Returns up to 400 lines from the given start line.",
                [Field.Str("path", "Repository-relative path, e.g. src/Auth.php", min: 1, max: 300), Field.Int("start_line", 1, 1_000_000, 1), Field.Int("lines", 1, MaxReadLines, MaxReadLines)],
                async (args, ct) =>
                {
                    var root = await EnsureClone(ct);
                    var file = Confine(root, args.Str("path"));
                    if (!File.Exists(file)) throw new ToolError($"No such file in the repository: {args.Str("path")}");
                    if (SkipExt.Contains(Path.GetExtension(file)) || new FileInfo(file).Length > MaxFileBytes) throw new ToolError("That file is binary or too large to read.");
                    var start = args.Int("start_line");
                    var count = args.Int("lines");
                    var all = await File.ReadAllLinesAsync(file, ct);
                    var rows = new List<Dictionary<string, object?>>();
                    for (var i = start - 1; i < all.Length && rows.Count < count; i++)
                        rows.Add(Row.Of(("line", i + 1), ("text", Limits.Clip(all[i]))));
                    var end = rows.Count > 0 ? start + rows.Count - 1 : start;
                    return ToolResult.Ok($"{Rel(root, file)} lines {start}-{end} of {all.Length}.", rows, $"code/{Rel(root, file)}", truncated: start - 1 + rows.Count < all.Length);
                }),

            new ToolDef("code_list",
                "List files in the repository, optionally under a sub-folder, so you can see the code layout.",
                [Field.Str("path", "A sub-folder, e.g. src; omit for the repository root", required: false, max: 300)],
                async (args, ct) =>
                {
                    var root = await EnsureClone(ct);
                    var sub = args.OptStr("path");
                    var start = string.IsNullOrWhiteSpace(sub) ? root : Confine(root, sub);
                    if (!Directory.Exists(start)) throw new ToolError($"No such folder in the repository: {sub}");
                    var budget = new[] { MaxFilesScanned };
                    var rows = Walk(start, budget)
                        .Select(f => { long size; try { size = new FileInfo(f).Length; } catch { size = 0; } return Row.Of(("path", Rel(root, f)), ("bytes", size)); })
                        .ToList();
                    return ToolResult.Ok($"{rows.Count} file(s){(sub is null ? "" : $" under {sub}")} in the repository.", rows, sub is null ? "code" : $"code/{sub}");
                }),
        ])
        { CallTimeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Runs git with fixed arguments and no shell, killed at the timeout, with its output capped. The repo is the clone cache only.</summary>
    private static async Task<string> Git(string workingDir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        // -c credential.helper= and the terminal prompt off, so a private repo fails fast instead of hanging for input.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("credential.helper=");
        if (!string.IsNullOrEmpty(workingDir)) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(workingDir); }
        start.ArgumentList.Add("--no-pager");
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new ToolError("Could not start git.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var err = (await stderr).Trim();
                throw new ToolError($"git failed: {(err.Length > 200 ? err[..200] : err)}");
            }
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolError("git took too long and was stopped.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
