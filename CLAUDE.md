# ResolveAI: notes for Claude Code

AI incident investigation: a .NET 10 Blazor Server app with read-only MCP servers (database, files, deploys, metrics). The model only
analyses an evidence packet; every claim must cite packet IDs, a deterministic validator checks the output, and a person reviews it.

## Run and test
- Build: `dotnet build ResolveAI.sln`
- Unit, API and golden tests: `dotnet test tests/ResolveAI.Tests`. Golden JSON lives in `tests/ResolveAI.Tests/Golden/`.
- Run: `dotnet run --project src/ResolveAI.App` (port 8787). Recorded mode works offline with no key.
- Live model: set it on **Settings** (click the "LLM:" badge in the header). The page saves only after a passing connection test.
  Fallback is `.env` (copy `.env.example`).
- Live fault for demos: `python3 demo-systems/live-demo.py setup`, restart the app, then `inject <scenario>` (`list` shows six; scenarios are JSON in `demo-systems/scenarios/`). `live-demo.ps1` still has the original two.
- Full guide with diagrams and screenshots: `docs/ResolveAI-Guide.md`.
- `TreatWarningsAsErrors` is on: any compiler warning fails the build.

## Where things are
- `src/ResolveAI.App/Services/AppService.cs`: every action. The UI and the HTTP API both call it, so checks live here.
- `Services/Incidents.cs`: queue filters. Buckets never overlap: **Reviewed** = status `resolved` or review state `verified`; **Active** = the rest;
  **Needs review** = active with unreviewed AI findings.
- `Services/Investigations.cs`: the pipeline (gather evidence → similar incidents → baseline MCP checks → follow-ups → analyse → validate).
- `Pipeline/`: `Packet` (evidence IDs), `Sanitizer` (redacts secrets and PII), `Validate` (citations, unsafe actions, invented numbers), `Similar` (FTS5, no model).
- `Llm/`:
  - `Prompts.cs`: prompts (bump `Prompts.Version` when they change).
  - Providers: `ClaudeProvider`, `GitHubCopilotProvider`, `RecordedProvider`.
  - `MeteredProvider`: token usage per key.
- `Services/LlmSettings.cs`: Settings-page model and key (key encrypted with Data Protection; table `llm_settings`; usage in `llm_usage`).
- `Data/Seed.cs`: demo data; `Data/Migrations/NNN_name.sql`: add new files, never edit or rename applied ones (and update `DataTests`).

## Rules
- Never commit `.env`, keys or tokens, `data/`, `team-knowledge/` or decks (`*.ppt`, `*.pptx`); all are git-ignored. Never print a key; show `LlmKeys.Hint`.
- MCP tools stay read-only. Recommended checks must never contain commands that change state (the validator strips them).
- Changing seed text, prompts or recorded fixtures changes golden files: update them in the same commit and read the diff.
- `GitHubCopilotProvider` has no follow-up tool calls (`FollowUp => null`); only the Claude provider makes the investigation agentic.
- Copilot model names use dots (`claude-sonnet-4.6`); Anthropic's use dashes (`claude-sonnet-4-6`).
- In a Codespace, `GITHUB_TOKEN` is set automatically and the app falls back to it; set `GITHUB_COPILOT_TOKEN` explicitly.
