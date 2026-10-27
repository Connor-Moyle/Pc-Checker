namespace PcChecker.Core.Processes;

/// <summary>Parent link of one process, for working out a process tree.</summary>
public readonly record struct ProcessLink(int Pid, int ParentPid, DateTimeOffset? StartTime, string Name);

/// <summary>Pure helpers for "End process tree".</summary>
public static class ProcessTree
{
    /// <summary>
    /// Every descendant of <paramref name="rootPid"/>, deepest first (the order to end them in). Windows reuses process
    /// ids and keeps the parent id of orphans, so a process only counts as a child when it started after its parent;
    /// otherwise an unrelated program that inherited a recycled parent id would be ended too. A process whose start
    /// time is unknown is left out for the same reason.
    /// </summary>
    public static IReadOnlyList<ProcessLink> Descendants(int rootPid, DateTimeOffset? rootStart, IEnumerable<ProcessLink> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var all = processes.Where(p => p.Pid != p.ParentPid).ToList();
        var byParent = all.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var ordered = new List<(ProcessLink Link, int Depth)>();
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<(int Pid, DateTimeOffset? Start, int Depth)>();
        queue.Enqueue((rootPid, rootStart, 0));

        while (queue.Count > 0)
        {
            var (pid, start, depth) = queue.Dequeue();
            if (!byParent.TryGetValue(pid, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!StartedAfter(child.StartTime, start) || !visited.Add(child.Pid))
                {
                    continue;
                }

                ordered.Add((child, depth + 1));
                queue.Enqueue((child.Pid, child.StartTime, depth + 1));
            }
        }

        return ordered.OrderByDescending(x => x.Depth).Select(x => x.Link).ToList();
    }

    private static bool StartedAfter(DateTimeOffset? child, DateTimeOffset? parent) =>
        child is not null && (parent is null || child.Value >= parent.Value);
}
