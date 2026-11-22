using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;

namespace RightHandBackup;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>A folder to back up: its name (the folder it gets in each snapshot), where it is, and which files.</summary>
public sealed record Source(string Name, string Folder, IReadOnlyList<Glob> Include, IReadOnlyList<Glob> Exclude);

/// <summary>How long snapshots are kept: every one from the last <c>AllHours</c>, then the newest of each day for
/// <c>DailyDays</c>, then the newest of each week for <c>WeeklyWeeks</c>.</summary>
public sealed record Keep(int AllHours, int DailyDays, int WeeklyWeeks);

/// <summary>The settings file. See the README and <see cref="Template"/>. Read strictly: an unknown key, or a value of
/// the wrong type, is an error that names it.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "RIGHT_HAND_BACKUP_CONFIG";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# right-hand-backup settings. See the README.\n" +
        "# base = \"base-config.toml\"        # settings shared with other tools ([paths] variables)\n" +
        "\n" +
        "# Where snapshots go: a folder per machine below it. A synced folder (OneDrive) keeps them off this PC.\n" +
        "destination = \"%OneDriveCommercial%/right-hand-backup\"\n" +
        "# machine = \"%COMPUTERNAME%\"        # the folder for this PC's snapshots (default: the computer's name)\n" +
        "max_snapshot_mb = 50               # over this, nothing is written: something big got included\n" +
        "\n" +
        "[schedule]                         # when the task runs (background-tools-lib's shared keys)\n" +
        "every_minutes = 120                # how often a run looks for changes\n" +
        "after_unlock_minutes = 5           # and this long after each unlock (0 for none)\n" +
        "\n" +
        "[keep]\n" +
        "all_hours = 48                     # every snapshot from the last 48 hours\n" +
        "daily_days = 30                    # then the newest of each day for 30 days\n" +
        "weekly_weeks = 12                  # then the newest of each week for 12 weeks\n" +
        "\n" +
        "# One [[source]] per folder. Patterns are relative to the folder, with / between folders:\n" +
        "# * and ? stay within a folder, ** spans any number of them.\n" +
        "[[source]]\n" +
        "name = \"my-tool\"\n" +
        "folder = \"%LOCALAPPDATA%/my-tool\"\n" +
        "include = [\"*.json\"]\n" +
        "# exclude = [\"*.bak-*\"]\n" +
        "\n" +
        "[restore]\n" +
        "# staging = \"%LOCALAPPDATA%/right-hand-backup/restore\"\n" +
        "# stop_tasks = [\"my-tool run\"]           # restore --apply refuses while any of these is running\n" +
        "# warn_if_older = [\"my-tool/state.json\"]  # files that stop something being done twice\n";

    public static readonly string[] KnownKeys =
    [
        "log_file", "state_file", "destination", "machine", "every_minutes", "after_unlock_minutes", "max_snapshot_mb",
        "notify", "keep", "source", "restore", "schedule",
    ];

    public required string Path { get; init; }
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }

    /// <summary>The folder holding a folder per machine.</summary>
    public required string Destination { get; init; }

    public required string Machine { get; init; }
    public int EveryMinutes { get; init; } = 120;
    public int? AfterUnlockMinutes { get; init; } = 5;

    /// <summary>The task's schedule when the settings say nothing: every 120 minutes, at logon and 5 minutes after each unlock.</summary>
    public static readonly ScheduleSettings DefaultSchedule = new() { EveryMinutes = 120, AtLogon = true, AfterUnlockMinutes = 5 };

    /// <summary>[schedule] (0.3.0): background-tools-lib's shared keys. The top-level every_minutes and after_unlock_minutes
    /// are the old way, still read; setting both forms is an error.</summary>
    public ScheduleSettings Schedule { get; init; } = DefaultSchedule;
    public int MaxSnapshotMb { get; init; } = 50;
    public bool Notify { get; init; } = true;
    public Keep Keep { get; init; } = new(48, 30, 12);
    public required IReadOnlyList<Source> Sources { get; init; }
    public required string Staging { get; init; }
    public IReadOnlyList<string> StopTasks { get; init; } = [];
    public IReadOnlyList<Glob> WarnIfOlder { get; init; } = [];

    /// <summary>This machine's snapshots (or another's, for a restore from it).</summary>
    public string MachineFolder(string? machine = null) => System.IO.Path.Combine(Destination, machine ?? Machine);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"%[A-Za-z0-9_()-]+%")]
    private static partial Regex Unexpanded();

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "right-hand-backup"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "right-hand-backup", "config.toml"));
    }

    public static Config Load(string? path = null, Func<string, string?>? env = null, string? destination = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `right-hand-backup config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");

        var folder = System.IO.Path.GetDirectoryName(full)!;
        var expandWith = env;
        UsageException Bad(string message) => new($"{full}: {message}");
        string PathSetting(string value, string where)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, expandWith), expandWith);
            if (Unexpanded().Match(expanded) is { Success: true } m)
                throw Bad($"{where} uses {m.Value}, which isn't set (an environment variable, or a [paths] name in the base).");
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        }
        void Strict(TomlTable table, string where, params string[] keys)
        {
            foreach (var key in table.Keys)
                if (!keys.Contains(key))
                    throw Bad($"[{where}] has an unknown setting '{key}' (known: {string.Join(", ", keys)}).");
        }
        string? Text(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v) ? v as string is { Length: > 0 } s ? s : throw Bad($"{where} should be text, and not empty.") : null;
        bool Flag(string key, bool fallback) =>
            raw.TryGetValue(key, out var v) ? v as bool? ?? throw Bad($"{key} should be true or false.") : fallback;
        int Whole(TomlTable table, string key, string where, int fallback, int min, int max) =>
            table.TryGetValue(key, out var v) ? v is long n && n >= min && n <= max ? (int)n : throw Bad($"{where} should be a whole number from {min} to {max}.") : fallback;
        IReadOnlyList<string> Strings(TomlTable table, string key, string where) =>
            table.TryGetValue(key, out var v)
                ? v is TomlArray a && a.All(x => x is string) ? a.Select(x => (string)x!).ToList() : throw Bad($"{where} should be a list of text.")
                : [];
        IReadOnlyList<Glob> Globs(TomlTable table, string key, string where)
        {
            var list = new List<Glob>();
            foreach (var text in Strings(table, key, where))
            {
                try
                {
                    list.Add(Glob.Parse(text));
                }
                catch (FormatException error)
                {
                    throw Bad($"{where}: {error.Message}");
                }
            }
            return list;
        }
        TomlTable Section(string name)
        {
            if (!raw.TryGetValue(name, out var v))
                return [];
            return v as TomlTable ?? throw Bad($"{name} should be a [{name}] section.");
        }

        var keep = Section("keep");
        Strict(keep, "keep", "all_hours", "daily_days", "weekly_weeks");
        var restore = Section("restore");
        Strict(restore, "restore", "staging", "stop_tasks", "warn_if_older");

        var sources = new List<Source>();
        if (raw.TryGetValue("source", out var s))
        {
            if (s is not TomlTableArray tables)
                throw Bad("source should be [[source]] sections.");
            var i = 0;
            foreach (var table in tables)
            {
                i++;
                var where = $"source {i}";
                Strict(table, "source", "name", "folder", "include", "exclude");
                var name = Text(table, "name", $"{where}: name") ?? throw Bad($"{where} needs a name.");
                if (!NamePattern().IsMatch(name))
                    throw Bad($"{where}: name '{name}' should be letters, digits, '.', '_' or '-' (it names a folder in each snapshot).");
                if (sources.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw Bad($"two sources are named '{name}'.");
                var folderText = Text(table, "folder", $"source {name}: folder") ?? throw Bad($"source {name} needs a folder.");
                var include = Globs(table, "include", $"source {name}: include");
                if (include.Count == 0)
                    throw Bad($"source {name} needs include: the files to back up, such as [\"*.json\"].");
                sources.Add(new Source(name, PathSetting(folderText, $"source {name}: folder"), include, Globs(table, "exclude", $"source {name}: exclude")));
            }
        }

        var destinationText = destination ?? Text(raw, "destination", "destination")
            ?? throw Bad("destination is needed: the folder snapshots go to (a folder per machine below it).");
        var dest = destination is not null
            ? PyPath.Resolve(PyPath.ExpandVars(PyPath.ExpandUser(destination, expandWith), expandWith))
            : PathSetting(destinationText, "destination");
        foreach (var source in sources)
            if (Inside(dest, source.Folder) || Inside(source.Folder, dest))
                throw Bad($"destination ({dest}) and source {source.Name} ({source.Folder}) overlap: a snapshot would back up snapshots.");

        var machineText = Text(raw, "machine", "machine");
        var machine = machineText is not null ? PyPath.ExpandVars(machineText, expandWith) : env("COMPUTERNAME") is { Length: > 0 } c ? c : "this-pc";
        if (!NamePattern().IsMatch(machine))
            throw Bad($"machine '{machine}' should be letters, digits, '.', '_' or '-' (it names a folder).");

        var stopTasks = Strings(restore, "stop_tasks", "restore.stop_tasks");

        // [schedule] (0.3.0), or the old top-level keys, never both.
        ScheduleSettings schedule;
        if (raw.TryGetValue("schedule", out var scheduleValue))
        {
            if (raw.ContainsKey("every_minutes") || raw.ContainsKey("after_unlock_minutes"))
                throw Bad("every_minutes or after_unlock_minutes is set both at the top and in [schedule]: keep [schedule] (the top-level keys are the old way).");
            if (scheduleValue is not TomlTable scheduleTable)
                throw Bad("[schedule] should be a table (every_minutes, after_unlock_minutes, at_logon, hours, days, end_date).");
            try
            {
                schedule = ScheduleSettings.Parse(scheduleTable, DefaultSchedule, TaskKind.Job, $"{full}: [schedule]");
            }
            catch (FormatException error)
            {
                throw new UsageException(error.Message.TrimEnd());
            }
        }
        else
        {
            var oldUnlock = raw.ContainsKey("after_unlock_minutes") ? Whole(raw, "after_unlock_minutes", "after_unlock_minutes", 5, 0, 60) : 5;
            schedule = DefaultSchedule with
            {
                EveryMinutes = Whole(raw, "every_minutes", "every_minutes", 120, 1, 1440),
                AfterUnlockMinutes = oldUnlock,
            };
        }

        return new Config
        {
            Schedule = schedule,
            Path = full,
            LogFile = Text(raw, "log_file", "log_file") is { } log ? PathSetting(log, "log_file") : PyPath.Pure(System.IO.Path.Combine(Local(env), "right-hand-backup.log")),
            StateFile = Text(raw, "state_file", "state_file") is { } state ? PathSetting(state, "state_file") : PyPath.Pure(System.IO.Path.Combine(Local(env), "state.json")),
            Destination = dest,
            Machine = machine,
            EveryMinutes = schedule.EveryMinutes ?? 120,
            AfterUnlockMinutes = schedule.AfterUnlockMinutes > 0 ? schedule.AfterUnlockMinutes : null,
            MaxSnapshotMb = Whole(raw, "max_snapshot_mb", "max_snapshot_mb", 50, 1, 10_000),
            Notify = Flag("notify", true),
            Keep = new Keep(
                Whole(keep, "all_hours", "keep.all_hours", 48, 0, 24 * 366),
                Whole(keep, "daily_days", "keep.daily_days", 30, 0, 3660),
                Whole(keep, "weekly_weeks", "keep.weekly_weeks", 12, 0, 520)),
            Sources = sources,
            Staging = Text(restore, "staging", "restore.staging") is { } staging ? PathSetting(staging, "restore.staging") : PyPath.Pure(System.IO.Path.Combine(Local(env), "restore")),
            StopTasks = stopTasks,
            WarnIfOlder = Globs(restore, "warn_if_older", "restore.warn_if_older"),
        };
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (ignoring case).</summary>
    internal static bool Inside(string path, string folder)
    {
        var p = PyPath.Pure(path).TrimEnd('\\') + "\\";
        var f = PyPath.Pure(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
