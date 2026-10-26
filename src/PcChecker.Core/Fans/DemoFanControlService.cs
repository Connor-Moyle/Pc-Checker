using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>Demo fans for the sample desktop: CPU cooler, two case fans, GPU fans. All controllable.</summary>
public sealed class DemoFanControlService : IFanControlService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (FanMode Mode, double Percent)> _state = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fan.cpu"] = (FanMode.Auto, 42),
        ["fan.case.front"] = (FanMode.Auto, 35),
        ["fan.case.rear"] = (FanMode.Auto, 38),
        ["fan.gpu"] = (FanMode.Auto, 46),
    };

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public FanProfile? ActiveProfile { get; private set; }
    public IReadOnlyList<FanProfile> BuiltInProfiles { get; } =
        FanCurveMath.BuiltInProfiles("cpu.package", "gpu.core", ["fan.cpu", "fan.case.front", "fan.case.rear"], ["fan.gpu"]);

    public event EventHandler<IReadOnlyList<FanDevice>>? FansUpdated;

    public async Task<IReadOnlyList<FanDevice>> GetFansAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var fans = Build();
        FansUpdated?.Invoke(this, fans);
        return fans;
    }

    public async Task<OperationResult> SetManualAsync(string fanId, double percent, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_state.ContainsKey(fanId))
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            _state[fanId] = (FanMode.Manual, Math.Clamp(percent, 0, 100));
        }

        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"{fanId} set to {Math.Clamp(percent, 0, 100):0}% (demo).");
    }

    public async Task<OperationResult> SetAutoAsync(string fanId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_state.TryGetValue(fanId, out var s))
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            _state[fanId] = (FanMode.Auto, s.Percent);
        }

        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"{fanId} returned to automatic control (demo).");
    }

    public async Task<OperationResult> ApplyProfileAsync(FanProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var (id, assignment) in profile.Fans)
            {
                if (!_state.TryGetValue(id, out var s))
                {
                    continue;
                }

                double percent = assignment.Mode switch
                {
                    FanMode.Manual => assignment.ManualPercent ?? s.Percent,
                    FanMode.Curve when assignment.Curve is not null => FanCurveMath.Evaluate(assignment.Curve, 58, null),
                    _ => s.Percent,
                };
                _state[id] = (assignment.Mode, Math.Clamp(percent, 0, 100));
            }
        }

        ActiveProfile = profile;
        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo).");
    }

    public async Task<OperationResult> RevertAllToAutoAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var id in _state.Keys.ToList())
            {
                _state[id] = (FanMode.Auto, _state[id].Percent);
            }
        }

        ActiveProfile = null;
        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok("All fans are back under automatic control (demo).");
    }

    private IReadOnlyList<FanDevice> Build()
    {
        var random = DemoData.CreateRandom(Environment.TickCount / 1000);
        lock (_gate)
        {
            return
            [
                Make("fan.cpu", "CPU cooler", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1800, random),
                Make("fan.case.front", "Case fan (front)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, random),
                Make("fan.case.rear", "Case fan (rear)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, random),
                Make("fan.gpu", "GPU fans", HardwareKind.Gpu, DemoData.GpuName, 2600, random),
            ];
        }
    }

    private FanDevice Make(string id, string name, HardwareKind kind, string hardwareName, double maxRpm, Random random)
    {
        var (mode, percent) = _state[id];
        double rpm = Math.Round(DemoData.Jitter(random, maxRpm * percent / 100, 25));
        return new FanDevice(id, name, kind, hardwareName, rpm, percent, true, 20, 100, mode, id + ".control", id + ".rpm");
    }
}
