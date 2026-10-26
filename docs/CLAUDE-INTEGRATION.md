# Claude integration

PC Checker can talk to [Claude](https://www.anthropic.com/claude) to explain
what it finds and to plan an overclock. The feature is optional and does
nothing until you add an API key: no key, no network traffic.

## What you get

- **Ask Claude** page: a chat that already knows your hardware, health score,
  temperatures, slowdown findings and installed software, so you can ask
  "why does my laptop get hot when idle?" or "which of these startup apps can I
  turn off?" and get an answer about *your* PC. Answers stream in as they are
  written; press **Stop** to cut one short. Every message has a **Copy**
  button and the chat is kept only in memory (it is gone when the app closes).
- **Explain** links around the app: other pages can hand a question to the
  Ask Claude page ("Explain this finding…"), which sends it on arrival.
- **Overclock advisor** (Advanced mode, Overclock page): give it your goal
  ("quiet", "max performance", "balanced") and it returns a staged plan: small
  steps, a stress test after each, abort conditions, expected gain. You apply
  each step yourself; the app never applies a plan automatically.

## Setup

1. Create an API key at <https://console.anthropic.com/>.
2. Open **Ask Claude**, paste the key into the setup card and click
   **Save key**. The app tests the connection straight away.
3. Pick a model and effort level. The defaults are a good balance of speed and
   quality. The settings card can be reopened any time with the **Settings**
   button in the page header.
4. Decide whether to **Share scan context**. On by default; turn it off if you
   want a general-purpose chat with nothing about your PC attached.

Alternative for developers and scripts: set the `ANTHROPIC_API_KEY`
environment variable before starting PC Checker. It is used only when no key
is saved in settings, and the page says so.

Usage is billed by Anthropic to your account. A typical question with scan
context costs a fraction of a cent; the overclock advisor uses more because
it reads a larger hardware profile. There is no subscription inside PC Checker.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Model | `claude-opus-5-5` | Which Claude model answers. The page offers Opus 5.5, Sonnet 5.5 and Haiku 5.5; any other id in `settings.json` is honoured as typed. |
| Effort | Medium | Low is faster and cheaper; High thinks longer about tricky problems. |
| Share scan context | On | Attach a summary of the latest scans to each message. |
| Max context characters | 12,000 | Upper bound on the attached summary (`Claude.MaxContextChars` in `settings.json`). |
| API key | (none) | Stored with Windows DPAPI, current user only. |

## Where the key lives

The key is encrypted with the Windows Data Protection API for the current
Windows account and the result is stored as base64 in
`%LOCALAPPDATA%\PcChecker\settings.json` (`Claude.ProtectedApiKey`). Another
user on the same PC, or a copy of the file on another machine, cannot decrypt
it. **Remove saved key** on the page deletes it. The key is never written to
the logs; the logs only record that a request was made, how many turns it
carried and how many tokens came back.

## What is sent, and when

Nothing is sent until you press **Send** (or click a suggested question, or a
page hands over an "Explain…" question).

Each chat request carries:

- the built-in system prompt (plain instructions about tone and safety);
- with *Share scan context* on, a text summary built from the latest scans:
  system, CPU, GPU, memory, disks, network adapters, health items that are
  not *Good*, disk and battery health, device problems, the top slowdown
  findings, startup programs, top processes, key sensor readings and the
  largest installed programs, trimmed to the character budget;
- your questions and Claude's previous answers from this chat.

The **What is shared with Claude** panel on the page shows the exact summary
text that will be attached, so you can check it before asking. With *Redact
serial numbers* on (Settings → Logging) serial numbers and MAC addresses are
masked before sending.

The overclock advisor sends a JSON hardware profile (CPU, GPUs, memory,
current sensor readings, detected tuning capabilities and your safety limits).
It never contains serial numbers.

Requests go only to `api.anthropic.com` over HTTPS, through the official
Anthropic .NET SDK. Anthropic's handling of API data is described in their
privacy policy; PC Checker itself stores none of the conversation.

## How it works

- `src/PcChecker.Core/Assistant` defines the contract (`IPcAssistantService`),
  the message and context records, `AssistantContextBuilder` (the pure
  function that turns scan results into the summary, including redaction),
  `AssistantModelCatalog` (the curated model list and suggested questions) and
  `AssistantTextFormatter` (tidies Markdown for display). All are unit-tested.
- `src/PcChecker.Claude` implements the contract on the official
  [Anthropic .NET SDK](https://www.nuget.org/packages/Anthropic) (package
  `Anthropic` 13.3.0): `ClaudeAssistantService` (requests), `AssistantPrompts`
  (every prompt and the context block), `ClaudeRequestBuilder` (history
  normalisation and trimming), `OverclockPlanSchema` (the JSON schema for
  structured plans) and `AssistantErrorMapper` (friendly error text).
- Requests use the SDK's beta Messages endpoint with the
  `server-side-fallback-2026-07-01` beta and `fallbacks: default`, so if the
  chosen model declines a request the API retries it with Anthropic's default
  fallback model instead of returning nothing. If Claude still declines, the
  explanation is shown in the chat. The system prompt is marked for prompt
  caching, which lowers the cost of follow-up questions in the same chat.
  Thinking is left at the model's adaptive default; the effort setting maps
  to `output_config.effort`.
- The advisor asks for structured JSON output (`output_config.format` with
  the schema in `OverclockPlanSchema`); `OverclockPlanParser` still parses it
  tolerantly and `OverclockSafetyValidator.Clamp` reduces every step to your
  limits and the hardware's limits before it is shown. When anything was
  clamped, the plan carries a warning saying so. A step that fails validation
  on the Overclock page is marked and cannot be applied.
- `ClaudeAssistantService` honours `IsConfigured` from settings or the
  environment variable, rebuilds its client when the key changes, and reports
  errors through `AssistantException` so the page can show them inline.

## Disabling the feature

Remove the saved key (Ask Claude → Settings → **Remove saved key**) or delete
`Claude.ProtectedApiKey` from `settings.json`. With no key the page shows the
setup card and sends nothing. The Demo mode (`--demo`) uses a built-in sample
assistant that never touches the network.

## Troubleshooting

- **"No key saved yet"**: paste a key in the setup card and click Save key.
- **"Your API key was rejected (401)"**: the key is wrong, revoked or from a
  different organisation. Create a new one and save it again.
- **"Claude is rate limited right now (429)"**: wait a moment or lower the
  effort level; a Haiku model has higher limits on most accounts.
- **"Your Anthropic account has no credit (402)"**: add credit in the console.
- **"The selected model was not found (404)"**: pick another model; the id in
  `settings.json` may be misspelled.
- **"Could not reach Claude"**: no internet, a VPN or a firewall is blocking
  `api.anthropic.com`. Use **Test connection** after fixing it.
- **Answers seem generic**: make sure *Share scan context* is on and that the
  Hardware, Health and Performance scans have run in this session; the
  **What is shared** panel shows which sections are filled.
- **The answer was cut short**: it hit the length limit (8,000 output tokens).
  Ask a follow-up to continue.

## What the overclock advisor can and cannot do

It can: read your hardware profile and capabilities, propose conservative
staged GPU plans (NVIDIA via NVAPI; AMD and Intel Arc where PC Checker marks
them tunable), propose CPU steps only when a supported tuning tool is
installed, and explain each step for a beginner.

It cannot: apply anything itself, exceed the limits you set in the Overclock
page (every value is clamped before it is shown), bypass the consent gate,
or guarantee stability — every PC is different, which is why each step ends
with a stress test and an abort condition.
