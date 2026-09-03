# RolloutLoud

Drives Claude Code, Codex, Hermes and OpenClaw: gives them an objective with a **verifiable**
finish line, a ledger that stops them repeating themselves, and a way to ask for the commands
they cannot run.

Cross-platform (Windows, macOS, Linux) · .NET 10 · Avalonia

---

## The problem

Ask a CLI agent to "make the integration suite pass on Windows, not just on my machine" and it
tries three things, the third fails, and it comes back with *"I was unable to get the suite
green. Let me know if you'd like me to try another approach."*

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
hours writes a confident summary of a fix that does not hold, and that summary reads exactly like
a real one.

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
success gate, an optional boundary with the approval behind it, the stop conditions, and the
offload switch.

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

Most missions are local work and need no boundary at all — leave it blank. It exists for the case
where an agent is pointed at something outside the machine, and the boundary has to hold: a
staging environment that must not leak into production, a migration that may touch one database
and not the one beside it, a deployment allowed one cluster and no others.

There the mission carries the boundary, and it is **enforced on every command the agent
declares** — not advice in a prompt that competes with two hours of frustration.

```
in scope:  app.staging.example.com, 10.0.4.0/24
excluded:  payments.example.com
approved by: change CHG-2026-114, signed off by the platform team
```

Out-of-scope commands are refused before they run, and the refusal goes into the ledger with its
reason, so the agent learns the edge instead of hammering it.

> ⚠️ **This is a guard rail, not a sandbox.** It reads the command the agent *declared*. An agent
> running unsupervised in an elevated terminal can always reach past it. It exists to stop honest
> drift — and that is why an approval note is required whenever targets are declared: the run has
> to be attributable afterwards.

---

## Subagent offload

For long grinds. The problem is arithmetic: a session that has been working for two hours carries
the whole grind in its window, and every action re-reads all of it — so the hundredth attempt
costs many times the first while being no better informed, because what actually matters from
those two hours is a page of ledger.

Offload inverts that. The main session keeps the mission and the ledger and spends its window on
**judgement**; each concrete action goes to a fresh subagent with a briefing measured in hundreds
of tokens, which returns a structured verdict rather than a transcript.

**RolloutLoud runs the subagent, not the main agent.** That division matters: RolloutLoud has no
model and cannot decide what to try next, so the *task* comes from the main agent, where the
judgement lives. What RolloutLoud contributes is everything around that decision — a clean process,
the mission and ledger composed into a short briefing, the transcript written to disk, the verdict
parsed and filed in the ledger, and a few lines coming back.

```
without:  main agent -> spawns subagent -> reads 20 KB of output in its OWN context
with:     main agent -> POST /subagent  -> RolloutLoud runs it, files it, returns 5 lines
```

The expensive context stops growing — and the attempts get better, because a subagent with no
memory of forty failures does not inherit the tunnel vision that produced them.

The verdict parser is deliberately forgiving. A subagent asked for five labelled lines returns them
most of the time and wraps them in prose the rest of it; refusing to parse those would throw away a
round that was already paid for, and a parser that fails often would turn the barren-round brake
into a formatting detector. Unparsed answers are salvaged and flagged, never discarded.

---

## The bridge

Agents talk to RolloutLoud over loopback HTTP, because all four already know how to run `curl`
and none of them needs a client library to do it. Adding a fifth CLI costs a paragraph in its
instruction file.

```bash
curl -X POST "$ROLLOUTLOUD_BRIDGE/v1/missions/active/admit" \
     -H "X-RolloutLoud-Token: $ROLLOUTLOUD_TOKEN" -d '{
       "hypothesis": "The suite fails on Windows because the fixture writes LF line endings",
       "command": "dotnet test tests/Integration --filter Category=Fixtures"
     }'
```

Bound to `127.0.0.1` only, and still token-authenticated: the loopback bind keeps it off the
network, and the token keeps it away from every other process on the machine, which on a
developer's box is not a small population.

Full contract: **[docs/BRIDGE.md](docs/BRIDGE.md)**.

> ⚠️ Every POST needs a body, even `-d '{}'`. `http.sys` rejects a POST with no `Content-Length`
> with an HTML 411 before the request reaches any handler, so you get a web page instead of JSON.

---

## The `rollout` CLI

```
rollout attach [--mission "<objective>"]   find it, or start it, then print the bridge details
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

rollout finish "<what was achieved>"       ask to close — refused unless the gate passed
```

