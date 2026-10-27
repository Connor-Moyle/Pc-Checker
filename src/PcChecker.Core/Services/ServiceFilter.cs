namespace PcChecker.Core.Services;

/// <summary>Pure search and filter for the Services list, so the view model stays thin and the rules are tested.</summary>
public static class ServiceFilter
{
    /// <summary>True when a service passes the chosen filter.</summary>
    public static bool MatchesFilter(ServiceDetails service, ServiceFilterKind filter)
    {
        ArgumentNullException.ThrowIfNull(service);
        return filter switch
        {
            ServiceFilterKind.Running => service.IsRunning,
            ServiceFilterKind.Stopped => !service.IsRunning,
            ServiceFilterKind.ThirdPartyOnly => !service.IsMicrosoft,
            ServiceFilterKind.Disabled => service.StartupType == ServiceStartupType.Disabled,
            _ => true,
        };
    }

    /// <summary>True when a service matches the search box (display name, service name, description or PID).</summary>
    public static bool MatchesSearch(ServiceDetails service, string? search)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();
        return Contains(service.DisplayName, term)
            || Contains(service.Name, term)
            || Contains(service.Description, term)
            || (service.Pid is { } pid && pid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal));
    }

    /// <summary>Applies both the filter and the search in one pass, keeping the input order.</summary>
    public static IReadOnlyList<ServiceDetails> Apply(IEnumerable<ServiceDetails> services, string? search, ServiceFilterKind filter)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Where(s => MatchesFilter(s, filter) && MatchesSearch(s, search)).ToList();
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
