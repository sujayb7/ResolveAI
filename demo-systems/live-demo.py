#!/usr/bin/env python3
"""
Breaks a demo service "right now" so ResolveAI can investigate a fresh incident live, through its MCP tools.

Works anywhere Python 3.8+ runs (Linux, macOS, Windows, GitHub Codespaces); no packages needed.
The scenarios live in demo-systems/scenarios/*.json, so adding one needs no code.

  1) Once, before starting the app:   python3 demo-systems/live-demo.py setup
     (writes mcp.config.json so every scenario's service, log folder, deploy feed and metrics are known; restart the app after)
  2) Just before creating the incident:   python3 demo-systems/live-demo.py inject inventory-cache-ttl
     (writes fresh, timestamped logs/deploy/metrics and prints what to type into "Investigate incident")
  3) Afterwards:   python3 demo-systems/live-demo.py reset

  python3 demo-systems/live-demo.py list      shows every scenario
  python3 demo-systems/live-demo.py inject all   injects every scenario at once (for testing)

ResolveAI's MCP servers re-read log files, the deploy feed and metric series on every call, so an incident created a minute
after "inject" is investigated against data that did not exist when the app started. Everything is written to
demo-systems/live/, which is git-ignored.
"""
import json
import random
import shutil
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
LIVE = HERE / "live"
SCENARIOS = HERE / "scenarios"
CONFIG = REPO / "mcp.config.json"
BACKUP = REPO / "mcp.config.json.before-live-demo"


def scenarios():
    return {s["id"]: s for s in (json.loads(p.read_text(encoding="utf-8")) for p in sorted(SCENARIOS.glob("*.json")))}


def iso_ms(t):
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"


def iso_s(t):
    return t.strftime("%Y-%m-%dT%H:%M:%SZ")


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def service_entry(s):
    return {"log_roots": [f"demo-systems/live/logs/{s['service']}"], "key_metrics": s.get("key_metrics", [])}


def setup():
    source = CONFIG if CONFIG.exists() else REPO / "mcp.config.example.json"
    if CONFIG.exists() and not BACKUP.exists():
        shutil.copyfile(CONFIG, BACKUP)
    cfg = json.loads(source.read_text(encoding="utf-8"))
    all_ = scenarios()
    for s in all_.values():
        cfg["services"][s["service"]] = service_entry(s)
    roots = cfg.setdefault("files", {}).setdefault("allowed_roots", [])
    if "demo-systems/live/logs" not in roots:
        roots.append("demo-systems/live/logs")
    cfg.setdefault("deploys", {})["feed"] = "demo-systems/live/deploys.json"
    cfg.setdefault("metrics", {})["series_dir"] = "demo-systems/live/metrics"
    # Link the code repo to the service whose scenario reads it, so an investigation of that service also searches its code.
    code_service = next((s["service"] for s in all_.values() if s.get("uses_code")), None)
    if code_service and cfg.get("code", {}).get("repo_url"):
        cfg["code"]["service"] = code_service
    write(CONFIG, json.dumps(cfg, indent=2) + "\n")

    # Start from the committed demo data so the seeded incidents keep working.
    (LIVE / "metrics").mkdir(parents=True, exist_ok=True)
    if not (LIVE / "deploys.json").exists():
        shutil.copyfile(HERE / "deploys.json", LIVE / "deploys.json")
    for m in (HERE / "metrics").glob("*.json"):
        shutil.copyfile(m, LIVE / "metrics" / m.name)
    for s in all_.values():
        (LIVE / "logs" / s["service"]).mkdir(parents=True, exist_ok=True)
    services = ", ".join(sorted({s["service"] for s in all_.values()}))
    print(f"Live demo ready: mcp.config.json now knows {services}.")
    print("Restart ResolveAI, then run:  python3 demo-systems/live-demo.py inject <scenario>")


def reset():
    if LIVE.exists():
        shutil.rmtree(LIVE)
    if BACKUP.exists():
        shutil.move(BACKUP, CONFIG)
        print("Restored your previous mcp.config.json.")
    elif CONFIG.exists() and "demo-systems/live" in CONFIG.read_text(encoding="utf-8"):
        CONFIG.unlink()
        print("Removed the live-demo mcp.config.json.")
    print("Live demo data removed. Restart ResolveAI to drop the live services.")


