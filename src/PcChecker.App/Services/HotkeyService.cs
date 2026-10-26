// TODO(overlay): overlay module — RegisterHotKey on a message-only window, parse "Ctrl+Shift+O".
namespace PcChecker.App.Services;

/// <summary>Placeholder <see cref="IHotkeyService"/>: remembers registrations but does not hook the OS yet.</summary>
public sealed class HotkeyService : IHotkeyService
{
    private readonly Dictionary<string, (string Text, Action Callback)> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public bool Register(string id, string hotkeyText, Action callback)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hotkeyText) || callback is null)
        {
            return false;
        }

        _registrations[id] = (hotkeyText, callback);
        return true;
    }

    public void Unregister(string id) => _registrations.Remove(id);

    public bool IsRegistered(string id) => _registrations.ContainsKey(id);
}
