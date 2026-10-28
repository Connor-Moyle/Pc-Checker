namespace PcChecker.Core.DeviceTests;

/// <summary>How many keys a keyboard registers at the same time, in plain classes.</summary>
public enum RolloverVerdict
{
    /// <summary>Fewer than two keys were held together, so nothing is known yet.</summary>
    NotTested,

    /// <summary>Two or three keys: combinations in games can get lost.</summary>
    Limited,

    /// <summary>Four or five keys: fine for typing, tight for some games.</summary>
    Fair,

    /// <summary>Six to nine keys: the usual USB limit, fine for games.</summary>
    Good,

    /// <summary>Ten or more keys: "n-key rollover".</summary>
    Excellent,
}

/// <summary>
/// Tracks the keyboard test: which keys are held now, which have been pressed at least once, and the most keys held
/// at the same time (the rollover). Key auto-repeat does not count as a new press.
/// </summary>
public sealed class KeyRolloverTracker
{
    private readonly HashSet<int> _held = [];
    private readonly HashSet<int> _tested = [];

    public int HeldCount => _held.Count;

    public int MaxHeld { get; private set; }

    public int TestedCount => _tested.Count;

    public IReadOnlyCollection<int> Held => _held;

    public IReadOnlyCollection<int> Tested => _tested;

    public RolloverVerdict Verdict => Rate(MaxHeld);

    /// <summary>Records a key going down. Returns false for auto-repeat of a key that is already held.</summary>
    public bool Press(int code)
    {
        if (!_held.Add(code))
        {
            return false;
        }

        _tested.Add(code);
        MaxHeld = Math.Max(MaxHeld, _held.Count);
        return true;
    }

    /// <summary>
    /// Records a key going up. A key that was never seen going down still counts as tested (Print Screen only sends
    /// its release to programs).
    /// </summary>
    public void Release(int code)
    {
        _held.Remove(code);
        _tested.Add(code);
    }

    /// <summary>Forgets held keys, for example when the test window loses focus and their releases would be missed.</summary>
    public void ReleaseAll() => _held.Clear();

    public void Reset()
    {
        _held.Clear();
        _tested.Clear();
        MaxHeld = 0;
    }

    public bool IsHeld(int code) => _held.Contains(code);

    public bool WasTested(int code) => _tested.Contains(code);

    /// <summary>How many of <paramref name="codes"/> have been pressed.</summary>
    public int TestedOf(IEnumerable<int> codes) => codes.Count(_tested.Contains);

    public static RolloverVerdict Rate(int maxHeld) => maxHeld switch
    {
        < 2 => RolloverVerdict.NotTested,
        <= 3 => RolloverVerdict.Limited,
        <= 5 => RolloverVerdict.Fair,
        <= 9 => RolloverVerdict.Good,
        _ => RolloverVerdict.Excellent,
    };
}
