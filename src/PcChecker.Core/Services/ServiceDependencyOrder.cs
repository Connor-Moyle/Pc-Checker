namespace PcChecker.Core.Services;

/// <summary>
/// Works out the safe order to stop and start a service and everything tied to it, so a Restart does not leave a
/// dependent service stranded. A service can only run while the services it depends on are running, so:
/// to stop a service, stop the services that depend on it first; to start it again, start it before them.
/// Pure graph logic, unit-tested.
/// </summary>
public static class ServiceDependencyOrder
{
    /// <summary>
    /// The order to stop <paramref name="target"/> and its dependent chain: deepest dependents first, the target last.
    /// <paramref name="requiredBy"/> maps a service name to the services that directly depend on it.
    /// </summary>
    public static IReadOnlyList<string> StopOrder(string target, IReadOnlyDictionary<string, IReadOnlyList<string>> requiredBy)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(requiredBy);

        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(target);
        return order;

        void Visit(string name)
        {
            if (!visited.Add(name))
            {
                return;
            }

            if (requiredBy.TryGetValue(name, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    Visit(dependent);
                }
            }

            order.Add(name);
        }
    }

    /// <summary>
    /// The order to start the chain again after a stop: the reverse of <see cref="StopOrder"/>, so the target comes
    /// up before the services that depend on it. Only the names that were actually stopped are started again.
    /// </summary>
    public static IReadOnlyList<string> StartOrder(IReadOnlyList<string> stopOrder)
    {
        ArgumentNullException.ThrowIfNull(stopOrder);
        return stopOrder.Reverse().ToList();
    }

    /// <summary>Turns a flat "depends on" map into the reverse "required by" map the stop order needs.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildRequiredBy(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        var requiredBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (service, dependencies) in dependsOn)
        {
            foreach (var dependency in dependencies)
            {
                if (!requiredBy.TryGetValue(dependency, out var list))
                {
                    list = [];
                    requiredBy[dependency] = list;
                }

                if (!list.Contains(service, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(service);
                }
            }
        }

        return requiredBy.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
