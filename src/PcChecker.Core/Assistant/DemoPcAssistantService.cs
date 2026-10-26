using System.Runtime.CompilerServices;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.Assistant;

/// <summary>Demo assistant: streams canned answers so the chat UI can be shown without an API key.</summary>
public sealed class DemoPcAssistantService : IPcAssistantService
{
    public bool IsConfigured => true;

    public Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("Connected (demo assistant)."));

    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AssistantMessage> history, AssistantContext? context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var last = history.LastOrDefault(m => m.Role == AssistantMessage.UserRole)?.Text ?? string.Empty;
        string reply = last.Contains("slow", StringComparison.OrdinalIgnoreCase)
            ? "Your PC is in good shape overall. The two things most likely to make it feel slow are the nine programs that start with Windows and the Balanced power plan. Try disabling Epic Games Launcher, Armoury Crate and iCUE at startup on the Startup page, then switch to the High performance plan from Optimize."
            : "This is a demo answer. Your sample PC has an Intel Core i7-13700K, an NVIDIA GeForce RTX 4070 and 32 GB of DDR5 memory that is currently running below its rated speed because XMP is off in the BIOS. Ask me about health, temperatures or what to optimize.";
        foreach (var word in reply.Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
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
        return OperationResult<string>.Ok($"{subject}: {details} In short, this is a demo explanation. In the real app the assistant explains the finding in plain English and suggests what to do next.");
    }
}
