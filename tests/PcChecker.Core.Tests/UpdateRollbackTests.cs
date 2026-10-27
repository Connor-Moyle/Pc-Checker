using PcChecker.Core.Optimization;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Drivers;
using PcChecker.Core.WindowsUpdate;
using Xunit;

namespace PcChecker.Core.Tests;

public class UpdateToolExitCodeTests
{
    [Theory]
    [InlineData(0, UpdateToolOutcome.Success)]
    [InlineData(3010, UpdateToolOutcome.SuccessRestartNeeded)]
    [InlineData(1641, UpdateToolOutcome.SuccessRestartNeeded)]
    [InlineData(2359301, UpdateToolOutcome.SuccessRestartNeeded)]
    [InlineData(2359303, UpdateToolOutcome.NotInstalled)]
    [InlineData(1168, UpdateToolOutcome.NotInstalled)]
    [InlineData(5, UpdateToolOutcome.AccessDenied)]
    [InlineData(740, UpdateToolOutcome.AccessDenied)]
    [InlineData(1618, UpdateToolOutcome.Busy)]
    [InlineData(87, UpdateToolOutcome.InvalidArguments)]
    [InlineData(1223, UpdateToolOutcome.Cancelled)]
    [InlineData(-2, UpdateToolOutcome.TimedOut)]
    public void Win32_and_wusa_exit_codes_map_to_outcomes(int exitCode, UpdateToolOutcome expected)
    {
        Assert.Equal(expected, UpdateToolExitCodes.Map(exitCode, "wusa").Outcome);
    }

    [Theory]
    [InlineData(0x80070490u, UpdateToolOutcome.NotInstalled)]
    [InlineData(0x800F080Cu, UpdateToolOutcome.NotInstalled)]
    [InlineData(0x80240017u, UpdateToolOutcome.NotApplicable)]
    [InlineData(0x800F0825u, UpdateToolOutcome.NotRemovable)]
    [InlineData(0x800F0806u, UpdateToolOutcome.RestartFirst)]
    [InlineData(0x80070BC2u, UpdateToolOutcome.SuccessRestartNeeded)]
    [InlineData(0x80070005u, UpdateToolOutcome.AccessDenied)]
    [InlineData(0x80070057u, UpdateToolOutcome.InvalidArguments)]
    public void Hresult_exit_codes_from_dism_map_to_outcomes(uint exitCode, UpdateToolOutcome expected)
    {
        Assert.Equal(expected, UpdateToolExitCodes.Map(unchecked((int)exitCode), "DISM").Outcome);
    }

    [Fact]
    public void Restart_codes_succeed_and_ask_for_a_restart()
    {
        var result = UpdateToolExitCodes.Map(UpdateToolExitCodes.RestartRequired);
        Assert.True(result.Success);
        Assert.True(result.RestartNeeded);
        Assert.Contains("Restart", result.Message);
        Assert.False(UpdateToolExitCodes.Map(0).RestartNeeded);
        Assert.False(UpdateToolExitCodes.Map(UpdateToolExitCodes.WusaNotInstalled).Success);
    }

    [Fact]
    public void Unknown_codes_are_failures_that_show_the_code_and_name_the_tool()
    {
        var hresult = UpdateToolExitCodes.Map(unchecked((int)0x800F0ABC), "DISM");
        Assert.Equal(UpdateToolOutcome.Failed, hresult.Outcome);
        Assert.Contains("0x800F0ABC", hresult.Message);
        Assert.Contains("DISM", hresult.Message);

        var small = UpdateToolExitCodes.Map(1603, "wusa");
        Assert.Equal("1603", small.CodeText);
        Assert.Contains("wusa", small.Message);

        // A Windows Update error PC Checker already explains keeps that explanation.
        Assert.Equal(WuaResultCodes.Explain(unchecked((int)0x80073712)), UpdateToolExitCodes.Map(unchecked((int)0x80073712), "DISM").Message);
    }

    [Fact]
    public void Codes_are_formatted_like_windows_prints_them()
    {
        Assert.Equal("3010", UpdateToolExitCodes.FormatCode(3010));
        Assert.Equal("0x00240007", UpdateToolExitCodes.FormatCode(UpdateToolExitCodes.WusaNotInstalled));
        Assert.Equal("0x80070490", UpdateToolExitCodes.FormatCode(UpdateToolExitCodes.NotFound));
    }
}

