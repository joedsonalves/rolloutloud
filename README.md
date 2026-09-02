# RolloutLoud

Drives Claude Code, Codex, Hermes and OpenClaw: gives them an objective with a **verifiable**
finish line, a ledger that stops them repeating themselves, and a way to ask for the commands
they cannot run.

Cross-platform (Windows, macOS, Linux) · .NET 10 · Avalonia

---

## The problem

Ask a CLI agent to "attack within scope until you land a critical" and it tries three things,
the third fails, and it comes back with *"I was unable to find a critical vulnerability. Let me
know if you'd like me to try another approach."*

That is not a capability problem. It is a question of **who decides it is finished** — and there
are four separate reasons behind it:

| Reason | What happens | What RolloutLoud does |
|---|---|---|
| The agent judges its own success | Declares victory early, or defeat early | A **success gate** the tool evaluates |
| The agent forgets what it tried | Repeats one idea with new parameters | A **ledger** with attempt fingerprints |
| The agent runs out of *kinds* of idea | Generates variations of the last one forever | An **escalation ladder** |
| A long context makes each action expensive | The hundredth action costs many times the first | **Subagent offload** |

Plus two operational ones: it stops for commands it cannot run (**fluid buttons**), and "respect
the scope" is a request rather than a rule (**scope enforced before every command**).

## The one rule

> **The agent never decides on its own that it is done.**

A mission carries a `SuccessGate` — a command whose exit code ends the run. The agent cannot
declare victory; it produces evidence and asks the gate. And a satisfied gate is **run a second
time, from a clean process**, before it is believed: an agent that has been grinding for two
hours writes a confident summary of a critical it did not find, and that summary reads exactly
like a real one.

---

## Getting started

```powershell
git clone https://github.com/joedsonalves/rolloutloud
cd rolloutloud
dotnet build
dotnet run --project src/RolloutLoud.App
```

Or, from inside any of the four CLIs:

```powershell
rollout install
```

**The folder you run it from is the anchor.** Every elevated CLI and every fluid button opens
there.

---

## What the window gives you

**Four CLIs, two buttons each.** Normal, and elevated — the red one, which turns the CLI's
approval prompts off. The exact command line is shown under each button before it runs.

| CLI | Elevated |
|---|---|
| Claude Code | `claude --dangerously-skip-permissions` |
| Codex | `codex --dangerously-bypass-approvals-and-sandbox` |
| Hermes | `hermes chat --yolo` |
| OpenClaw | *(no bypass flag — see below)* |

> ⚠️ **OpenClaw has no single bypass flag.** Its permission lives in `openclaw approvals` and
> `openclaw exec-policy`, which are persisted host state rather than a launch argument. Its
> elevated button only elevates the process; set the exec policy once, by hand.

These four are **data, not code**: defaults live in `AgentCatalog`, overrides in
`.rolloutloud/agents.json`. They ship new flags constantly, and fixing one should be a JSON edit,
not a release.

**A mission box.** Write the outcome you want, not the steps. It carries the objective, the
success gate, the engagement scope with its authorisation, the stop conditions, and the offload
switch.

**Fluid buttons.** A panel where commands requested by agents appear.

**A ledger.** What has been ruled out — fed back into every briefing, so a spent idea stays spent
across restarts and across CLIs.

---

## Elevation

RolloutLoud **does not bypass UAC**, and that is a decision rather than a limitation.

It becomes the **broker**: you elevate RolloutLoud once, consenting once, and from then on every
process it starts inherits that token with no further prompt — including fluid buttons the agent
invokes itself. One consent, recorded, at a moment you chose.

> ⚠️ **A child never holds more privilege than its parent.** Clicking an elevated button from an
> unelevated RolloutLoud starts the CLI with its bypass flag but *without* administrative rights,
> and the difference only shows up an hour later when a privileged command fails. That is why the
> warning has three answers, not two: elevate and restart, launch anyway, or cancel.

---

## Fluid buttons

The case that motivated them is small and completely real: Hermes needs Chrome listening on port
9222 and cannot start it. Today that ends the run — the agent says what it needs and waits for a
human who is asleep.

With a fluid button, the agent posts the command, it appears in the window, and RolloutLoud runs
it. If the command matches a pattern you put in `.rolloutloud/allowlist.json` in advance, the
agent can run it **itself** and never stops at all.

```json
[
  "*chrome* --remote-debugging-port=*"
]
```

The allowlist **fails closed** in every direction — missing file, unreadable file, malformed
JSON, empty pattern, and a bare `*` — all yield "no auto-invocation", never "allow". A bare `*`
is rejected on purpose: it is the pattern a tired operator writes at 2am to stop being
interrupted, and it turns the allowlist into decoration.

