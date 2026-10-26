using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcChecker.Core.Assistant;

namespace PcChecker.Claude;

/// <summary>
/// Every prompt the Claude integration sends, in one place, plus the pure builders that assemble them from scan
/// context. Nothing here touches the network, so it is unit-tested.
/// </summary>
public static class AssistantPrompts
{
    /// <summary>System prompt for the Ask Claude chat.</summary>
    public const string System =
        "You are the built-in assistant of PC Checker, a Windows 11 diagnostics and optimization tool. " +
        "You help people who are not computer experts understand what the tool found on their own PC and what to do about it.\n\n" +
        "Guidelines:\n" +
        "- Answer plainly and concretely. Prefer short paragraphs and numbered steps over long essays.\n" +
        "- When scan results are provided, base your answer on them and refer to specific components, numbers and findings. " +
        "Do not invent hardware, readings or problems that are not in the results.\n" +
        "- Point to the PC Checker page where the user can act (Health, Drivers, Startup, Optimize, Cleanup, Storage, Memory, Fans, Latency, Overclock, Windows Features, Software, Displays, Audio, Keyboard & Mouse, Lighting).\n" +
        "- Never suggest registry edits, service changes or BIOS changes without explaining the risk and how to undo them. " +
        "Recommend a restore point or backup before anything that changes the system.\n" +
        "- Be honest about uncertainty. If the results do not contain enough to answer, say what is missing and which scan would help.\n" +
        "- Never recommend disabling security software or Windows Update as a fix.\n" +
        "- Keep answers under about 250 words unless the user asks for detail. Use plain Markdown only (short headings, bullets, bold); no tables.";

    /// <summary>System prompt for the overclock advisor.</summary>
    public const string OverclockAdvisorSystem =
        "You are a cautious overclocking advisor inside PC Checker. You produce a staged plan that the user applies one step at a time, " +
        "running a stress test between steps.\n\n" +
        "Rules you must follow:\n" +
        "- Never exceed the limits given in the request (user limits and hardware limits). Smaller is better.\n" +
        "- Prefer many small steps over few large ones. The first step must be very conservative.\n" +
        "- Only use controls the capabilities say are available. If the GPU cannot be tuned, do not propose GPU steps; if no CPU tool is available, say CPU tuning must be done in the BIOS and do not propose CPU steps.\n" +
        "- Every step needs a stress test of at least 300 seconds and a clear abort condition that mentions temperatures.\n" +
        "- Explain each step for a beginner: what it changes, why, and what to watch for.\n" +
        "- Include warnings about warranty, lifespan and the possibility of crashes, and list the assumptions you made (cooling, airflow, power supply).\n" +
        "- Respond with JSON only, matching the requested schema exactly. No prose before or after the JSON.";

    /// <summary>Wraps the scan context in a clearly delimited block appended to the system prompt.</summary>
    public static string BuildContextBlock(AssistantContext? context)
    {
        if (context is null || context.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Latest PC Checker scan results for this PC");
        sb.AppendLine("Use these as facts about the user's computer. They were captured by the app, not typed by the user.");
        Append(sb, "Hardware", context.HardwareSummary);
        Append(sb, "Health", context.HealthSummary);
        Append(sb, "Performance", context.PerformanceSummary);
        Append(sb, "Temperatures and sensors", context.SensorSummary);
        Append(sb, "Installed software", context.SoftwareSummary);
        return sb.ToString().TrimEnd();
    }

    /// <summary>The full system text for a chat turn: prompt plus optional context block.</summary>
    public static string BuildChatSystemText(AssistantContext? context)
    {
        var block = BuildContextBlock(context);
        return block.Length == 0 ? System : System + "\n\n" + block;
    }

    /// <summary>User message for a one-off explanation of a finding.</summary>
    public static string BuildExplainPrompt(string subject, string details)
    {
        var s = string.IsNullOrWhiteSpace(subject) ? "this finding" : subject.Trim();
        var d = string.IsNullOrWhiteSpace(details) ? "(no further details)" : details.Trim();
        return $"Explain this to a non-expert in at most 150 words and say clearly whether it is worth worrying about and what, if anything, to do next.\n\nSubject: {s}\nDetails: {d}";
    }

    /// <summary>User message for the overclock advisor: hardware profile, capabilities and limits as JSON, plus goal and history.</summary>
    public static string BuildOverclockRequest(OverclockAdvisorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder();
        sb.AppendLine("Create a staged overclock plan for this PC.");
        sb.AppendLine();
        sb.AppendLine("Goal: " + (string.IsNullOrWhiteSpace(request.UserGoal) ? "a modest, stable performance gain with no risk to the hardware" : request.UserGoal.Trim()));
        sb.AppendLine();
        sb.AppendLine("Hardware profile (JSON):");
        sb.AppendLine(string.IsNullOrWhiteSpace(request.HardwareProfileJson) ? "{}" : request.HardwareProfileJson);
        sb.AppendLine();
        sb.AppendLine("Tuning capabilities detected by PC Checker (JSON):");
        sb.AppendLine(JsonSerializer.Serialize(new
        {
            request.Capabilities.GpuVendor,
            request.Capabilities.GpuName,
            request.Capabilities.GpuMethod,
            request.Capabilities.GpuLimits,
            request.Capabilities.CpuName,
            request.Capabilities.CpuMethod,
            request.Capabilities.XmpState,
            request.Capabilities.MemoryConfiguredMhz,
            request.Capabilities.MemoryMaxMhz,
            request.Capabilities.Notes,
            canTuneGpu = request.Capabilities.CanTuneGpu,
            canTuneCpu = request.Capabilities.CanTuneCpu,
        }, JsonOptions));
        sb.AppendLine();
        sb.AppendLine("User safety limits that must never be exceeded (JSON):");
        sb.AppendLine(JsonSerializer.Serialize(new
        {
            maxGpuCoreOffsetMhz = request.Limits.MaxGpuCoreOffsetMhz,
            maxGpuMemoryOffsetMhz = request.Limits.MaxGpuMemoryOffsetMhz,
            maxGpuPowerLimitPercent = request.Limits.MaxGpuPowerLimitPercent,
            maxGpuTempLimitC = request.Limits.MaxGpuTempLimitC,
            maxCpuTempC = request.Limits.MaxCpuTempC,
        }, JsonOptions));
        if (!string.IsNullOrWhiteSpace(request.PreviousResults))
        {
            sb.AppendLine();
            sb.AppendLine("Results of previous steps and stress tests (take these into account; back off after any failure):");
            sb.AppendLine(request.PreviousResults.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("Respond with JSON only: {summary, steps[{order, title, rationale, target{gpuCoreOffsetMhz, gpuMemoryOffsetMhz, gpuPowerLimitPercent, gpuTempLimitC, gpuFanPercent, cpuAllCoreRatio, cpuVoltageOffsetMv, cpuPowerLimitW, notes}, stressSeconds, abortIf, expectedGain}], warnings[], assumptions[], disclaimer}.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Friendly text shown when the model declines to answer.</summary>
    public static string RefusalMessage(string? explanation) =>
        string.IsNullOrWhiteSpace(explanation)
            ? "Claude declined to answer this request. Try rephrasing the question, or ask about a specific scan result."
            : "Claude declined to answer this request: " + explanation.Trim() + " Try rephrasing the question.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void Append(StringBuilder sb, string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("### " + title);
        sb.AppendLine(text.Trim());
    }
}
