using PcChecker.Core.Common;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// A realistic 30-day app history for the demo gaming desktop: browsers in the day, Discord and games in the evening
/// and at weekends, Steam downloading two big games, Windows Update on patch day, plus the Windows parts that always
/// run. One record per app per hour the PC was on, from a fixed seed so screenshots look the same every run.
/// </summary>
public static class DemoAppHistoryData
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private enum Pattern
    {
        Always,
        Daytime,
        Evening,
        WorkHours,
        WeekendGame,
        WeekdayGame,
        Background,
    }

    private sealed record DemoApp(string Key, string Name, string? Path, string Publisher, Pattern Pattern,
        double CpuSecondsPerHour, long NetworkPerHour, long ReadPerHour, long WritePerHour);

    private static readonly DemoApp[] Apps =
    [
        new(@"c:\program files\google\chrome\application\chrome.exe", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", Pattern.Daytime, 420, 120 * Mb, 140 * Mb, 230 * Mb),
        new(@"c:\program files (x86)\microsoft\edge\application\msedge.exe", "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Corporation", Pattern.WorkHours, 110, 35 * Mb, 40 * Mb, 60 * Mb),
        new(@"c:\users\alex\appdata\local\discord\app-1.0.9170\discord.exe", "Discord", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9170\Discord.exe", "Discord Inc.", Pattern.Evening, 160, 95 * Mb, 25 * Mb, 40 * Mb),
        new(@"c:\program files (x86)\steam\steam.exe", "Steam", @"C:\Program Files (x86)\Steam\steam.exe", "Valve Corporation", Pattern.Always, 35, 6 * Mb, 15 * Mb, 20 * Mb),
        new(@"c:\program files (x86)\steam\bin\cef\cef.win7x64\steamwebhelper.exe", "Steam Client WebHelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", "Valve Corporation", Pattern.Evening, 120, 22 * Mb, 30 * Mb, 25 * Mb),
        new(@"d:\steamlibrary\steamapps\common\cyberpunk 2077\bin\x64\cyberpunk2077.exe", "Cyberpunk 2077", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "CD PROJEKT RED", Pattern.WeekendGame, 2300, 3 * Mb, 2600 * Mb, 70 * Mb),
        new(@"d:\steamlibrary\steamapps\common\counter-strike global offensive\game\bin\win64\cs2.exe", "Counter-Strike 2", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", "Valve Corporation", Pattern.WeekdayGame, 1500, 65 * Mb, 900 * Mb, 30 * Mb),
        new(@"c:\users\alex\appdata\roaming\spotify\spotify.exe", "Spotify", @"C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe", "Spotify AB", Pattern.WorkHours, 55, 70 * Mb, 20 * Mb, 35 * Mb),
        new("wuauserv", "Windows Update", null, "Microsoft Corporation", Pattern.Background, 30, 12 * Mb, 60 * Mb, 45 * Mb),
        new("dosvc", "Delivery Optimization", null, "Microsoft Corporation", Pattern.Background, 8, 4 * Mb, 10 * Mb, 30 * Mb),
        new(@"c:\program files\microsoft onedrive\onedrive.exe", "Microsoft OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe", "Microsoft Corporation", Pattern.Always, 14, 16 * Mb, 30 * Mb, 12 * Mb),
        new(@"c:\windows\system32\dwm.exe", "Desktop Window Manager", @"C:\Windows\System32\dwm.exe", "Microsoft Corporation", Pattern.Always, 210, 0, 5 * Mb, 1 * Mb),
        new(@"c:\windows\explorer.exe", "Windows Explorer", @"C:\Windows\explorer.exe", "Microsoft Corporation", Pattern.Always, 45, 1 * Mb, 35 * Mb, 12 * Mb),
        new(@"c:\programdata\microsoft\windows defender\platform\4.18.24090.11-0\msmpeng.exe", "Antimalware Service Executable", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", "Microsoft Corporation", Pattern.Always, 85, 3 * Mb, 320 * Mb, 25 * Mb),
        new(@"c:\program files\nvidia corporation\nvidia app\cef\nvidia app.exe", "NVIDIA App", @"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA App.exe", "NVIDIA Corporation", Pattern.Background, 12, 2 * Mb, 8 * Mb, 4 * Mb),
        new(@"c:\program files\pc checker\pcchecker.exe", "PC Checker", @"C:\Program Files\PC Checker\PcChecker.exe", "PC Checker", Pattern.Always, 22, 0, 6 * Mb, 3 * Mb),
    ];

    /// <summary>Who each demo app is, for the page's names, icons and publishers.</summary>
    public static IReadOnlyDictionary<string, AppIdentity> Identities { get; } =
        Apps.ToDictionary(a => a.Key, a => new AppIdentity(a.Key, a.Name, a.Path, a.Publisher), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hourly records for the <paramref name="days"/> days up to <paramref name="now"/> (local time decides when the PC is
    /// on). With <paramref name="withNetwork"/> false the records look like PC Checker's own recording (no network).
    /// </summary>
    public static IReadOnlyList<AppUsageRecord> Generate(DateTimeOffset now, int days, bool withNetwork)
    {
        var random = DemoData.CreateRandom(2077);
        var records = new List<AppUsageRecord>(days * 24 * Apps.Length / 2);
        var localNow = now.ToLocalTime();
        var start = new DateTimeOffset(localNow.Date.AddDays(-(days - 1)), localNow.Offset);
        for (var hour = start; hour <= localNow; hour = hour.AddHours(1))
        {
            int dayIndex = (int)(localNow.Date - hour.Date).TotalDays;
            bool weekend = hour.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (!PcIsOn(hour.Hour, weekend))
            {
                continue;
            }

            foreach (var app in Apps)
            {
                double intensity = Intensity(app.Pattern, hour.Hour, weekend, dayIndex, random);
                if (intensity <= 0)
                {
                    continue;
                }

                double wobble = 0.65 + random.NextDouble() * 0.7;
                long network = (long)(app.NetworkPerHour * intensity * wobble) + ExtraDownload(app.Key, dayIndex, hour.Hour);
                long metered = withNetwork && dayIndex is 12 or 13 && app.Pattern is Pattern.Daytime or Pattern.Evening ? network / 3 : 0;
                var time = hour.ToUniversalTime();
                records.Add(new AppUsageRecord(app.Key, time, time.AddMinutes(withNetwork ? 0 : 59),
                    Math.Round(app.CpuSecondsPerHour * intensity * wobble, 1),
                    withNetwork ? network / 9 : null, withNetwork ? network - network / 9 : null, withNetwork ? metered : null,
                    (long)(app.ReadPerHour * intensity * wobble), (long)(app.WritePerHour * intensity * wobble) + ExtraDownload(app.Key, dayIndex, hour.Hour)));
            }
        }

        return records;
    }

    private static bool PcIsOn(int hour, bool weekend) => weekend ? hour >= 9 : hour >= 7;

    private static double Intensity(Pattern pattern, int hour, bool weekend, int dayIndex, Random random) => pattern switch
    {
        Pattern.Always => 1,
        Pattern.Background => random.NextDouble() < 0.35 ? 0.5 : 0,
        Pattern.Daytime => hour >= (weekend ? 10 : 8) ? (hour >= 19 ? 0.6 : 0.9) : 0,
        Pattern.WorkHours => !weekend && hour is >= 9 and < 17 ? 1 : (weekend && hour is >= 12 and < 15 ? 0.3 : 0),
        Pattern.Evening => hour >= (weekend ? 13 : 18) ? 1 : 0,
        Pattern.WeekendGame => weekend && (hour is >= 14 and < 18 || hour is >= 20 and < 23) ? 1 : (dayIndex == 0 && hour is >= 20 and < 22 ? 1 : 0),
        Pattern.WeekdayGame => !weekend && hour is >= 20 and < 23 && dayIndex % 3 != 1 ? 1 : 0,
        _ => 0,
    };

    /// <summary>The big one-off downloads: two Steam game installs and a monthly Windows update.</summary>
    private static long ExtraDownload(string key, int dayIndex, int hour) => key switch
    {
        @"c:\program files (x86)\steam\steam.exe" when dayIndex == 23 && hour is >= 19 and < 23 => 18 * Gb,
        @"c:\program files (x86)\steam\steam.exe" when dayIndex == 9 && hour is >= 20 and < 22 => 12 * Gb,
        "wuauserv" when dayIndex == 15 && hour is 10 or 11 => 1800 * Mb,
        "dosvc" when dayIndex == 15 && hour is 10 or 11 => 900 * Mb,
        _ => 0,
    };
}
