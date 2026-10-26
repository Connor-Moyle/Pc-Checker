namespace PcChecker.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> and notifies listeners when they change.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsDirectory { get; }
    string SettingsFilePath { get; }
    Task LoadAsync();
    Task SaveAsync();

    /// <summary>Raised after a successful save (and after load) with the current settings.</summary>
    event EventHandler<AppSettings>? Changed;
}
