using System.Runtime.CompilerServices;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.Assistant;

/// <summary>
/// Demo assistant: streams canned, question-aware answers about the sample gaming desktop so the chat UI can be
/// shown and screenshotted without an API key. Pure .NET; no network.
/// </summary>
public sealed class DemoPcAssistantService : IPcAssistantService
{
    private const int WordDelayMs = 25;

    public bool IsConfigured => true;

    public async Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Connected to the demo assistant. Answers are canned sample text, not live Claude replies.");
    }

    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AssistantMessage> history, AssistantContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var question = history.LastOrDefault(m => m.Role == AssistantMessage.UserRole)?.Text ?? string.Empty;
        var reply = PickReply(question, context);
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        foreach (var word in reply.Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(WordDelayMs, cancellationToken).ConfigureAwait(false);
            yield return word + " ";
        }
    }

    public async Task<OperationResult<OverclockPlan>> CreateOverclockPlanAsync(OverclockAdvisorRequest request, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        var plan = new OverclockPlan(
            "A conservative three-step GPU plan for the RTX 4070. Each step raises the core offset a little and checks stability for five minutes.",
            [
                new(1, "Raise the power limit", "A higher power limit lets the card hold its boost clock longer without changing voltages.", new OverclockProfile { Name = "Step 1", GpuPowerLimitPercent = 110, GpuTempLimitC = 83 }, 300, "GPU temperature above 83 °C or any artifacts", "+2–3% in GPU-bound games"),
                new(2, "Core +75 MHz", "A small core offset that almost every 4070 handles.", new OverclockProfile { Name = "Step 2", GpuCoreOffsetMhz = 75, GpuPowerLimitPercent = 110, GpuTempLimitC = 83 }, 300, "Crash, driver reset or artifacts", "+3–4%"),
                new(3, "Core +120 MHz, memory +500 MHz", "Adds a memory offset; GDDR6X usually takes +500 easily.", new OverclockProfile { Name = "Step 3", GpuCoreOffsetMhz = 120, GpuMemoryOffsetMhz = 500, GpuPowerLimitPercent = 110, GpuTempLimitC = 83 }, 300, "Crash, artifacts or lower performance than step 2", "+5–7%"),
            ],
            ["Stop at the first step that shows artifacts or a crash and go back one step."],
            ["Stock cooling and airflow; the case is not dust-clogged."],
            "Overclocking can reduce component lifespan and is done at your own risk. This is demo advice.");
        return OperationResult<OverclockPlan>.Ok(plan);
    }

    public async Task<OperationResult<string>> ExplainAsync(string subject, string details, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult<string>.Ok(
            $"{subject}: {details}\n\nIn plain terms, this is something PC Checker noticed during its scan. In the demo the explanation is canned; "
            + "with an API key the assistant explains what it means for your PC and whether it is worth worrying about.");
    }

    /// <summary>Chooses a canned answer by keywords in the question. Public so tests can check the routing.</summary>
    public static string PickReply(string question, AssistantContext? context)
    {
        var q = question.ToLowerInvariant();
        bool hasContext = context is { IsEmpty: false };

        if (q.Contains("slow") || q.Contains("lag") || q.Contains("faster"))
        {
            return "Your PC is in good shape overall. The two things most likely to make it feel slow are the nine programs that start with Windows "
                + "and the Balanced power plan.\n\nTry this first:\n- On the Startup page, turn off Epic Games Launcher, Armoury Crate and iCUE at startup. They can be opened when you need them.\n"
                + "- On the Optimize page, switch to the High performance power plan.\n- Memory is also running below its rated speed because XMP is off in the BIOS; enabling it is free performance.\n\n"
                + "None of these change anything permanent, and PC Checker creates a restore point before it changes settings.";
        }

        if (q.Contains("disk") || q.Contains("ssd") || q.Contains("drive") || q.Contains("storage"))
        {
            return "Both drives look healthy. The Samsung 990 PRO (your Windows drive) reports 3% wear, 1,240 power-on hours and no reallocated sectors, "
                + "which is excellent for its age. The 4 TB WD hard drive passes its SMART self-check and runs at 38 °C.\n\nOne thing to watch: the hard drive has 2 pending sectors. "
                + "That is not an emergency, but keep a backup of anything important on it and re-check in a month. If the number grows, plan a replacement.";
        }

        if (q.Contains("update") || q.Contains("driver"))
        {
            return "Update in this order:\n1. The NVIDIA graphics driver (551.23 → 560.94): it fixes stutter in several recent games and is the update most likely to be noticed.\n"
                + "2. The Intel chipset and Wi-Fi drivers from the ASUS support page, which are more than a year old.\n3. Windows updates, which are already pending a restart.\n\n"
                + "Skip the Realtek audio driver offered by Windows Update; the ASUS version is newer. PC Checker backs up drivers before installing new ones.";
        }

        if (q.Contains("health") || q.Contains("report") || q.Contains("score"))
        {
            return "Your health score is 86 out of 100, which means nothing urgent is wrong. Points were taken off for two things: the hard drive's pending sectors "
                + "(see the Health page) and 14 application errors in the event log, almost all from Armoury Crate crashing on login.\n\nEverything else is clean: no device problems, "
                + "no crash dumps in the last 30 days, memory diagnostics passed, and the CPU and GPU stay well inside their safe temperature range.";
        }

        if (q.Contains("temp") || q.Contains("hot") || q.Contains("cool") || q.Contains("fan"))
        {
            return "Temperatures are normal for this hardware. The i7-13700K idles around 38 °C and peaks near 78 °C under load; Intel allows up to 100 °C. "
                + "The RTX 4070 sits at 61 °C under gaming load with the hotspot at 72 °C, both comfortable.\n\nIf you want it quieter, the Fans page can lower the case fans a little "
                + "below 60 °C without any thermal risk. There is no need to change anything for performance reasons.";
        }

        if (q.Contains("startup") || q.Contains("start with windows") || q.Contains("boot"))
        {
            return "Of the nine startup programs, these can be turned off safely: Epic Games Launcher, Armoury Crate, iCUE, Discord and Steam. They will still work when you open them.\n\n"
                + "Keep enabled: Windows Security, the NVIDIA app (it applies your display settings) and OneDrive if you rely on it for backups. Your last boot took 24 seconds, "
                + "which should drop to around 15 after these changes.";
        }

        if (q.Contains("overclock") || q.Contains("xmp") || q.Contains("ram speed") || q.Contains("memory"))
        {
            return "The easiest win is memory: your G.Skill DDR5-6000 kit is currently running at 4800 MT/s because XMP is turned off. Enable the XMP I profile in the BIOS "
                + "(Ai Tweaker → Ai Overclock Tuner) and the Memory page will confirm 6000 MT/s after the restart.\n\nFor the GPU, the Overclock page can build a cautious plan: "
                + "a slightly higher power limit and a small core offset usually give 3–5% without any voltage change.";
        }

        string intro = hasContext
            ? "I can see the latest scan results for this PC. "
            : "No scan results are attached yet, so I am answering from the demo profile. ";
        return intro + "This sample machine has an Intel Core i7-13700K, an NVIDIA GeForce RTX 4070 with 12 GB, 32 GB of DDR5 memory, a Samsung 990 PRO NVMe drive and a 4 TB WD hard drive "
            + "on an ASUS ROG STRIX Z790-E board running Windows 11 24H2.\n\nAsk me about health, temperatures, drivers, startup programs or what to optimize, "
            + "and I will point at the exact page in PC Checker where you can act on it.";
    }
}
