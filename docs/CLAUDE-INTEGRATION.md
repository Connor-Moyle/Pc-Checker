# Claude integration

PC Checker can talk to [Claude](https://www.anthropic.com/claude) to explain
what it finds and to plan an overclock. The feature is optional and does
nothing until you add an API key.

## What you get

- **Ask Claude** page: a chat that already knows your hardware, health score,
  temperatures, slowdown findings and installed software, so you can ask
  "why does my laptop get hot when idle?" or "which of these startup apps can I
  turn off?" and get an answer about *your* PC.
- **Explain** buttons around the app: a one-click plain-English explanation of
  a SMART attribute, an event-log error, a device problem code or a finding.
- **Overclock advisor** (Advanced mode): give it your goal ("quiet", "max
  performance", "balanced") and it returns a staged plan: small steps, a
  stress test after each, abort conditions, expected gain. You apply each step
  yourself; the app never applies a plan automatically.

## Setup

1. Create an API key at <https://console.anthropic.com/>.
2. In PC Checker open **Settings → Claude**, paste the key and click
   **Test connection**.
3. Pick a model and effort level. The defaults are a good balance of speed and
   quality.
4. Decide whether to **Share scan context**. On by default; turn it off if you
   want a general-purpose chat with nothing about your PC attached.

Usage is billed by Anthropic to your account. A typical question with scan
context costs a fraction of a cent; the overclock advisor uses more because
it reads a larger hardware profile.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Model | `claude-opus-5-5` | Which Claude model answers |
| Effort | Medium | Low is faster and cheaper; High thinks longer |
| Share scan context | On | Attach a summary of the latest scans to each message |
| Max context characters | 12,000 | Upper bound on the attached summary |
| API key | (none) | Stored with Windows DPAPI, current user only |

## What is sent

With *Share scan context* on, each message carries a text summary built from
the latest scans: system, CPU, GPU, memory, disks, network adapters, health
items that are not *Good*, disk and battery health, device problems, the top
slowdown findings, startup programs, top processes, key sensor readings and
the largest installed programs. It is trimmed to the character budget.

With *Redact serial numbers* on (Settings → Logging) serial numbers and MAC
addresses are masked before sending.

The overclock advisor sends a JSON hardware profile (CPU, GPUs, memory,
current sensor readings, detected tuning capabilities and your limits). It
never contains serial numbers.

Nothing is sent when the key is empty, when you have not pressed send, or
from any page other than Ask Claude and the advisor.

## How it works

- `src/PcChecker.Core/Assistant` defines the contract (`IPcAssistantService`),
  the message and context records, and `AssistantContextBuilder`, the pure
  function that turns scan results into the summary. It is unit-tested,
  including the redaction.
- `src/PcChecker.Claude` implements the contract on the official
  [Anthropic .NET SDK](https://www.nuget.org/packages/Anthropic). Chat
  responses stream token by token into the page.
- The advisor asks for a JSON plan; `OverclockPlanParser` parses it
  tolerantly and `OverclockSafetyValidator` clamps every step to your limits
  before it is shown. A step that fails validation is marked and cannot be
  applied.

## Troubleshooting

- **"Not configured"**: no key saved. Paste it in Settings and test.
- **Authentication error**: the key is wrong or revoked. Create a new one.
- **Rate limited**: wait a moment or lower the effort level.
- **Answers seem generic**: make sure *Share scan context* is on and that you
  have run the scans (Hardware, Health, Performance) in this session.
