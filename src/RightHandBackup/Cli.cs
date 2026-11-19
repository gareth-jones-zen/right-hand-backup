using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using WinBackground;

namespace RightHandBackup;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Tests: called between a pass's copy and its check, with the attempt number.</summary>
    public Action<int>? AfterCopy { get; init; }
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(Cli.TaskName,
        "Backs up the tools' state when it has changed (right-hand-backup run).");

    /// <summary>Whether a scheduled task is running now (restore --apply refuses while one in stop_tasks is).</summary>
    public Func<string, bool> TaskRunning { get; init; } = name =>
        new ScheduledTask(name).Status() is { } info && info.GetValueOrDefault("state") == "Running";

    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public bool Lock { get; init; } = true;
}

/// <summary>right-hand-backup: snapshots of the small files tools keep outside a repo (state, queues, records of what
/// they've done), when they've changed, and a restore that shows what it would change first. Exit codes: 0 done; 1
/// failed, refused, or a snapshot that doesn't match its manifest; 2 settings or arguments.</summary>
public static class Cli
{
    public const string Prog = "right-hand-backup";
    public const string TaskName = "right-hand-backup run";
    public const string BackgroundExe = "right-hand-backup-background.exe";

    private const string Usage = "usage: right-hand-backup [-h] [--version] [--config CONFIG] [--destination DIR] {run,list,verify,restore,autostart,config} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "right-hand-backup: snapshots of the small files your tools keep outside a repo (state, queues, records of what\n" +
        "they've done), written only when something changed, and a restore that shows what it would change first.\n\n" +
        "    right-hand-backup run [--dry-run]           take a snapshot if anything changed (--dry-run: list, write nothing)\n" +
        "    right-hand-backup list [--from MACHINE]     the snapshots kept\n" +
        "    right-hand-backup verify [SNAPSHOT]         re-hash a snapshot against its manifest (default: the newest)\n" +
        "    right-hand-backup restore [SNAPSHOT]        copy a snapshot to a staging folder and compare it with live\n" +
        "        --from MACHINE   another machine's snapshots (a new PC)\n" +
        "        --only PATTERN   only these files (\"source/path\" patterns; repeatable)\n" +
        "        --to DIR         another staging folder\n" +
        "        --apply          then write it over live state (after a staging restore; refused while a stop_tasks task runs)\n" +
        "    right-hand-backup autostart on              run every few hours, at logon and after unlocking (off, restart, status)\n" +
        "    right-hand-backup config                    create the settings file (if missing) and say where it is\n\n" +
        "options:\n" +
        "  -h, --help         show this help message and exit\n" +
        "  --version          show program's version number and exit\n" +
        "  --config CONFIG    settings file (default %APPDATA%\\right-hand-backup\\config.toml)\n" +
        "  --destination DIR  snapshots folder, instead of the settings' destination (to try it out)\n";

    private static readonly string[] Commands = ["run", "list", "verify", "restore", "autostart", "config"];
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string? Destination;
        public string Command = "";
        public List<string> Args = [];
        public bool DryRun;
        public bool Apply;
        public string? From;
        public string? To;
        public List<string> Only = [];
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    /// <summary>A run that stopped itself rather than write something that looks wrong (it won't clear by itself, so
    /// it's notified at once).</summary>
    public sealed class SafetyStop(string message) : Exception(message);

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        Config? config = null;
        var isRun = options.Command == "run" && !options.DryRun;
        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(RightHandBackup.Config.CreateDefault(options.Config ?? host.Env(RightHandBackup.Config.EnvConfig), host.Env));
                return 0;
            }
            config = RightHandBackup.Config.Load(options.Config, host.Env, options.Destination);
            switch (options.Command)
            {
                case "autostart":
                    return Autostart(config, options.Args[0], host);
                case "list":
                    return List(config, host, options.From);
                case "verify":
                    return VerifyCommand(config, host, options.Args.FirstOrDefault(), options.From);
            }
            using var held = host.Lock && (isRun || options.Apply) ? Locks.Exclusive("right-hand-backup-" + Locks.Key(config.Path), 300) : null;
            if (options.Command == "restore")
                return RestoreCommand(config, host, options);
            var code = TakeSnapshot(config, host, options.DryRun);
            if (isRun)
                ForgetFailure(config);
            return code;
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            if (isRun && config is not null)
                Failed(config, host, error);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine(error is SafetyStop ? error.Message : $"{error.GetType().Name}: {error.Message}");
            if (isRun && config is not null)
                Failed(config, host, error);
            return 1;
        }
    }

    // ---- run ----

    private static int TakeSnapshot(Config config, CliHost host, bool dryRun)
    {
        var log = dryRun ? (_ => { }) : Logger.Create(config.LogFile, null, clock: host.Now);
        var now = host.Now();
        var machineFolder = config.MachineFolder();
        var parent = Path.GetDirectoryName(config.Destination);
        if (!Directory.Exists(config.Destination) && (parent is null || !Directory.Exists(parent)))
            throw new InvalidOperationException($"The destination's folder, {parent}, doesn't exist, so nothing was written "
                                                + "(is the synced folder set up on this PC?).");

        var scan = RightHandBackup.Backup.Scan(config);
        foreach (var note in scan.Notes)
        {
            host.Out.WriteLine(note);
            log(note);
        }
        if (dryRun)
        {
            foreach (var f in scan.Files)
                host.Out.WriteLine($"  {f.Path}  {RightHandBackup.Backup.Size(f.Size)}");
            foreach (var b in scan.Blocked)
                host.Out.WriteLine($"  blocked: {b.Path} ({b.Why})");
            host.Out.WriteLine($"{scan.Files.Count} files, {RightHandBackup.Backup.Size(scan.Bytes)}; {scan.Blocked.Count} blocked.");
        }
        var limit = (long)config.MaxSnapshotMb * 1024 * 1024;
        if (scan.Bytes > limit)
        {
            var biggest = scan.Files.OrderByDescending(f => f.Size).Take(5).Select(f => $"{f.Path} ({RightHandBackup.Backup.Size(f.Size)})");
            throw new SafetyStop($"The files to back up come to {RightHandBackup.Backup.Size(scan.Bytes)}, over max_snapshot_mb ({config.MaxSnapshotMb}), "
                                 + $"so nothing was written: something big has been included. Largest: {string.Join(", ", biggest)}.");
        }

        var pass = RightHandBackup.Backup.Pass(scan.Files, host.Sleep, afterCopy: host.AfterCopy);
        var latest = RightHandBackup.Backup.Latest(machineFolder);
        Manifest? last = null;
        if (latest is not null)
        {
            try
            {
                last = Manifest.Load(latest.Folder);
            }
            catch (Exception error) when (error is FormatException or IOException)
            {
                log($"the last snapshot's manifest ({latest.Name}) couldn't be read, so a new snapshot is taken: {error.Message}");
            }
        }
        var same = RightHandBackup.Backup.SameAs(pass, last);
        foreach (var s in pass.Skipped)
        {
            host.Out.WriteLine($"skipped {s.Path}: {s.Why}");
            log($"skipped {s.Path}: {s.Why}");
        }
        if (pass.Unsettled.Count > 0)
        {
            var text = $"still changing after {pass.Attempts} attempts, so taken as they were: {string.Join(", ", pass.Unsettled)}";
            host.Out.WriteLine(text);
            log(text);
        }

        if (dryRun)
        {
            host.Out.WriteLine(same
                ? $"No change since {latest!.Name}: a run would write nothing."
                : latest is null ? $"No snapshot yet: a run would write one to {machineFolder}." : $"Changed since {latest.Name}: a run would write a snapshot.");
            var drop = RightHandBackup.Backup.ToDrop(RightHandBackup.Backup.Snapshots(machineFolder), config.Keep, now);
            if (drop.Count > 0)
                host.Out.WriteLine($"It would remove {drop.Count} old snapshot(s): {string.Join(", ", drop.Select(d => d.Name))}.");
            return 0;
        }

        var bytes = pass.Files.Sum(f => f.Data.LongLength);
        if (!same)
        {
            var name = RightHandBackup.Backup.Write(machineFolder, pass, scan, config, now, Version);
            latest = new SnapshotInfo(name, now, Path.Combine(machineFolder, name));
            last = Manifest.Load(latest.Folder);
            var line = $"snapshot {name}: {pass.Files.Count} files, {RightHandBackup.Backup.Size(bytes)}"
                       + (scan.Blocked.Count > 0 ? $", {scan.Blocked.Count} blocked" : "") + (pass.Attempts > 1 ? $", {pass.Attempts} attempts" : "");
            host.Out.WriteLine($"Snapshot {name}: {pass.Files.Count} files, {RightHandBackup.Backup.Size(bytes)}, in {machineFolder}.");
            log(line);
        }
        else
        {
            host.Out.WriteLine($"No change since {latest!.Name} ({pass.Files.Count} files): nothing written.");
            log($"no change since {latest.Name} ({pass.Files.Count} files)");
        }
        RightHandBackup.Backup.WriteLatest(machineFolder, now, latest, last, !same, pass.Skipped.Count, Version);
        foreach (var gone in RightHandBackup.Backup.Prune(machineFolder, config.Keep, now, dryRun: false))
            log($"removed old snapshot {gone}");
        Skips(config, host, pass.Skipped);
        return 0;
    }

    /// <summary>A file skipped (locked) three runs in a row is notified, once, until it's copied again.</summary>
    private static void Skips(Config config, CliHost host, IReadOnlyList<Note> skipped)
    {
        var state = LoadState(config.StateFile);
        var counts = state["skips"] as JsonObject ?? [];
        var notified = (state["skip_notified"] as JsonArray)?.Select(n => (string)n!).ToHashSet(StringComparer.Ordinal) ?? [];
        var now = skipped.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        var next = new JsonObject();
        foreach (var path in now)
            next[path] = ((int?)counts[path] ?? 0) + 1;
        var fresh = now.Where(p => (int)next[p]! >= 3 && !notified.Contains(p)).ToList();
        if (fresh.Count > 0 && config.Notify)
            host.Notify("right-hand-backup couldn't copy a file", [$"{string.Join(", ", fresh)}: skipped 3 runs in a row (locked).", $"Log: {config.LogFile}"]);
        notified.IntersectWith(now);
        notified.UnionWith(fresh);
        var changed = next.Count > 0 || counts.Count > 0 || state["skip_notified"] is not null;
        state["skips"] = next;
        state["skip_notified"] = new JsonArray(notified.Order(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p)).ToArray());
        if (next.Count == 0 && notified.Count == 0)
        {
            state.Remove("skips");
            state.Remove("skip_notified");
        }
        if (changed)
            SaveState(config.StateFile, state);
    }

    // ---- list and verify ----

    private static int List(Config config, CliHost host, string? from)
    {
        var machine = from ?? config.Machine;
        var folder = config.MachineFolder(machine);
        var all = RightHandBackup.Backup.Snapshots(folder);
        var latest = RightHandBackup.Backup.Latest(folder);
        if (all.Count == 0)
            host.Out.WriteLine($"No snapshots for {machine} in {folder}.");
        else
        {
            host.Out.WriteLine($"Snapshots for {machine}, in {folder}:");
            foreach (var s in all)
            {
                string detail;
                try
                {
                    var m = Manifest.Load(s.Folder);
                    detail = $"{m.Files.Count,4} files  {RightHandBackup.Backup.Size(m.Bytes),9}"
                             + (m.Skipped.Count > 0 ? $"  {m.Skipped.Count} skipped" : "") + (m.Unsettled.Count > 0 ? $"  {m.Unsettled.Count} unsettled" : "");
                }
                catch (Exception error) when (error is FormatException or IOException)
                {
                    detail = "manifest can't be read";
                }
                host.Out.WriteLine($"  {s.Name,-17}  {detail}{(s.Name == latest?.Name ? "  (latest)" : "")}");
            }
            var checkedAt = (string?)JsonFile.Read(Path.Combine(folder, RightHandBackup.Backup.LatestFile))["checked"];
            if (checkedAt is not null)
                host.Out.WriteLine($"Last checked {checkedAt.Replace('T', ' ')}.");
        }
        if (Directory.Exists(config.Destination))
        {
            var others = Directory.GetDirectories(config.Destination).Select(Path.GetFileName)
                .Where(n => !string.Equals(n, machine, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (others.Count > 0)
                host.Out.WriteLine($"Other machines: {string.Join(", ", others)} (--from MACHINE).");
        }
        return 0;
    }

    private static SnapshotInfo Find(Config config, string? name, string? from)
    {
        var machine = from ?? config.Machine;
        var folder = config.MachineFolder(machine);
        if (name is null)
            return RightHandBackup.Backup.Latest(folder) ?? throw new UsageException($"No snapshots for {machine} in {folder}.");
        return RightHandBackup.Backup.Snapshots(folder).FirstOrDefault(s => s.Name == name)
               ?? throw new UsageException($"No snapshot {name} for {machine} (see `right-hand-backup list{(from is null ? "" : " --from " + from)}`).");
    }

    private static int VerifyCommand(Config config, CliHost host, string? name, string? from)
    {
        var snapshot = Find(config, name, from);
        var manifest = Manifest.Load(snapshot.Folder);
        var problems = RightHandBackup.Backup.Verify(snapshot.Folder, manifest);
        if (problems.Count == 0)
        {
            host.Out.WriteLine($"{snapshot.Name}: all {manifest.Files.Count} files match the manifest.");
            return 0;
        }
        foreach (var p in problems)
            host.Out.WriteLine($"  {p}");
        host.Out.WriteLine($"{snapshot.Name}: {problems.Count} problem(s).");
        return 1;
    }

    // ---- restore ----

    private static int RestoreCommand(Config config, CliHost host, Options options)
    {
        var machine = options.From ?? config.Machine;
        var snapshot = Find(config, options.Args.FirstOrDefault(), options.From);
        var manifest = Manifest.Load(snapshot.Folder);
        var problems = RightHandBackup.Backup.Verify(snapshot.Folder, manifest);
        if (problems.Count > 0)
        {
            foreach (var p in problems)
                host.Out.WriteLine($"  {p}");
            host.Out.WriteLine($"Not restored: {snapshot.Name} doesn't match its manifest. `right-hand-backup list` shows the others.");
            return 1;
        }
        var only = options.Only.Select(Glob.Parse).ToList();
        var rows = Restore.Compare(config, manifest, only);
        var table = Restore.Table(rows, $"Snapshot {snapshot.Name} from {machine} (taken {manifest.Created:dd/MM/yyyy HH:mm}), compared with live:");
        var staging = Restore.StagingFolder(config, machine, snapshot.Name, options.To);
        var toWrite = rows.Where(r => r.ToWrite).ToList();
        var from = options.From is null ? "" : $" --from {options.From}";

        if (!options.Apply)
        {
            Directory.CreateDirectory(staging);
            foreach (var r in rows.Where(r => r.Status != Status.Refused))
            {
                var target = Path.Combine(staging, r.File.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(snapshot.Folder, r.File.Path.Replace('/', '\\')), target, true);
                File.SetLastWriteTimeUtc(target, r.File.ModifiedUtc);
            }
            File.WriteAllText(Path.Combine(staging, Restore.ComparisonFile), table.Replace("\n", "\r\n"));
            host.Out.Write(table);
            host.Out.WriteLine($"Copied to {staging}; nothing live was changed.");
            if (toWrite.Count > 0)
            {
                var stop = config.StopTasks.Count > 0 ? $"stop these tasks ({string.Join(", ", config.StopTasks)}), then " : "";
                host.Out.WriteLine($"To write the {toWrite.Count} new and changed file(s) over live state: {stop}"
                                   + $"`right-hand-backup restore {snapshot.Name}{from}{string.Concat(options.Only.Select(o => $" --only \"{o}\""))} --apply`.");
            }
            else
                host.Out.WriteLine("Live state already matches: there's nothing to restore.");
            return 0;
        }

        if (!File.Exists(Path.Combine(staging, Restore.ComparisonFile)))
        {
            host.Out.WriteLine($"Not restored: first run `right-hand-backup restore {snapshot.Name}{from}` (without --apply), "
                               + "and read what it would change.");
            return 1;
        }
        host.Out.Write(table);
        if (toWrite.Count == 0)
        {
            host.Out.WriteLine("Nothing to write: live state already matches.");
            return 0;
        }
        var running = config.StopTasks.Where(host.TaskRunning).ToList();
        if (running.Count > 0)
        {
            host.Out.WriteLine($"Not restored: {string.Join(", ", running.Select(r => $"'{r}'"))} {(running.Count == 1 ? "is" : "are")} running, "
                               + "and would work on the state as it's replaced. Stop them (End in Task Scheduler, or the tool's "
                               + "`autostart off`), run this again, then start them once you've checked.");
            return 1;
        }

        var log = Logger.Create(config.LogFile, null, clock: host.Now);
        var replaced = Path.Combine(staging, "replaced-" + host.Now().ToString("yyyyMMdd-HHmmss", En));
        var written = 0;
        foreach (var r in toWrite)
        {
            var data = File.ReadAllBytes(Path.Combine(snapshot.Folder, r.File.Path.Replace('/', '\\')));
            if (RightHandBackup.Backup.Hash(data) != r.File.Sha256)
                throw new SafetyStop($"{r.File.Path} changed in the snapshot since it was checked; stopped after {written} file(s).");
            if (File.Exists(r.Live))
            {
                var keep = Path.Combine(replaced, r.File.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                File.Copy(r.Live!, keep, true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(r.Live)!);
            var temp = r.Live + ".right-hand-backup.tmp";
            File.WriteAllBytes(temp, data);
            File.SetLastWriteTimeUtc(temp, r.File.ModifiedUtc);
            File.Move(temp, r.Live!, true);
            written++;
            log($"restored {r.File.Path} from {machine} {snapshot.Name} ({r.Status.ToString().ToLowerInvariant()})");
        }
        host.Out.WriteLine($"Restored {written} file(s) from {snapshot.Name}."
                           + (Directory.Exists(replaced) ? $" The live files they replaced are in {replaced}." : ""));
        if (config.StopTasks.Count > 0)
            host.Out.WriteLine(rows.Any(r => r.Warn && r.ToWrite)
                ? "Check the [!] files before starting the stopped tasks again."
                : "Start the stopped tasks again.");
        return 0;
    }

    // ---- failures ----

    /// <summary>A run that failed: logged, and notified once until a run succeeds again. On the third failure in a
    /// row (a run on waking can find the synced folder not there yet), or at once for a safety stop.</summary>
    private static void Failed(Config config, CliHost host, Exception error)
    {
        try
        {
            Logger.Create(config.LogFile, null, clock: host.Now)($"run failed: {error.GetType().Name}: {error.Message}");
            var state = LoadState(config.StateFile);
            var failures = (state["failures"]?.GetValue<int>() ?? 0) + 1;
            state["failures"] = failures;
            if (state["notified"] is null && (failures >= 3 || error is SafetyStop))
            {
                if (config.Notify)
                    host.Notify("right-hand-backup couldn't back up", [error.Message, $"Log: {config.LogFile}"]);
                state["notified"] = host.Now().ToString("yyyy-MM-ddTHH:mm:ss", En);
            }
            SaveState(config.StateFile, state);
        }
        catch (Exception)
        {
            // nothing more can be done: the exit code says it failed
        }
    }

    private static void ForgetFailure(Config config)
    {
        var state = LoadState(config.StateFile);
        if (state.Remove("failures") | state.Remove("notified"))
            SaveState(config.StateFile, state);
    }

    internal static JsonObject LoadState(string path) => JsonFile.Read(path);

    internal static void SaveState(string path, JsonObject state) => JsonFile.Write(path, state);

    // ---- autostart ----

    private static int Autostart(Config config, string action, CliHost host)
    {
        var task = host.Task();
        switch (action)
        {
            case "on":
                var background = Path.Combine(host.ProgramDir, BackgroundExe);
                var exe = File.Exists(background) ? background : Path.Combine(host.ProgramDir, "right-hand-backup.exe");
                var schedule = new TaskSchedule
                {
                    AtLogon = true,
                    Every = TimeSpan.FromMinutes(config.EveryMinutes),
                    AfterUnlock = config.AfterUnlockMinutes is { } u ? TimeSpan.FromMinutes(u) : null,
                    KeepRunning = false,
                    TimeLimit = TimeSpan.FromMinutes(10),
                };
                var line = task.Enable(exe, ["--config", config.Path, "run"], Path.GetDirectoryName(config.Path), schedule);
                host.Out.WriteLine($"Autostart is on: '{TaskName}' runs every {config.EveryMinutes} minutes, at logon" +
                                   (config.AfterUnlockMinutes is { } m ? $" and {m} minute(s) after each unlock" : "") +
                                   $", and has run now.\n  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            case "restart":
                try
                {
                    host.Out.WriteLine(task.Restart() ? $"Started '{TaskName}' now." : "Autostart is off, so there's nothing to restart.");
                }
                catch (InvalidOperationException error)
                {
                    host.Err.WriteLine(error.Message);
                    return 1;
                }
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                    host.Out.WriteLine("Autostart is off. `right-hand-backup autostart on` turns it on.");
                else
                {
                    host.Out.WriteLine($"Autostart is on ('{TaskName}').");
                    foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        host.Out.WriteLine($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
                }
                return 0;
        }
    }

    // ---- arguments ----

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        string Value(string name)
        {
            if (++i >= argv.Count)
                throw new ArgumentsException($"argument {name}: expected one argument");
            return argv[i];
        }
        bool Shared(string arg)
        {
            if (arg == "--destination")
                options.Destination = Value("--destination");
            else if (arg.StartsWith("--destination=", StringComparison.Ordinal))
                options.Destination = arg["--destination=".Length..];
            else
                return false;
            return true;
        }
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
                options.Config = Value("--config");
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else if (!Shared(arg))
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            switch (options.Command, arg)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case ("run", "--dry-run"):
                    options.DryRun = true;
                    break;
                case ("list" or "verify" or "restore", "--from"):
                    options.From = Value("--from");
                    break;
                case ("restore", "--to"):
                    options.To = Value("--to");
                    break;
                case ("restore", "--only"):
                    options.Only.Add(Value("--only"));
                    break;
                case ("restore", "--apply"):
                    options.Apply = true;
                    break;
                default:
                    if (Shared(arg))
                        break;
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    options.Args.Add(arg);
                    break;
            }
        }
        var max = options.Command is "autostart" or "verify" or "restore" ? 1 : 0;
        if (options.Command == "autostart" && options.Args.Count == 0)
            throw new ArgumentsException("the following arguments are required: action");
        if (options.Args.Count > max)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.Args.Skip(max))}");
        if (options.Command == "autostart" && options.Args[0] is not ("on" or "off" or "restart" or "status"))
            throw new ArgumentsException($"argument action: invalid choice: '{options.Args[0]}' (choose from 'on', 'off', 'restart', 'status')");
        foreach (var o in options.Only)
        {
            try
            {
                Glob.Parse(o);
            }
            catch (FormatException error)
            {
                throw new ArgumentsException($"argument --only: {error.Message}");
            }
        }
        if (options.From is not null && (options.From.Contains('/') || options.From.Contains('\\') || options.From.Contains("..")))
            throw new ArgumentsException("argument --from: a machine name, not a path");
        return options;
    }
}
