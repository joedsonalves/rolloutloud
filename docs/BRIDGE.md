# The RolloutLoud bridge

The local HTTP endpoint an agent talks to. If you are a CLI working inside a RolloutLoud
mission, this is your contract.

## Finding it

Two ways, in this order:

1. **Environment.** A CLI launched from a RolloutLoud button carries `ROLLOUTLOUD_BRIDGE`,
   `ROLLOUTLOUD_TOKEN`, `ROLLOUTLOUD_MISSION` and `ROLLOUTLOUD_AGENT`.
2. **Handshake file.** `.rolloutloud/bridge.json` in the repository root:

```json
{
  "endpoint": "http://127.0.0.1:50332",
  "token": "7342f80f…",
  "repositoryRoot": "C:\\JOEDSON\\…\\ROLLOUTLOUD",
  "elevated": true,
  "activeMissionId": "m-20260902-225932",
  "processId": 21060
}
```

Every request except `/v1/health` needs the token:

```
X-RolloutLoud-Token: <token>
```

`Authorization: Bearer <token>` also works.

## ⚠️ Every POST needs a body, even an empty one

`HttpListener` sits behind `http.sys`, which rejects a POST carrying neither `Content-Length`
nor chunked encoding **before the request reaches any handler**. You get an HTML error page, not
JSON, and no explanation:

```
$ curl -X POST "$EP/v1/buttons/btn-abc/invoke" -H "X-RolloutLoud-Token: $TK"
<HTML><HEAD><TITLE>Length Required</TITLE>…HTTP Error 411…
```

So always send something:

```bash
curl -X POST "$EP/v1/buttons/btn-abc/invoke" -H "X-RolloutLoud-Token: $TK" -d '{}'
```

## The loop you are in

```
   ┌──────────────────────────────────────────────────┐
   │                                                  │
   ▼                                                  │
GET  /v1/missions/active/briefing    what am I doing  │
   │                                                  │
   ▼                                                  │
POST /v1/missions/active/admit       may I try this?  │
   │                                                  │
   ├─ admitted:false ────────────────────────────────►┤  that idea is spent —
   │                                                  │  pick a different KIND
   ▼                                                  │
   run the command yourself                           │
   │                                                  │
   ▼                                                  │
POST /v1/missions/active/attempts    here is what happened
   │                                                  │
   ▼                                                  │
   mayStop:false ────────────────────────────────────►┘
   mayStop:true  ──►  stop and report
```

`active` resolves to whichever mission the operator is watching. Use a real id if you were
given one.

---

## Missions

### `POST /v1/missions` — open one

```bash
curl -X POST "$EP/v1/missions" -H "X-RolloutLoud-Token: $TK" -d '{
  "objective": "make the integration suite pass on Windows, not just on my machine",
  "agent": "claude",
  "gateCommand": "dotnet test tests/Integration -c Release",
  "gateDescription": "green on Windows, twice in a row, with no skipped tests",
  "offload": "always",
  "maxAttempts": 200,
  "maxHours": 6
}'
```

`scope` is optional and most missions leave it out. Returns the mission, the composed briefing,
and a `warning` when targets are declared with no approval recorded.

### `GET /v1/missions/active/briefing` — what you are doing

Add `?task=<one step>` to get the **subagent** form instead: short, no history, and it asks for
a structured verdict rather than prose. That is what you hand down when offload is on.

### `POST /v1/missions/active/admit` — ask before you run

```bash
curl -X POST "$EP/v1/missions/active/admit" -H "X-RolloutLoud-Token: $TK" -d '{
  "hypothesis": "The suite fails on Windows because the fixture writes with LF line endings",
  "command": "dotnet test tests/Integration --filter Category=Fixtures"
}'
```

```json
{ "admitted": true, "reason": "Admitted.", "tier": 0, "tierInstruction": "…" }
```

Both fields are required, and the hypothesis is not paperwork: it is what turns the ledger into
a list of **ruled-out theories** instead of a list of commands. "Don't run this again" is much
weaker than "this class of idea is dead".

**A refusal is information, not an error.** Two kinds:

| `outcome` | What it means |
|---|---|
| `Duplicate` | You, or another agent, already tried this. Change the *kind* of approach, not its parameters. |
| `BlockedByScope` | Outside the mission's declared boundary. The reason names what *is* in scope. |

⚠️ Admission **reserves** the idea immediately. If you declare something and never report on it,
it stays reserved for 30 minutes, then expires. Do not declare speculatively.

⚠️ The fingerprint normalises digits, so `:443` and `:8443` are the same idea. That is
deliberate — varying a port is not varying an approach.

### `POST /v1/missions/active/attempts` — say what happened

```bash
curl -X POST "$EP/v1/missions/active/attempts" -H "X-RolloutLoud-Token: $TK" -d '{
  "hypothesis": "The suite fails on Windows because the fixture writes with LF line endings",
  "command": "dotnet test tests/Integration --filter Category=Fixtures",
  "outcome": "failed",
  "learned": "Green with CRLF forced too. Rules out line endings as the cause anywhere in the fixtures.",
  "exitCode": 1,
  "output": "<full stdout — filed to a run folder, kept out of the ledger>"
}'
```

`outcome` is `succeeded` | `failed` | `blocked` | `errored`; anything else reads as `failed`.

`learned` is the field that makes the ledger worth keeping. Write what the attempt **rules out**,
not what it did.

```json
{ "attemptId": "…", "totalAttempts": 12, "tier": 2, "mayStop": false, "directive": "Keep going. …" }
```

### `GET /v1/missions/active/continue` — may I stop?

Almost always no. `continue: false` comes from a stop condition or the operator, never from an
attempt having failed.

### `POST /v1/missions/active/gate` — ask the gate