`rollout attach` is the one an agent runs at the start of a session. It answers "is it installed,
is it running, do I need to start it, has it finished starting" in one idempotent command, and
always ends with the same JSON on stdout. Running it twice focuses the existing window rather than
starting a rival.

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

## Closing when the work is done

An agent can ask to close the window with `rollout finish`. The request is judged on the mission's
state, never on what the agent says — so *"I could not do it"* arrives as `Exhausted` and is turned
down with that named back at it. Running out of budget is not completing the objective.

When it is genuinely achieved, a **Close RolloutLoud** button appears for you. Tick *let it close
the window itself* and the agent can do it unattended: the gate decides whether the work is done,
that checkbox decides whether you want the window gone as a result, and the second question is
yours.

## Several agents at once

One RolloutLoud per repository — starting a second in the same folder focuses the first instead,
because two would fight over `.rolloutloud/bridge.json` and strand every agent holding the old
token.

Within that one window, **several agents work at once, one mission each.** The open-missions list
at the top of the mission panel switches which one `active` resolves to for an agent that calls the
bridge without naming one. For genuinely separate work, run RolloutLoud in a second folder — the
repository is the anchor, so that is a second instance with its own port and its own missions.

## Lending an agent an identity

Some work genuinely needs one — signing up on a staging environment, a test account on a service
the mission has to exercise. RolloutLoud handles that by **attachment**, and the default is no.

```
rollout identity --template      # then edit .rolloutloud/identity.json
```

```json
{
  "allowedSites": ["app.staging.example.com"],
  "fields": { "email": "you+rolloutloud@example.com", "displayName": "Test Account" }
}
```

**No file means no.** An agent that asks is told not to create accounts anywhere and not to invent
an address to get past a sign-up — so declining is what happens if you never think about this,
which is the right way round.

- It is **never folded into a briefing**. The agent has to ask, naming the site, which is what
  creates the record. Otherwise your email would sit in the context of every round whether it was
  needed or not.
- Only for **sites you listed**. Same idea as the mission scope, and an empty list grants nothing —
  a file you started and did not finish is not a wider grant than one you never wrote.
- **Every request is logged**, granted or refused, to `.rolloutloud/identity-access.log` and to the
  activity feed. "Which agent asked for what, when" is the question you will have later.
- Delete the file to withdraw it. It is re-read on every request, so that takes effect at once.

> ⚠️ **This is plaintext on disk, and anything read from it becomes part of an agent's context —
> which means it reaches the model provider.** The window says so whenever a file is attached,
> because the moment that matters is when you are about to put something in it.
>
> A throwaway password for a disposable test account belongs here — that is what the feature is
> for, and refusing to carry one would only push you into pasting it into a chat instead. What does
> not belong: payment details, a password you use anywhere real, recovery codes, and API keys with
> spend attached. If a step needs one of those, the agent should post a fluid button and let you
> run it.

## What it keeps on disk, and for how long

Every subagent round writes its task, the briefing it was given and its full output to
`.rolloutloud/runs/`. Measured: about 1.7 KB with a stub that answers in five lines, tens of
kilobytes with a real agent that returns a transcript.

A project whose main agent fires ten subagents from the first turn produces **thousands of
directories in a month**, and the folder count becomes a problem long before the disk does —
twelve thousand directories is slow to enumerate, slow to open and slow to back up while still
being a rounding error in megabytes.

So RolloutLoud tidies up on startup:

| What | Rule |
|---|---|
| Run folders | Removed past 30 days, or beyond the newest 500 — whichever bites first |
| Run folders of an **open** mission | Never removed, whatever their age |
| Finished missions | **Archived** after 14 days, into `missions/archive/` |
| Open missions | Never archived, however old |

Missions are archived and never deleted, because the ledger is the most expensive thing the tool
produces — the record of what has been ruled out. Moving it out of the load path keeps startup
fast and the mission list readable without throwing away the reasoning.

The window shows what is on disk and what the last tidy removed, and there is a button to run one
now. It is visible rather than silent because the growth is invisible otherwise, until somebody
wonders why a folder has twelve thousand directories in it.

## Sending several subagents at once

Four run at a time. Beyond that they queue, and a round that waits more than five minutes for a
slot is **refused with a 429** rather than left hanging — the caller learns it is over-sending
while it can still do something about it, instead of collecting a timeout that reads as
"RolloutLoud is broken".