public class DismOutputParserTests
{
    private const string Rollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10";
    private const string OlderRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.3915.1.6";

    private const string Table = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1150

        Image Version: 10.0.26100.4061

        Packages listing:

        ----------------------------------------------------------------------------- | ---------- | --------------- | ------------------
        Package Identity                                                              | State      | Release Type    | Install Time
        ----------------------------------------------------------------------------- | ---------- | --------------- | ------------------
        Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.9294.1              | Installed  | Update          | 5/14/2025 1:23 AM
        Package_for_KB5054979~31bf3856ad364e35~amd64~~26100.3772.1.1                  | Installed  | Update          | 4/9/2025 2:10 AM
        Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.3915.1.6                  | Superseded | Security Update | 4/9/2025 2:12 AM
        Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10                 | Installed  | Security Update | 5/14/2025 1:20 AM
        Package_for_ServicingStack_4058~31bf3856ad364e35~amd64~~26100.4058.1.0        | Installed  | Update          | 5/14/2025 1:10 AM

        The operation completed successfully.
        """;

    [Fact]
    public void Table_output_gives_every_package_with_its_state()
    {
        var packages = DismOutputParser.ParsePackages(Table);
        Assert.Equal(5, packages.Count);
        var current = Assert.Single(packages, p => p.Identity == Rollup);
        Assert.Equal(DismPackageState.Installed, current.State);
        Assert.Equal("Security Update", current.ReleaseType);
        Assert.Equal(new Version(26100, 4061, 1, 10), current.Version);
        Assert.Equal("Package_for_RollupFix", current.Name);
        Assert.Equal(DismPackageState.Superseded, packages.Single(p => p.Identity == OlderRollup).State);
    }

    [Fact]
    public void List_output_in_any_language_still_finds_the_identities()
    {
        // Labels are translated without /English; identities never are. The English state is used when present.
        const string list = """
            Paketidentität : Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10
            Status : Installed
            Freigabetyp : Security Update

            Paketidentität : Package_for_KB5054979~31bf3856ad364e35~amd64~~26100.3772.1.1
            Status : Installiert
            """;
        var packages = DismOutputParser.ParsePackages(list);
        Assert.Equal(2, packages.Count);
        Assert.Equal(DismPackageState.Installed, packages[0].State);
        Assert.Equal(DismPackageState.Other, packages[1].State);
        Assert.Empty(DismOutputParser.ParsePackages(null));
        Assert.Empty(DismOutputParser.ParsePackages("Error: 740\nElevated permissions are required."));
    }

    [Theory]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10", true)]
    [InlineData("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1", true)]
    [InlineData("Package_for_RollupFix", false)]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10 /Quiet", false)]
    [InlineData("C:\\temp\\evil.cab", false)]
    [InlineData("", false)]
    public void Package_identities_are_recognised_by_shape(string text, bool expected)
    {
        Assert.Equal(expected, DismOutputParser.IsPackageIdentity(text));
    }

    [Fact]
    public void Uninstall_window_is_read_from_the_english_label_or_the_only_bare_number()
    {
        const string english = """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.1150

            Image Version: 10.0.26100.4061

            Uninstall Window : 10