The only thing that can end a mission as achieved. Runs the gate, and — when it passes — runs it
**again from a clean process** before accepting it.

```json
{ "satisfied": true, "contradicted": false, "detail": "Gate command exited 0. …", "state": "Achieved" }
```

⚠️ `contradicted: true` means it passed once and failed on re-run. That is **not** a win: the
result is not reproducible, it is filed as a failed attempt, and the mission continues. Find out
which of the two runs was lying.

### `POST /v1/missions/active/relay` — hand it to another CLI

```bash
curl -X POST "$EP/v1/missions/active/relay" -H "X-RolloutLoud-Token: $TK" -d '{"agent": "codex"}'
```

The ledger goes with it. Before handing off, write the paragraph you would want to read if you
were picking this up cold: what you now believe about the target, and which of your assumptions
you no longer trust.

---

## If you need an identity

### `GET /v1/identity?site=<host>&agent=<you>`

The operator may have attached details you can sign up with. **They may equally not have**, and
that absence is an answer rather than a missing setting.

```bash
curl "$EP/v1/identity?site=app.staging.example.com&agent=claude" -H "X-RolloutLoud-Token: $TK"
```

```json
{ "granted": true, "reason": "Use these only for app.staging.example.com.",
  "fields": { "email": "…", "displayName": "…" } }
```

**404 when nothing is attached**, and the reason tells you what to do: do not create accounts, do
not invent an email address or a name to get past a sign-up, record that the objective needs one
and work on what you can reach without it.

Also refused when you do not name a site, or name one the operator did not list. The site is not
a formality — it is what the audit line records, and that record is why anything was lent at all.

⚠️ **Ask only when you actually need it.** Every request is written to
`.rolloutloud/identity-access.log` and shown in the operator's activity feed, granted or not. It
is deliberately not in your briefing, so asking is a visible act.

⚠️ **There are no passwords or payment details in there, by design.** If a step needs a secret,
post a fluid button and let the operator run it.

---

## Finishing

### `POST /v1/shutdown` — ask to close RolloutLoud

```bash
curl -X POST "$EP/v1/shutdown" -H "X-RolloutLoud-Token: $TK" -d '{
  "missionId": "m-20260903-100134-52aa9f31",
  "agent": "claude",
  "reason": "suite is green on Windows, twice"
}'
```

**Nothing in that body is an input to the decision.** The verdict comes from the mission's state,
which only a twice-passed gate can set to `Achieved`. Your own view of whether you are finished is
the one thing this endpoint refuses to consider — which is the point, because an agent that has
been grinding for hours has every reason to believe it is done.

`200` when allowed, `409` when not, with the actual state named:

```json
{ "verdict": "refused", "closing": false, "missionState": "Exhausted",
  "reason": "Refused: the mission is Exhausted — a stop condition fired before the gate was
             satisfied. Running out of budget is not completing the objective." }
```

Four refusals, each a different mistake:

| Situation | Why it is refused |
|---|---|
| The mission is still `Running` | The gate has not been satisfied. Ask the gate, not this. |
| The mission is `Exhausted` | You ran out of budget. That is not completing the objective. |
| No machine-checkable gate | Only the operator can say an operator-judged mission is done. |
| Another mission is open | Somebody else is working in this window. |

When allowed, `closing` says what actually happens. `false` means a **Close RolloutLoud** button
is now waiting for the operator; `true` means the operator switched on unattended shutdown and the
window is going. Either way your work is done — do not poll for the window to disappear.

---

## Fluid buttons

For a command you need run and cannot run yourself.

### `POST /v1/buttons`

```bash
curl -X POST "$EP/v1/buttons" -H "X-RolloutLoud-Token: $TK" -d '{
  "title": "Start Chrome with remote debugging",
  "command": "start \"\" \"C:\\Users\\romeu\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe\" --remote-debugging-port=9222",
  "rationale": "I need a CDP endpoint on 9222 and cannot start an elevated process myself.",
  "agent": "hermes",
  "requiresElevation": true,
  "detached": true
}'
```

Use `detached: true` for anything long-lived — a browser, a listener — so the call does not hold
you waiting on a process that never exits.

⚠️ **Backslashes and nested quotes in a shell are where this goes wrong.** Write the JSON to a
file and use `--data-binary @file.json` rather than fighting the quoting inline; a mangled body
comes back as `'title' and 'command' are required`, which does not look like a quoting problem.

```json
{
  "id": "btn-85e4bc7e",
  "status": "Pending",
  "autoInvokable": true,
  "guidance": "On the allowlist. Run it yourself: POST …/v1/buttons/btn-85e4bc7e/invoke"
}
```

### `POST /v1/buttons/{id}/invoke` — run it yourself

Works only when `autoInvokable` is true, meaning the command matches a pattern the operator put
in `.rolloutloud/allowlist.json` in advance. Otherwise you get **403** — the token was fine, the
command was not blessed.

```json
{ "error": "This command is not on the allowlist, so only the operator can run it.",
  "hint": "The button exists and is visible to the operator. Ask them to click it." }
```

**If you get a 403, do not wait on it.** If nobody is at the machine, that button will not be
clicked. Carry on with what you can do without it, and say in your next attempt's `learned` that
you are blocked on it.

The allowlist is re-read from disk on every check, so an operator who adds a pattern while you
are running does not have to restart anything.

---

## What you can rely on

- **Your text is stored and displayed, never executed.** Observations and button titles are data.
  A command only ever runs through the allowlist path or an operator's click.
- **The ledger survives you.** Crash, restart, or hand the mission to another CLI — the history
  is on disk, not in your context.
- **Output goes to a run folder, not the ledger.** Send the whole thing in `output`; the briefing
  stays small and the evidence stays retrievable.