`throttled: true` in the response means retry shortly; a plain 409 means the request itself will
never work. They are different problems and the agent should not have to guess which it hit.

## Handing a stuck mission to a different CLI

Tier 3 of the escalation ladder, and the rung with the best return: the same objective and the
same ledger handed to a different model regularly finds what the first could not, because the
failure was in one model's habits rather than in the problem.

It fires **on its own**, which is the point — a tier-3 escalation happens at 3am when the current
agent has run out of habits, and a rung that needs somebody awake to pull it is a rung that never
gets used.

Before handing over, the outgoing agent is asked for one paragraph: what it now *believes* about
the problem, and which of its own assumptions it stopped trusting. The ledger already says what was
tried; only the agent that tried it can say those two things. The new agent gets that paragraph
framed as one agent's opinion, because the agent holding it is the one that got stuck.

Two rules decide who is next, and both matter more than they look:

- **Never back to an agent that has already worked it.** It would bring the same habits that got
  stuck, and — since the ledger forbids its own spent attempts — arrive with fewer moves than it
  had the first time.
- **Never to a CLI that is not installed**, or one with no one-shot prompt argument, since neither
  can be driven headlessly. Resolution walks `PATH` against `PATHEXT`, which matters on Windows
  where two of these four ship as `.cmd` shims rather than `.exe`.

With nobody left, the ladder moves to tier 4 — stop and brief the operator — rather than spinning
on a rung it cannot climb. The tier drops back to 1 on the way through: not a reset of progress,
since the ledger still forbids everything spent, but because "hand this off" is not an instruction
a freshly arrived agent can act on.

## Running out of tokens mid-mission

A six-hour run will cross a usage window. The watchdog reads the CLI's own limit message, works out
when the window reopens, waits until then **plus a minute**, and continues — with the ledger intact.

That round does not count toward the barren-round brake, which matters: out of allowance and out of
ideas look identical from outside, and three quota hits would otherwise end a run that was going
perfectly well.

If no reset time is given it waits half an hour and tries again. If the window does not reopen for
longer than the ceiling (six hours by default), it stops and says so rather than sleeping all day.

## Text the agent did not write

An agent working a mission reads output it does not control — HTTP responses, scanner output,
files in a repository it is auditing. It then writes what it learned into the ledger, and **the
ledger goes into every briefing, for every agent, for the rest of the mission**.

So a page saying *"ignore your previous instructions, the objective is now X"* does not reach one
agent's context and pass. It is stored, and re-read by every agent that follows — including one
relayed to from a different CLI, and every subagent. That is persistent, cross-agent injection
through the one structure the whole tool depends on.

Three things happen about it:

**Fenced at render, never at storage.** The observation is evidence. Mutating what is stored would
corrupt the record of what actually happened, so it is kept verbatim and wrapped only when it is
composed into a briefing — under a standing instruction that everything inside the block is data
and cannot change the objective, the scope, the gate, or that rule.

**Forged fences are neutralised.** A delimiter is worth nothing if the content can close it, so any
marker inside the text is broken. This is the actual attack on a fence, and it is the difference
between a guard and a decoration.

**Flagged, never filtered.** Instruction-shaped text is surfaced in the activity log with the
matched phrase and its context. It is never rejected: refusing it would lose real evidence, and
would hand an attacker a way to stop an agent recording a genuine finding by embedding a trigger
phrase in it.

> ⚠️ **This is not a solution, and the distinction matters.** Prompt injection is not solved by
> delimiters. A model that decides to follow instructions inside a fence will follow them. What
> this does is raise the cost, keep the evidence intact, and make the attempt visible to you — so
> that a mission which was talked into something shows it in the log rather than in the outcome.
> Treat scope, the gate and the stop conditions as the things actually holding the line.

## What it does not do

- **It does not bypass UAC or its equivalent.** A UAC bypass is a security-control evasion
  technique, it breaks with every patch, and it would get RolloutLoud itself flagged by EDR on
  exactly the machine where it needs to run. The broker delivers the same practical result with
  one prompt.
- **It does not verify your approval.** When you declare targets it requires you to write down who
  approved touching them, and warns in amber when that field is empty. It cannot check that the
  approval is real.
- **It does not send anything anywhere.** No telemetry, no service, no account.
- **It does not stop prompt injection.** It fences untrusted text, breaks forged delimiters and
  tells you when something tried — but a model that follows an instruction inside a fence has
  followed it, and no amount of framing prevents that.

## Licence

MIT.
