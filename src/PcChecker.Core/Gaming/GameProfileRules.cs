namespace PcChecker.Core.Gaming;

/// <summary>Pure validation and list editing for <see cref="GamingSettings.Profiles"/>.</summary>
public static class GameProfileRules
{
    public const int MaxNameLength = 60;

    /// <summary>"eldenring" / "C:\Games\eldenring.EXE" → "eldenring.exe" (file name only, ".exe" added when missing).</summary>
    public static string CanonicalExeName(string? exeNameOrPath)
    {
        string file = GameProfileMatcher.ExeFileName(exeNameOrPath)?.Trim() ?? string.Empty;
        if (file.Length == 0)
        {
            return string.Empty;
        }

        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] + ".exe" : file + ".exe";
    }

    /// <summary>Returns a plain-English problem with the profile, or null when it can be saved.</summary>
    public static string? Validate(GameProfile profile, IEnumerable<GameProfile> existing, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string name = profile.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "Give the profile a name, for example the game's title.";
        }

        if (name.Length > MaxNameLength)
        {
            return $"Keep the name under {MaxNameLength} characters.";
        }

        string exe = GameProfileMatcher.NormalizeExeName(profile.ExeName);
        if (exe.Length == 0)
        {
            return "Choose the game's program file (.exe), or pick the game from the running programs.";
        }

        if (exe.IndexOfAny(Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToArray()) >= 0)
        {
            return $"\"{profile.ExeName}\" is not a valid program file name.";
        }

        if (GameProfileMatcher.IsIgnoredProcess(exe))
        {
            return $"\"{CanonicalExeName(profile.ExeName)}\" is part of Windows or PC Checker, not a game.";
        }

        bool duplicate = existing.Any(p => p is not null
            && !string.Equals(p.Name, originalName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        return duplicate ? $"There is already a profile called \"{name}\". Choose another name." : null;
    }

    /// <summary>
    /// Adds the profile, or replaces the one called <paramref name="originalName"/> in place. The stored copy has a
    /// trimmed name and a canonical exe name. Returns the stored copy.
    /// </summary>
    public static GameProfile Upsert(GamingSettings settings, GameProfile profile, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        var stored = profile.Clone();
        stored.Name = stored.Name.Trim();
        stored.ExeName = CanonicalExeName(stored.ExeName);
        stored.OverlayPresetName = Blank(stored.OverlayPresetName);
        stored.FanProfileName = Blank(stored.FanProfileName);
        stored.ExePath = Blank(stored.ExePath);
        if (stored.PowerPlanId == Guid.Empty)
        {
            stored.PowerPlanId = null;
        }

        int index = originalName is null
            ? -1
            : settings.Profiles.FindIndex(p => string.Equals(p.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            settings.Profiles[index] = stored;
        }
        else
        {
            settings.Profiles.Add(stored);
        }

        return stored;
    }

    /// <summary>Removes the profile with this name; false when there is none.</summary>
    public static bool Remove(GamingSettings settings, string name)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Other enabled profiles for the same exe that are shadowed by this one (or that shadow it).</summary>
    public static IReadOnlyList<GameProfile> SameExe(GamingSettings settings, GameProfile profile) =>
        settings.Profiles.Where(p => !ReferenceEquals(p, profile)
            && !string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
            && GameProfileMatcher.Matches(p, profile.ExeName)).ToList();

    /// <summary>One line describing what the profile changes, e.g. "Overlay: Full · Fans: Performance · Power: High performance".</summary>
    public static string Describe(GameProfile profile, Func<Guid, string?>? powerPlanName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var parts = new List<string>();
        if (!profile.ShowOverlay)
        {
            parts.Add("Overlay hidden");
        }
        else
        {
            parts.Add(profile.OverlayPresetName is { Length: > 0 } preset ? $"Overlay: {preset}" : "Overlay shown");
        }

        if (profile.FanProfileName is { Length: > 0 } fans)
        {
            parts.Add($"Fans: {fans}");
        }

        if (profile.PowerPlanId is { } plan && plan != Guid.Empty)
        {
            parts.Add($"Power: {powerPlanName?.Invoke(plan) ?? plan.ToString()}");
        }

        if (!profile.RevertOnExit)
        {
            parts.Add("kept after the game closes");
        }

        return string.Join(" · ", parts);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