def fill(text, now):
    """Replaces {today} and {+Nd}/{-Nd} in scenario files with real dates, so certificate dates etc. stay current."""
    text = text.replace("{today}", now.strftime("%Y-%m-%d"))
    for sign in "+-":
        while "{" + sign in text:
            start = text.index("{" + sign)
            end = text.index("d}", start)
            days = int(text[start + 1:end])
            text = text[:start] + (now + timedelta(days=days)).strftime("%Y-%m-%d") + text[end + 2:]
    return text


def inject(s, now):
    service = s["service"]
    if s.get("deploy"):
        d = dict(s["deploy"])
        minutes = d.pop("minutes_ago")
        deploy = {"service": service, "version": d["version"], "deployed_at": iso_s(now - timedelta(minutes=minutes)), **d}
        feed_path = LIVE / "deploys.json"
        feed = [x for x in json.loads(feed_path.read_text(encoding="utf-8")) if x.get("commit") != deploy["commit"]]
        feed.append(deploy)
        write(feed_path, json.dumps(feed, indent=2) + "\n")

    # Logs: one line per entry, oldest first, "2026-11-18T10:04:12.403Z ERROR message" like the committed demo logs.
    lines = sorted(s["logs"], key=lambda x: -x[0])
    text = "".join(
        f"{iso_ms(now - timedelta(minutes=m) + timedelta(milliseconds=random.randint(0, 899)))} {level:<5} {message}\n"
        for m, level, message in lines)
    write(LIVE / "logs" / service / "app.log", text)
    for name, content in s.get("files", {}).items():
        write(LIVE / "logs" / service / name, fill(content, now))

    # Metrics: one point a minute for the last 40 minutes; each series is [unit, normal, after the change, minutes ago it changed].
    doc = {}
    for name, (unit, normal, after, changed_ago) in s.get("metrics", {}).items():
        points = [[iso_s(now - timedelta(minutes=m)), after if m <= changed_ago else normal] for m in range(40, -1, -1)]
        doc[name] = {"unit": unit, "points": points}
    write(LIVE / "metrics" / f"{service}.json", json.dumps(doc, separators=(",", ":")))


def show_form(s, now):
    f = s["form"]
    print(f"\nInjected '{s['id']}' at {iso_s(now)}. Open Investigate incident and enter:\n")
    for label, key in [("Title", "title"), ("Application name", "application"), ("Environment", "environment"),
                       ("Severity", "severity"), ("Customer impact", "impact"), ("Incident details", "details")]:
        print(f"{label}:\n{f[key]}\n")
    print(f"(Expected root cause, for the presenter: {s['expected_cause']})")


def main(argv):
    if not argv or argv[0] in ("-h", "--help", "help"):
        print(__doc__)
        return 0
    cmd = argv[0]
    all_ = scenarios()
    if cmd == "setup":
        setup()
    elif cmd == "reset":
        reset()
    elif cmd == "list":
        for s in all_.values():
            print(f"{s['id']:<22} {s['service']:<17} {s['form']['severity']}  {s['summary']}")
    elif cmd == "inject":
        if not (LIVE / "deploys.json").exists():
            print("Run 'python3 demo-systems/live-demo.py setup' first (and restart ResolveAI).", file=sys.stderr)
            return 1
        names = list(all_) if argv[1:] == ["all"] else argv[1:] or ["pinpoint-locale"]
        unknown = [n for n in names if n not in all_]
        if unknown:
            print(f"Unknown scenario: {', '.join(unknown)}. Choose from: {', '.join(all_)}", file=sys.stderr)
            return 1
        now = datetime.now(timezone.utc)
        for n in names:
            inject(all_[n], now)
        if len(names) == 1:
            show_form(all_[names[0]], now)
        else:
            print(f"Injected {len(names)} scenarios at {iso_s(now)}: {', '.join(names)}")
    else:
        print(f"Unknown command '{cmd}'. Use setup, inject <scenario>, list or reset.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