            The operation completed successfully.
            """;
        Assert.Equal(10, DismOutputParser.ParseUninstallWindow(english));
        Assert.Equal(30, DismOutputParser.ParseUninstallWindow("Version: 10.0.26100.1150\nDeinstallationsfenster : 30\n"));
        Assert.Null(DismOutputParser.ParseUninstallWindow("Version: 10.0.26100.1150\nImage Version: 10.0.26100.4061"));
        Assert.Null(DismOutputParser.ParseUninstallWindow("Fehler: 1168"));
        Assert.Null(DismOutputParser.ParseUninstallWindow(""));
    }
}

public class InstalledUpdateRulesTests
{
    private const string Rollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.10";
    private const string OlderRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.3915.1.6";
    private const string LcuTitle = "2025-05 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5058411) (26100.4061)";
    private const string OlderLcuTitle = "2025-04 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5055523) (26100.3915)";

    private static readonly DismPackage[] Packages =
    [
        new(OlderRollup, DismPackageState.Superseded, "Security Update"),
        new(Rollup, DismPackageState.Installed, "Security Update"),
        new("Package_for_KB5054979~31bf3856ad364e35~amd64~~26100.3772.1.1", DismPackageState.Installed, "Update"),
    ];

    [Fact]
    public void The_cumulative_package_is_the_installed_one_whose_build_matches_the_title()
    {
        Assert.Equal(Rollup, InstalledUpdateRules.SelectCumulativePackage(Packages, LcuTitle)?.Identity);

        // The April update's package is superseded, so it is not offered for removal.
        Assert.Null(InstalledUpdateRules.SelectCumulativePackage(Packages, OlderLcuTitle));

        // Without a build in the title, the installed package with the highest version.
        Assert.Equal(Rollup, InstalledUpdateRules.SelectCumulativePackage(Packages, "2025-05 Cumulative Update for Windows 11 (KB5058411)")?.Identity);
        Assert.Null(InstalledUpdateRules.SelectCumulativePackage([], LcuTitle));
        Assert.Equal(new Version(26100, 4061), InstalledUpdateRules.ParseBuild(LcuTitle));
        Assert.Null(InstalledUpdateRules.ParseBuild("Windows Malicious Software Removal Tool x64 - v5.141 (KB890830)"));
    }

    [Fact]
    public void Merge_joins_get_hotfix_history_and_dism_and_marks_replaced_cumulative_updates()
    {
        var qfe = new[]
        {
            new QuickFixRecord("KB5058411", "Security Update", "5/14/2025"),
            new QuickFixRecord("KB5055523", "Security Update", "4/9/2025"),
            new QuickFixRecord("KB5054979", "Update", "4/9/2025"),
            new QuickFixRecord("KB5054979", "Update", "4/9/2025"),
            new QuickFixRecord("KB5059502", "Update", "5/14/2025"),
            new QuickFixRecord("not-a-kb", "Update", ""),
        };
        var history = new[]
        {
            History(LcuTitle, "11111111-2222-3333-4444-555555555555"),
            History(OlderLcuTitle, null),
            History("2025-05 Servicing Stack Update for Windows 11 Version 24H2 for x64-based Systems (KB5059502)", null),
            History("2025-05 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5054979)", null),
        };

        var rows = InstalledUpdateRules.Merge(qfe, history, Packages);

        Assert.Equal(4, rows.Count);
        var lcu = rows.Single(r => r.Kb == "KB5058411");
        Assert.True(lcu.IsWindowsCumulative);
        Assert.Equal(Rollup, lcu.PackageName);
        Assert.Equal("11111111-2222-3333-4444-555555555555", lcu.UpdateId);
        Assert.Equal(LcuTitle, lcu.Title);
        Assert.Equal(new DateTimeOffset(2025, 5, 14, 0, 0, 0, TimeSpan.Zero), lcu.InstalledOn);

        var older = rows.Single(r => r.Kb == "KB5055523");
        Assert.True(older.IsSuperseded);
        Assert.Null(older.PackageName);

        var dotnet = rows.Single(r => r.Kb == "KB5054979");
        Assert.Equal(InstalledUpdateType.Cumulative, dotnet.Type);
        Assert.False(dotnet.IsWindowsCumulative);

        Assert.Equal(InstalledUpdateType.ServicingStack, rows.Single(r => r.Kb == "KB5059502").Type);
        Assert.True(rows[0].InstalledOn >= rows[^1].InstalledOn);
    }

    [Fact]
    public void Without_a_build_in_the_titles_the_newest_cumulative_update_owns_the_installed_package()
    {
        var qfe = new[] { new QuickFixRecord("KB5037771", "Security Update", "5/14/2024"), new QuickFixRecord("KB5036980", "Update", "4/23/2024") };
        var history = new[]
        {
            History("2024-05 Cumulative Update for Windows 11 Version 23H2 for x64-based Systems (KB5037771)", null),
            History("2024-04 Cumulative Update Preview for Windows 11 Version 23H2 for x64-based Systems (KB5036980)", null),
        };
        var rows = InstalledUpdateRules.Merge(qfe, history, [new DismPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~22621.3593.1.11", DismPackageState.Installed, null)]);

        Assert.Equal("Package_for_RollupFix~31bf3856ad364e35~amd64~~22621.3593.1.11", rows.Single(r => r.Kb == "KB5037771").PackageName);
        Assert.True(rows.Single(r => r.Kb == "KB5036980").IsSuperseded);
    }

    [Fact]
    public void Removal_uses_dism_for_the_windows_cumulative_update_and_wusa_for_others_and_refuses_servicing_stack()
    {
        var lcu = new InstalledUpdate("KB5058411", LcuTitle, null, InstalledUpdateType.Cumulative) { IsWindowsCumulative = true, PackageName = Rollup };
        var plan = InstalledUpdateRules.PlanUninstall(lcu);
        Assert.Equal(UninstallMethod.DismPackage, plan.Method);
        Assert.Equal(Rollup, plan.PackageName);

        Assert.Equal(UninstallMethod.Wusa, InstalledUpdateRules.PlanUninstall(new InstalledUpdate("KB5054979", ".NET", null, InstalledUpdateType.Cumulative)).Method);
        Assert.Equal(UninstallMethod.Wusa, InstalledUpdateRules.PlanUninstall(new InstalledUpdate("KB5062660", "Update", null, InstalledUpdateType.Update)).Method);

        var ssu = InstalledUpdateRules.PlanUninstall(new InstalledUpdate("KB5059502", "SSU", null, InstalledUpdateType.ServicingStack));
        Assert.False(ssu.CanUninstall);
        Assert.Contains("permanent", ssu.Explanation);

        Assert.False(InstalledUpdateRules.PlanUninstall(lcu with { PackageName = null, IsSuperseded = true }).CanUninstall);
        Assert.Contains("Settings", InstalledUpdateRules.PlanUninstall(lcu with { PackageName = null }).Explanation);
        Assert.False(InstalledUpdateRules.PlanUninstall(lcu with { PackageName = "Package_for_RollupFix /Add-Package" }).CanUninstall);
        Assert.False(InstalledUpdateRules.PlanUninstall(new InstalledUpdate("KBxyz", "Odd", null, InstalledUpdateType.Update)).CanUninstall);
    }

    [Theory]
    [InlineData("2025-05 Servicing Stack Update for Windows 10 Version 22H2 (KB5059502)")]
    [InlineData("2025-05 Wartungsstapelupdate für Windows 10 Version 22H2 (KB5059502)")]
    [InlineData("2025-05 Mise à jour de la pile de maintenance pour Windows 10 Version 22H2 (KB5059502)")]
    [InlineData("2025-05 Actualización de la pila de servicio para Windows 10 Version 22H2 (KB5059502)")]
    [InlineData("2025-05 Aggiornamento dello stack di manutenzione per Windows 10 Version 22H2 (KB5059502)")]
    [InlineData("2025-05 Atualização da Pilha de Manutenção para Windows 10 Version 22H2 (KB5059502)")]
    public void Servicing_stack_updates_are_recognised_in_the_shipped_languages(string title)
    {
        Assert.Equal(InstalledUpdateType.ServicingStack, InstalledUpdateRules.Classify(title, "Update", null));
    }

    [Fact]
    public void Other_types_come_from_the_title_and_the_untranslated_hotfix_description()
    {
        Assert.Equal(InstalledUpdateType.Cumulative, InstalledUpdateRules.Classify(LcuTitle, "Security Update", null));
        Assert.Equal(InstalledUpdateType.Security, InstalledUpdateRules.Classify(null, "Security Update", null));
        Assert.Equal(InstalledUpdateType.Hotfix, InstalledUpdateRules.Classify(null, "Hotfix", null));
        Assert.Equal(InstalledUpdateType.Update, InstalledUpdateRules.Classify(null, "Update", null));
        Assert.False(string.IsNullOrWhiteSpace(InstalledUpdateRules.Label(InstalledUpdateType.ServicingStack)));
    }

    [Theory]
    [InlineData("5/14/2025", 2025, 5, 14)]
    [InlineData("10/3/2024", 2024, 10, 3)]
    [InlineData("20250514", 2025, 5, 14)]
    [InlineData("01db c4d6 0d5c 8000", 0, 0, 0)]
    [InlineData("01dbc4d60d5c8000", 2025, 5, 14)]
    [InlineData("", 0, 0, 0)]
    [InlineData("soon", 0, 0, 0)]
    public void Hotfix_install_dates_are_read_in_every_format_windows_uses(string text, int year, int month, int day)
    {
        var date = InstalledUpdateRules.ParseQuickFixDate(text);
        if (year == 0)
        {
            Assert.Null(date);
        }
        else
        {
            Assert.Equal(new DateTime(year, month, day), date);
        }
    }

    [Fact]
    public void Tool_arguments_are_fixed_and_only_take_checked_values()
    {
        Assert.Equal(["/uninstall", "/kb:5058411", "/quiet", "/norestart"], InstalledUpdateRules.WusaUninstallArguments("KB5058411"));
        Assert.Equal(["/uninstall", "/kb:5058411", "/quiet", "/norestart"], InstalledUpdateRules.WusaUninstallArguments("5058411"));
        Assert.Throws<ArgumentException>(() => InstalledUpdateRules.WusaUninstallArguments("KB5058411 /forcerestart"));

        var dism = InstalledUpdateRules.DismRemoveArguments(Rollup);
        Assert.Equal(["/Online", "/Remove-Package", "/PackageName:" + Rollup, "/Quiet", "/NoRestart", "/English"], dism);
        Assert.Throws<ArgumentException>(() => InstalledUpdateRules.DismRemoveArguments("Package_for_RollupFix"));
        Assert.Contains("/English", InstalledUpdateRules.GetPackagesArguments());
    }

    [Theory]
    [InlineData("KB5058411", "KB5058411")]
    [InlineData("kb890830", "KB890830")]
    [InlineData(" 5058411 ", "KB5058411")]
    [InlineData("KB", null)]
    [InlineData("KB12", null)]
    [InlineData("File 1", null)]
    [InlineData(null, null)]
    public void Kb_numbers_are_normalised(string? text, string? expected)
    {
        Assert.Equal(expected, InstalledUpdateRules.NormalizeKb(text));
    }

    [Theory]
    [InlineData("2025-10 Cumulative Update for Windows 11 Version 25H2 for x64-based Systems (KB5066835) (26200.6899)", "26100.6899.1.7")]
    [InlineData("2025-10 Cumulative Update for Windows 11 Version 23H2 for x64-based Systems (KB5066793) (22631.6060)", "22621.6060.1.9")]
    [InlineData("2025-10 Cumulative Update for Windows 10 Version 22H2 for x64-based Systems (KB5066791) (19045.6456)", "19041.6456.1.4")]
    [InlineData("2025-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835) (26100.6899)", "26100.6899.1.7")]
    public void Enablement_package_releases_find_the_package_on_their_base_build(string title, string packageVersion)
    {
        var identity = "Package_for_RollupFix~31bf3856ad364e35~amd64~~" + packageVersion;
        DismPackage[] packages = [new(identity, DismPackageState.Installed, "Security Update")];

        Assert.Equal(identity, InstalledUpdateRules.SelectCumulativePackage(packages, title)?.Identity);

        var kb = UpdateClassifier.ExtractKb(title)!;
        var row = InstalledUpdateRules.Merge([new QuickFixRecord(kb, "Security Update", "10/14/2025")], [History(title, null)], packages).Single();
        Assert.Equal(identity, row.PackageName);
        Assert.False(row.IsSuperseded);
        Assert.Equal(UninstallMethod.DismPackage, InstalledUpdateRules.PlanUninstall(row).Method);
    }

    [Fact]
    public void Enablement_package_releases_still_need_the_same_revision_and_see_newer_packages_as_replacements()
    {
        DismPackage[] packages = [new("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.6899.1.7", DismPackageState.Installed, null)];
        const string September = "2025-09 Cumulative Update for Windows 11 Version 25H2 for x64-based Systems (KB5065426) (26200.6584)";

        Assert.Null(InstalledUpdateRules.SelectCumulativePackage(packages, September));
        Assert.Null(InstalledUpdateRules.SelectCumulativePackage(packages, "2025-10 Cumulative Update for Windows 11 (KB5066835) (22631.6899)"));

        var row = InstalledUpdateRules.Merge([new QuickFixRecord("KB5065426", "Security Update", "9/9/2025")], [History(September, null)], packages).Single();
        Assert.True(row.IsSuperseded);
        Assert.Null(row.PackageName);

        Assert.Equal(26100, InstalledUpdateRules.ServicingBaseBuild(26200));
        Assert.Equal(22621, InstalledUpdateRules.ServicingBaseBuild(22631));
        Assert.Equal(19041, InstalledUpdateRules.ServicingBaseBuild(19045));
        Assert.Equal(22000, InstalledUpdateRules.ServicingBaseBuild(22000));
    }

    [Fact]
    public void An_update_without_its_own_package_next_to_a_servicing_stack_package_is_refused_in_any_language()
    {
        DismPackage[] packages =
        [
            new(Rollup, DismPackageState.Installed, "Security Update"),
            new("Package_for_ServicingStack_4058~31bf3856ad364e35~amd64~~26100.4058.1.0", DismPackageState.Installed, "Update"),
            new("Package_for_KB5062660~31bf3856ad364e35~amd64~~26100.4061.1.1", DismPackageState.Installed, "Update"),
            new("Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.9294.1", DismPackageState.Installed, "Update"),
        ];
        var qfe = new[]
        {
            new QuickFixRecord("KB5059502", "Update", "5/14/2025"),
            new QuickFixRecord("KB5059503", "Update", "5/14/2025"),
            new QuickFixRecord("KB5062660", "Update", "5/14/2025"),
            new QuickFixRecord("KB5054979", "Update", "5/14/2025"),
        };
        var history = new[]
        {
            // A Japanese title: no known "servicing stack" words.
            History("2025-05 x64 ベース システム用 Windows 11 Version 24H2 のサービス スタック更新プログラム (KB5059503)", null),
            History("2025-05 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5054979)", null),
        };

        var rows = InstalledUpdateRules.Merge(qfe, history, packages);

        // No history at all (KB5059502) and a title in another language (KB5059503): both refused before anything runs.
        foreach (var kb in new[] { "KB5059502", "KB5059503" })
        {
            var row = rows.Single(r => r.Kb == kb);
            Assert.True(row.IsLikelyServicingStack);
            var plan = InstalledUpdateRules.PlanUninstall(row);
            Assert.False(plan.CanUninstall);
            Assert.Contains("servicing stack", plan.Explanation);
        }

        // An update with its own package, and a .NET update, still go to wusa.
        Assert.Equal(UninstallMethod.Wusa, InstalledUpdateRules.PlanUninstall(rows.Single(r => r.Kb == "KB5062660")).Method);
        Assert.Equal(UninstallMethod.Wusa, InstalledUpdateRules.PlanUninstall(rows.Single(r => r.Kb == "KB5054979")).Method);

        // Without the DISM list (DISM failed), nothing is guessed.
        Assert.DoesNotContain(InstalledUpdateRules.Merge(qfe, history, []), r => r.IsLikelyServicingStack);
    }

    private static UpdateHistoryRecord History(string title, string? id) =>
        new(title, UpdateHistoryMapper.OperationInstallation, WuaResultCodes.Succeeded, 0, new DateTime(2025, 5, 14, 3, 0, 0, DateTimeKind.Utc), [], id, null);
}

public class RollbackRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Days_left_count_from_the_upgrade()
    {
        var window = RollbackRules.Evaluate(10, false, Now.AddDays(-3).AddHours(-2), true, Now);
        Assert.Equal(RollbackState.Available, window.State);
        Assert.Equal(7, window.DaysLeft);
        Assert.Equal(3, window.DaysSinceUpgrade);
        Assert.Equal(Now.AddDays(-3).AddHours(-2).AddDays(10), window.Deadline);
        Assert.Contains("7 more days", window.Message);
    }

    [Fact]
    public void Missing_windows_old_dism_not_found_and_an_expired_window_each_say_why()
    {
        Assert.Equal(RollbackState.NotAvailable, RollbackRules.Evaluate(10, false, Now.AddDays(-2), false, Now).State);
        Assert.Equal(RollbackState.NotAvailable, RollbackRules.Evaluate(null, true, Now.AddDays(-2), true, Now).State);
        Assert.Equal(RollbackState.Unknown, RollbackRules.Evaluate(null, false, Now.AddDays(-2), true, Now).State);

        var expired = RollbackRules.Evaluate(10, false, Now.AddDays(-12), true, Now);
        Assert.Equal(RollbackState.Expired, expired.State);
        Assert.Equal(0, expired.DaysLeft);

        var noDate = RollbackRules.Evaluate(10, false, null, true, Now);
        Assert.Equal(RollbackState.Available, noDate.State);
        Assert.Null(noDate.DaysLeft);

        Assert.True(RollbackRules.MeansNotAvailable(1168));
        Assert.True(RollbackRules.MeansNotAvailable(unchecked((int)0x80070490)));
        Assert.False(RollbackRules.MeansNotAvailable(740));
    }

    [Fact]
    public void A_longer_window_must_be_2_to_60_days_and_longer_than_the_time_already_gone()
    {
        Assert.Null(RollbackRules.ValidateExtension(30, 3));
        Assert.NotNull(RollbackRules.ValidateExtension(61, 3));
        Assert.NotNull(RollbackRules.ValidateExtension(1, null));
        Assert.NotNull(RollbackRules.ValidateExtension(14, 20));
        Assert.Equal([14, 21, 30, 45, 60], RollbackRules.ExtensionOptions(10, 3));
        Assert.Equal([30, 45, 60], RollbackRules.ExtensionOptions(21, 3));
        Assert.Equal([45, 60], RollbackRules.ExtensionOptions(10, 40));
        Assert.Empty(RollbackRules.ExtensionOptions(60, 3));
    }

    [Fact]
    public void Dism_arguments_never_restart_on_their_own_and_ask_for_english()
    {
        Assert.Equal(["/Online", "/Set-OSUninstallWindow", "/Value:30", "/English"], RollbackRules.SetWindowArguments(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => RollbackRules.SetWindowArguments(90));
        Assert.Contains("/NoRestart", RollbackRules.GoBackArguments());
        Assert.Contains("/Quiet", RollbackRules.GoBackArguments());
        Assert.Contains("/English", RollbackRules.GetWindowArguments());
    }

    [Fact]
    public void The_upgrade_date_comes_from_install_date_seconds()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero), RollbackRules.FromInstallDate(1_792_836_000));
        Assert.Null(RollbackRules.FromInstallDate(0));
        Assert.Null(RollbackRules.FromInstallDate(null));
    }
}

public class HiddenUpdateRulesTests
{
    [Theory]
    [InlineData(true, false, false, HideDecision.Change)]
    [InlineData(true, true, false, HideDecision.AlreadyDone)]
    [InlineData(true, false, true, HideDecision.Mandatory)]
    [InlineData(false, true, false, HideDecision.Change)]
    [InlineData(false, true, true, HideDecision.Change)]
    [InlineData(false, false, false, HideDecision.AlreadyDone)]
    public void Hide_and_show_decisions_follow_the_current_state(bool hide, bool isHidden, bool mandatory, HideDecision expected)
    {
        Assert.Equal(expected, HiddenUpdateRules.Decide(hide, isHidden, mandatory));
    }

    [Fact]
    public void Messages_say_what_happened_to_the_update()
    {
        Assert.Contains("will not offer or install", HiddenUpdateRules.Message(HideDecision.Change, true, "KB5058411"));
        Assert.Contains("shown again", HiddenUpdateRules.Message(HideDecision.Change, false, "KB5058411"));
        Assert.Contains("already hidden", HiddenUpdateRules.Message(HideDecision.AlreadyDone, true, "KB5058411"));
        Assert.Contains("cannot be hidden", HiddenUpdateRules.Message(HideDecision.Mandatory, true, "KB5058411"));
        Assert.Equal("Hidden", HiddenUpdateRules.StateText(true, false));
        Assert.Equal("Offered", HiddenUpdateRules.StateText(false, false));
        Assert.Equal("Installed (hidden)", HiddenUpdateRules.StateText(true, true));
    }

    [Fact]
    public void Only_a_guid_reaches_the_search_criteria()
    {
        Assert.Equal("UpdateID='11111111-2222-3333-4444-555555555555'", HiddenUpdateRules.CriteriaForId("{11111111-2222-3333-4444-555555555555}"));
        Assert.Null(HiddenUpdateRules.CriteriaForId("x' or IsInstalled=1 or UpdateID='y"));
        Assert.Null(HiddenUpdateRules.CriteriaForId(null));
    }

    [Fact]
    public void A_removed_update_is_matched_by_its_kb()
    {
        Assert.True(HiddenUpdateRules.MatchesKb("KB5058411", ["5058411"], null));
        Assert.True(HiddenUpdateRules.MatchesKb("5058411", [], "2025-05 Cumulative Update (KB5058411) (26100.4061)"));
        Assert.False(HiddenUpdateRules.MatchesKb("KB5058411", ["5058412"], "Other (KB5058412)"));
        Assert.False(HiddenUpdateRules.MatchesKb(null, ["5058411"], null));
    }
}

public class DemoUpdateRollbackServiceTests
{
    [Fact]
    public async Task Demo_shows_installed_updates_a_hidden_update_and_seven_days_to_go_back()
    {
        var svc = new DemoUpdateRollbackService();
        var installed = await svc.GetInstalledUpdatesAsync(CancellationToken.None);
        Assert.True(installed.Updates.Count >= 4);
        Assert.Contains(installed.Updates, u => InstalledUpdateRules.PlanUninstall(u).Method == UninstallMethod.DismPackage);
        Assert.Contains(installed.Updates, u => InstalledUpdateRules.PlanUninstall(u).Method == UninstallMethod.Wusa);
        Assert.Contains(installed.Updates, u => u.Type == InstalledUpdateType.ServicingStack && !InstalledUpdateRules.PlanUninstall(u).CanUninstall);

        Assert.Single((await svc.GetHiddenUpdatesAsync(CancellationToken.None)).Updates);

        var window = await svc.GetRollbackWindowAsync(CancellationToken.None);
        Assert.Equal(RollbackState.Available, window.State);
        Assert.Equal(7, window.DaysLeft);
    }

    [Fact]
    public async Task Demo_uninstall_hide_unhide_and_extend_change_only_memory()
    {
        var svc = new DemoUpdateRollbackService();
        var installed = (await svc.GetInstalledUpdatesAsync(CancellationToken.None)).Updates;
        var ssu = installed.First(u => u.Type == InstalledUpdateType.ServicingStack);
        Assert.False((await svc.UninstallAsync(ssu, null, CancellationToken.None)).Success);

        var lcu = installed.First(u => u.IsWindowsCumulative && !u.IsSuperseded);
        var removed = await svc.UninstallAsync(lcu, null, CancellationToken.None);
        Assert.True(removed.Success);
        Assert.True(removed.RebootRequired);
        Assert.DoesNotContain((await svc.GetInstalledUpdatesAsync(CancellationToken.None)).Updates, u => u.Kb == lcu.Kb);

        Assert.True((await svc.HideAsync(new UpdateHideTarget(lcu.UpdateId, lcu.Kb, lcu.Title), CancellationToken.None)).Success);
        var hidden = (await svc.GetHiddenUpdatesAsync(CancellationToken.None)).Updates;
        Assert.Equal(2, hidden.Count);
        Assert.True((await svc.UnhideAsync(hidden.First(h => h.Kb == lcu.Kb), CancellationToken.None)).Success);
        Assert.Single((await svc.GetHiddenUpdatesAsync(CancellationToken.None)).Updates);

        Assert.True((await svc.SetRollbackWindowAsync(30, CancellationToken.None)).Success);
        Assert.Equal(27, (await svc.GetRollbackWindowAsync(CancellationToken.None)).DaysLeft);
        Assert.False((await svc.SetRollbackWindowAsync(90, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task A_pending_update_hidden_in_the_demo_leaves_the_next_check_until_shown_again()
    {
        var updates = new DemoWindowsUpdateService();
        var rollback = new DemoUpdateRollbackService(updates);
        var preview = (await updates.CheckForUpdatesAsync(null, CancellationToken.None)).Updates.First(u => u.IsOptional);

        Assert.True((await rollback.HideAsync(new UpdateHideTarget(preview.Id, preview.KbArticle, preview.Title), CancellationToken.None)).Success);
        Assert.DoesNotContain((await updates.CheckForUpdatesAsync(null, CancellationToken.None)).Updates, u => u.Id == preview.Id);

        var hidden = (await rollback.GetHiddenUpdatesAsync(CancellationToken.None)).Updates.Single(h => h.Id == preview.Id);
        Assert.True((await rollback.UnhideAsync(hidden, CancellationToken.None)).Success);
        Assert.Contains((await updates.CheckForUpdatesAsync(null, CancellationToken.None)).Updates, u => u.Id == preview.Id);
    }
}

public class WindowsOldCleanupWarningTests
{
    [Fact]
    public void Cleanup_warns_that_removing_windows_old_ends_going_back()
    {
        var category = CleanupCatalog.Find(CleanupRules.WindowsOldId)!;
        Assert.Contains("go back", category.Description);
        Assert.Contains("Roll back", category.Description);
        Assert.Contains("Storage Sense", CleanupRules.WindowsOldNote);
        Assert.Contains("Disk Cleanup", CleanupRules.WindowsOldNote);
        Assert.Equal(RiskLevel.Advanced, category.Risk);
        Assert.False(category.EnabledByDefault);
        Assert.True(CleanupRules.IsOneWay(CleanupRules.WindowsOldId));
    }
}
