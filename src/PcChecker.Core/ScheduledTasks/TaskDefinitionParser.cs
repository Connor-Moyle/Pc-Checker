using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Reads Task Scheduler XML: one task (<c>IRegisteredTask.Xml</c>, the files under System32\Tasks) or the whole list
/// that <c>schtasks /Query /XML ONE</c> prints (a <c>Tasks</c> element where a comment with the task path comes before
/// each task). Element names are matched without their namespace, DTDs are refused, and nothing depends on the Windows
/// display language: the XML schema is the same in every language, only free text such as the author differs. Pure.
/// </summary>
public static class TaskDefinitionParser
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>The definition of one task, or null when the text is not a task definition.</summary>
    /// <param name="localZone">Converts start times that carry a UTC offset; null uses the PC's time zone.</param>
    public static TaskDefinition? Parse(string? xml, TimeZoneInfo? localZone = null)
    {
        var root = Load(xml);
        return root is not null && root.Name.LocalName == "Task" ? Read(root, localZone ?? TimeZoneInfo.Local) : null;
    }

    /// <summary>Every task of a <c>schtasks /Query /XML ONE</c> listing with its path; tasks without a path comment are skipped.</summary>
    public static IReadOnlyList<(string Path, TaskDefinition Definition)> ParseList(string? xml, TimeZoneInfo? localZone = null)
    {
        var root = Load(xml);
        if (root is null)
        {
            return [];
        }

        if (root.Name.LocalName == "Task")
        {
            var single = Read(root, localZone ?? TimeZoneInfo.Local);
            return single.Uri is { } uri ? [(TaskPaths.Normalize(uri), single)] : [];
        }

        var result = new List<(string, TaskDefinition)>();
        string? pendingPath = null;
        foreach (var node in root.Nodes())
        {
            switch (node)
            {
                case XComment comment:
                    var text = comment.Value.Trim();
                    pendingPath = text.StartsWith('\\') ? TaskPaths.Normalize(text) : pendingPath;
                    break;
                case XElement element when element.Name.LocalName == "Task":
                    var definition = Read(element, localZone ?? TimeZoneInfo.Local);
                    var path = pendingPath ?? (definition.Uri is { } u && u.StartsWith('\\') ? TaskPaths.Normalize(u) : null);
                    if (path is not null)
                    {
                        result.Add((path, definition));
                    }

                    pendingPath = null;
                    break;
            }
        }

        return result;
    }

    private static XElement? Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            // The declaration says UTF-16 while the text is already a .NET string: read it as a fragment-free document
            // through a StringReader, which ignores the declared encoding.
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), Settings);
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static TaskDefinition Read(XElement task, TimeZoneInfo zone)
    {
        var info = Child(task, "RegistrationInfo");
        var settings = Child(task, "Settings");
        var actionsElement = Child(task, "Actions");

        var principals = Child(task, "Principals")?.Elements().Where(e => e.Name.LocalName == "Principal").ToList() ?? [];
        var context = actionsElement?.Attribute("Context")?.Value;
        var principal = principals.FirstOrDefault(p => context is not null && string.Equals(p.Attribute("id")?.Value, context, StringComparison.Ordinal))
                        ?? principals.FirstOrDefault();

        var triggers = Child(task, "Triggers")?.Elements().Select(t => ReadTrigger(t, zone)).ToList() ?? [];
        var actions = actionsElement?.Elements().Select(ReadAction).OfType<TaskAction>().ToList() ?? [];

        return new TaskDefinition(
            Text(info, "Author"),
            Text(info, "Description"),
            triggers,
            actions,
            Text(principal, "UserId"),
            Text(principal, "GroupId"),
            Text(principal, "LogonType"),
            string.Equals(Text(principal, "RunLevel"), "HighestAvailable", StringComparison.OrdinalIgnoreCase),
            Bool(settings, "Hidden", false),
            Bool(settings, "Enabled", true))
        {
            Uri = Text(info, "URI"),
        };
    }

    private static TaskTrigger ReadTrigger(XElement trigger, TimeZoneInfo zone)
    {
        var kind = trigger.Name.LocalName switch
        {
            "TimeTrigger" => TaskTriggerKind.Once,
            "BootTrigger" => TaskTriggerKind.Boot,
            "LogonTrigger" => TaskTriggerKind.Logon,
            "IdleTrigger" => TaskTriggerKind.Idle,
            "EventTrigger" => TaskTriggerKind.Event,
            "RegistrationTrigger" => TaskTriggerKind.Registration,
            "SessionStateChangeTrigger" => TaskTriggerKind.SessionStateChange,
            "CalendarTrigger" => CalendarKind(trigger),
            _ => TaskTriggerKind.Custom,
        };

        var repetition = Child(trigger, "Repetition");
        var result = new TaskTrigger(kind)
        {
            Enabled = Bool(trigger, "Enabled", true),
            Start = ParseStart(Text(trigger, "StartBoundary"), zone),
            RepeatEvery = Duration(Text(repetition, "Interval")),
            RepeatFor = Duration(Text(repetition, "Duration")),
            Delay = Duration(Text(trigger, "Delay")),
            UserId = Text(trigger, "UserId"),
            StateChange = Text(trigger, "StateChange"),
        };

        return kind switch
        {
            TaskTriggerKind.Daily => result with { Interval = Int(Child(trigger, "ScheduleByDay"), "DaysInterval") },
            TaskTriggerKind.Weekly => result with
            {
                Interval = Int(Child(trigger, "ScheduleByWeek"), "WeeksInterval"),
                DaysOfWeek = Days(Child(Child(trigger, "ScheduleByWeek"), "DaysOfWeek")),
            },
            TaskTriggerKind.Monthly => ReadMonthly(result, Child(trigger, "ScheduleByMonth")),
            TaskTriggerKind.MonthlyDayOfWeek => result with
            {
                DaysOfWeek = Days(Child(Child(trigger, "ScheduleByMonthDayOfWeek"), "DaysOfWeek")),
                Weeks = Child(Child(trigger, "ScheduleByMonthDayOfWeek"), "Weeks")?.Elements().Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList() ?? [],
                Months = Months(Child(Child(trigger, "ScheduleByMonthDayOfWeek"), "Months")),
            },
            _ => result,
        };
    }

    private static TaskTriggerKind CalendarKind(XElement trigger)
    {
        foreach (var child in trigger.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "ScheduleByDay": return TaskTriggerKind.Daily;
                case "ScheduleByWeek": return TaskTriggerKind.Weekly;
                case "ScheduleByMonth": return TaskTriggerKind.Monthly;
                case "ScheduleByMonthDayOfWeek": return TaskTriggerKind.MonthlyDayOfWeek;
            }
        }

        return TaskTriggerKind.Once;
    }

    private static TaskTrigger ReadMonthly(TaskTrigger trigger, XElement? schedule)
    {
        var days = new List<int>();
        bool last = false;
        foreach (var day in Child(schedule, "DaysOfMonth")?.Elements() ?? [])
        {
            var value = day.Value.Trim();
            if (string.Equals(value, "Last", StringComparison.OrdinalIgnoreCase))
            {
                last = true;
            }
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d is >= 1 and <= 31)
            {
                days.Add(d);
            }
        }

        return trigger with { DaysOfMonth = days, LastDayOfMonth = last, Months = Months(Child(schedule, "Months")) };
    }

    private static TaskAction? ReadAction(XElement action) => action.Name.LocalName switch
    {
        "Exec" => new TaskAction(TaskActionKind.Exec, Text(action, "Command"), Text(action, "Arguments"), Text(action, "WorkingDirectory")),
        "ComHandler" => new TaskAction(TaskActionKind.ComHandler, null, Text(action, "Data"), null, Text(action, "ClassId")),
        "SendEmail" => new TaskAction(TaskActionKind.SendEmail, null),
        "ShowMessage" => new TaskAction(TaskActionKind.ShowMessage, null),
        _ => null,
    };

    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static IReadOnlyList<DayOfWeek> Days(XElement? days) =>
        days?.Elements()
            .Select(e => Array.FindIndex(DayNames, n => string.Equals(n, e.Name.LocalName, StringComparison.Ordinal)))
            .Where(i => i >= 0)
            .Select(i => (DayOfWeek)i)
            .Distinct()
            .OrderBy(d => ((int)d + 6) % 7) // Monday first
            .ToList() ?? [];

    private static IReadOnlyList<int> Months(XElement? months) =>
        months?.Elements()
            .Select(e => Array.FindIndex(MonthNames, n => string.Equals(n, e.Name.LocalName, StringComparison.Ordinal)) + 1)
            .Where(m => m >= 1)
            .Distinct()
            .Order()
            .ToList() ?? [];

    /// <summary>"2024-05-01T03:00:00" is local clock time; a value with Z or an offset is converted to <paramref name="zone"/>.</summary>
    public static DateTime? ParseStart(string? text, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        bool hasOffset = value.EndsWith('Z') || (value.Length > 19 && (value[^6] == '+' || value[^6] == '-'));
        if (hasOffset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return TimeZoneInfo.ConvertTime(withOffset, zone).DateTime;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;
    }

    /// <summary>ISO 8601 durations such as PT1H, P1D or PT30M; null for missing or invalid text.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var span = XmlConvert.ToTimeSpan(text.Trim());
            return span > TimeSpan.Zero ? span : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement? parent, string name) =>
        Child(parent, name)?.Value.Trim() is { Length: > 0 } value ? value : null;

    private static bool Bool(XElement? parent, string name, bool fallback) =>
        Text(parent, name) is { } value ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1" : fallback;

    private static int Int(XElement? parent, string name) =>
        int.TryParse(Text(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : 1;
}
