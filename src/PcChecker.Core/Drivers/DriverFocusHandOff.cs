namespace PcChecker.Core.Drivers;

/// <summary>Asks the Drivers page to show one device (sent from the Crashes page): the text to put in its search box.</summary>
public sealed record DriverFocusRequest(string SearchText);

/// <summary>Crashes page → Drivers page: show the device a crash is blamed on.</summary>
public sealed class DriverFocusHandOff : Games.GameHandOff<DriverFocusRequest>;
