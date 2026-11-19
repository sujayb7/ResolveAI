# GreenPay security demo — presenter script

ResolveAI investigates the deliberately-vulnerable **PHP_GreenPay** app: it reads the incident **logs** (files source) and the **code** (GitHub source) and produces an evidence-backed root cause that cites the exact buggy line, plus a recommended fix. No PHP server is required.

## One-time setup (already done on this machine)

```bash
# In the ResolveAI repo:
python3 demo-systems/live-demo.py setup     # maps a "greenpay" service and links the GitHub repo to it
# restart the app:  dotnet run --project src/ResolveAI.App
```

`setup` writes `mcp.config.json` with a `greenpay` service (logs) and `code.service: greenpay` (so an investigation of greenpay also searches the repo's code). The GitHub repo is `https://github.com/sujayb7/PHP_GreenPay.git` (must be public).

## How ResolveAI knows to check the GitHub code

`mcp.config.json` links the repository to an application: `code.service: greenpay`. When you create an incident with **Application name = greenpay**, the investigation automatically searches the repository's code (source files first, docs last) for words from the title and details, and cites the matching lines. You can see the link on the **Systems** page, and the **Investigate incident** form shows a hint as soon as you type `greenpay`.

**`inject` is optional.** The code check needs no setup per demo. `inject` only adds fresh, timestamped logs, a deploy and metrics, so the investigation also has runtime evidence (who attacked, when, what moved). Without it, ResolveAI still finds the bug from the code.

## Running one incident

Optional, for richer evidence: just before you present, stage fresh, timestamped logs:

```bash
python3 demo-systems/live-demo.py inject greenpay-login-bypass    # or greenpay-treasury-key
```

Then in the app: **Investigate incident** → paste the ticket below → **Run investigation**. Do it within ~25 minutes of `inject` (the evidence is timestamped relative to "now").

> Tip: `inject` also prints the ticket text and the expected root cause for you.

---

## Incident ticket #1 — Login auth bypass (SQL injection + md5)

| Field | Value |
| --- | --- |
| **Title** | `Admin account signed in without the correct password` |
| **Application name** | `greenpay` |
| **Environment** | `production` |
| **Severity** | `SEV-1` |
| **Customer impact** | `An attacker signed in as admin and moved funds out of the operations account.` |
| **Incident details** | `An admin session was created from an external IP using a crafted username value and no valid password. The login query appears to be built from the username, and the password is hashed with md5 rather than verified with password_verify. Large transfers left the operations account moments later. The afternoon PAY-318 login change is the only recent release.` |

**What ResolveAI finds (expected):** *SQL injection and weak md5 password check introduced by the PAY-318 single-query login change* — **high confidence**, citing `src/Auth.php` (~line 19) and the PAY-318 deploy. **Check this first:** review the PAY-318 diff and the current `src/Auth.php` login code.

**The real bug (for your narration):** The PAY-318 "hotfix" rewrote the login to run one query, building SQL by **concatenating the username** (`WHERE username = '$username' …`) and hashing passwords with **`md5()`** instead of `password_verify()`. So a username like `admin'--` comments out the password check and signs in as admin. It also dropped `session_regenerate_id()`, adding session fixation.
**Fix:** a prepared statement with a bound `:username`, and `password_hash()`/`password_verify()`; restore session regeneration.

---

## Incident ticket #2 — Treasury balance exposed (PHP type juggling)

| Field | Value |
| --- | --- |
| **Title** | `Treasury balance exposed through the balance API without a valid key` |
| **Application name** | `greenpay` |
| **Environment** | `production` |
| **Severity** | `SEV-2` |
| **Customer impact** | `The balance API returned the treasury account balance to an external caller that supplied key=0.` |
| **Incident details** | `The balance API compares the supplied key against the stored api_key with a loose comparison. The treasury api_key was generated with md5 and begins with 0e, so a caller passing key=0 is treated as equal. There was no deployment; this looks like a long-standing flaw in the comparison.` |

Stage it with `python3 demo-systems/live-demo.py inject greenpay-treasury-key`.

**The real bug:** `public/api/balance.php` compares the key with `!=` (loose comparison). The treasury key is an `0e…` "magic hash", and PHP treats two `0e…` strings as the number 0, so `?key=0` equals the stored key. There is **no deploy** — a good test that ResolveAI doesn't just blame the latest release.
**Fix:** compare with `hash_equals()` and store a real random key.

---

## Suggested 3-minute demo flow

1. **Systems** tab: point out the **GitHub repository** source (connected) alongside logs, deploys and metrics, and the **Test connection** button.
2. `inject greenpay-login-bypass`, then **Investigate incident** with ticket #1. Show the live stages running.
3. On **AI findings**: read the top explanation and the **"Check this first"** box naming `src/Auth.php`. Click an evidence chip (e.g. a `code_search` result) to show it pointing at real repository code.
4. **Connected-source checks** tab: show the `code_search` / `files_search` / `deploys_recent` calls that produced the evidence — all read-only.
5. Note the **"AI-generated — not confirmed"** label and the human-in-the-loop: **Record what you found** → **Draft RCA**.
6. (Optional) Run ticket #2 to show a no-deploy incident where the AI still finds the flaw in the code.

### Optional: show the live app

```bash
cd /path/to/PHP_GreenPay && ./install.sh && ./run.sh      # http://127.0.0.1:8080
```
Sign in as `alice / alice-wallet-42`, or demonstrate the bug by signing in with username `admin'--` and any password. This is only to show the app itself; ResolveAI doesn't need it.

> Note: the repo's `docs/SECURITY-DEMO.md` documents the bugs. ResolveAI may read it too. Delete that file from the repo if you want the AI to work purely from the source code and logs.
