namespace PcChecker.App.ViewModels;

/// <summary>
/// Opens the standard Windows "open file" picker for the Drivers page. Kept behind a replaceable delegate so view
/// model code can be exercised without a window: tests assign <see cref="PickFile"/> instead of showing a dialog.
/// </summary>
public static class DriverFilePicker
{
    /// <summary>Shows the picker and returns the chosen path, or null when cancelled. Arguments: title, Win32 filter.</summary>
    public static Func<string, string, string?> PickFile { get; set; } = ShowOpenFileDialog;

    /// <summary>Asks for a driver package (.inf) and returns its path, or null when the user cancelled.</summary>
    public static string? PickInf() => PickFile("Choose a driver package (.inf)", "Driver setup information (*.inf)|*.inf");

    private static string? ShowOpenFileDialog(string title, string filter)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }
}