It is re-read from disk on every check, so adding a pattern mid-run takes effect immediately.

---

## Scope

For engagement work, the mission carries the boundary, and it is **enforced on every command the
agent declares** — not advice in a prompt that competes with two hours of frustration.

```
in scope:  app.example.com, 10.0.4.0/24
excluded:  payments.example.com
authorised by: engagement REF-2026-114, signed by the client CISO
```

Out-of-scope commands are refused before they run, and the refusal goes into the ledger with its
reason, so the agent learns the edge instead of hammering it.

> ⚠️ **This is a guard rail, not a sandbox.** It reads the command the agent *declared*. An agent
> running unsupervised in an elevated terminal can always reach past it. It exists to stop honest
> drift — and that is why authorisation is required whenever targets are declared: the run has to
> be attributable afterwards.

---

## Subagent offload

For long grinds. The problem is arithmetic: a session that has been working for two hours carries
the whole grind in its window, and every action re-reads all of it — so the hundredth attempt
costs many times the first while being no better informed, because what actually matters from
those two hours is a page of ledger.

Offload inverts that. The main session keeps the mission and the ledger and spends its window on
**judgement**; each concrete action goes to a fresh subagent with a briefing measured in hundreds
of tokens, which returns a structured verdict rather than a transcript.

The expensive context stops growing — and the attempts get better, because a subagent with no
memory of forty failures does not inherit the tunnel vision that produced them.

---

## The bridge

Agents talk to RolloutLoud over loopback HTTP, because all four already know how to run `curl`
and none of them needs a client library to do it. Adding a fifth CLI costs a paragraph in its
instruction file.

```bash
curl -X POST "$ROLLOUTLOUD_BRIDGE/v1/missions/active/admit" \
     -H "X-RolloutLoud-Token: $ROLLOUTLOUD_TOKEN" -d '{
       "hypothesis": "The login form concatenates the username into SQL",
       "command": "sqlmap -u https://app.example.com/login --batch"
     }'
```

Bound to `127.0.0.1` only, and still token-authenticated: the loopback bind keeps it off the
network, and the token keeps it away from every other process on the machine — which on a pentest
box is not a hypothetical population.

Full contract: **[docs/BRIDGE.md](docs/BRIDGE.md)**.

> ⚠️ Every POST needs a body, even `-d '{}'`. `http.sys` rejects a POST with no `Content-Length`
> with an HTML 411 before the request reaches any handler, so you get a web page instead of JSON.

---

## The `rollout` CLI

```
rollout install [--no-open]      build and open, anchored here
rollout open [--elevated]
rollout status

rollout mission "<objective>" --gate "<command>" --scope a,b --auth "<who authorised it>"
rollout briefing ["<subagent task>"]
rollout admit    "<hypothesis>" "<command>"
rollout attempt  "<hypothesis>" "<command>" --outcome failed --learned "…"
rollout continue
rollout gate

rollout button --title "<label>" --command "<cmd>" [--elevated] [--detached]
rollout invoke <button-id>
```

---

## Building

```powershell
dotnet build
dotnet test tests/RolloutLoud.Core.Tests    # 16 tests, ~60 ms, no elevation needed
```

> **Kill `RolloutLoud.exe` before building.** It locks its DLLs and the build fails to copy —
> sometimes silently, and then you run old code. If the window is elevated and your terminal is
> not, `taskkill` will not reach it; close the window.

```
src/RolloutLoud.Core/              no UI — runs in a test with no window
src/RolloutLoud.App/               Avalonia 12
src/RolloutLoud.Cli/               rollout
src/RolloutLoud.Platform.Windows/  UAC
src/RolloutLoud.Platform.Unix/     osascript / pkexec
tests/RolloutLoud.Core.Tests/
```

Machine-local state lives in `.rolloutloud/` at the repository root — missions, run folders, the
bridge token, the allowlist. It is git-ignored: it carries a live credential and target output.

---

## What it does not do

- **It does not bypass UAC or its equivalent.** A UAC bypass is a security-control evasion
  technique, it breaks with every patch, and it would get RolloutLoud itself flagged by EDR on
  exactly the machine where it needs to run. The broker delivers the same practical result with
  one prompt.
- **It does not verify your authorisation.** It requires you to write down who authorised the
  engagement, and warns in amber when that field is empty. It cannot check that the engagement
  exists.
- **It does not send anything anywhere.** No telemetry, no service, no account.

## Licence

MIT.
