namespace PcChecker.Core.Services;

/// <summary>
/// Plans a Restart: which services to stop (running dependents first, the target last) and start again (the reverse),
/// and when to refuse. Dependents that were already stopped are left alone, so a restart never starts a service that
/// was not running, and a restart that would stop a protected dependent is refused. Pure logic, unit-tested.
/// </summary>
public static class ServiceRestartPlan
{
    /// <summary>One service that depends on the target, as the Service Control Manager reported it.</summary>
    public sealed record Dependent(string Name, bool Running);

    /// <summary>The services to stop, in order, or the plain reason the restart is refused.</summary>
    public sealed record Plan(IReadOnlyList<string> StopOrder, string? Refusal)
    {
        public IReadOnlyList<string> StartOrder => ServiceDependencyOrder.StartOrder(StopOrder);
    }

    /// <summary>
    /// Builds the plan. <paramref name="dependents"/> are every direct and indirect dependent in the order Windows
    /// returns them (reverse start order, which is a safe stop order). <paramref name="isLocked"/> says whether a
    /// service may never be stopped from here.
    /// </summary>
    public static Plan Build(string target, IEnumerable<Dependent> dependents, Func<string, bool> isLocked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(dependents);
        ArgumentNullException.ThrowIfNull(isLocked);

        var running = dependents
            .Where(d => d.Running && !string.IsNullOrWhiteSpace(d.Name) && !d.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var locked = running.Where(isLocked).ToList();
        if (locked.Count > 0)
        {
            return new Plan([], $"Restarting it would also stop {string.Join(", ", locked)}, which PC Checker keeps running.");
        }

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [target] = running };
        return new Plan(ServiceDependencyOrder.StopOrder(target, map), null);
    }
}
