using System.Text.Json.Nodes;
using WinBackground;

namespace RightHandBackup.Tests;

/// <summary>A task in memory: what autostart asked of it.</summary>
internal sealed class FakeTask : ITask
{
    public string Name => Cli.TaskName;
    public TaskSchedule? Schedule;
    public List<string> Args = [];

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => throw new NotSupportedException();

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
    {
        Schedule = schedule;
        Args = [.. args];
        return exe + " " + string.Join(" ", args);
    }

    public bool Disable() => true;
    public bool Stop() => true;
    public IReadOnlyDictionary<string, string>? Status() => null;
}

public sealed class BackupTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "right-hand-backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string live, other, dest;
    private DateTime now = new(2026, 11, 19, 10, 0, 0);
    private readonly List<string> notes = [];
    private readonly HashSet<string> running = [];
    private Action<int>? afterCopy;

    public BackupTests()
    {
        live = Path.Combine(dir, "live");
        other = Path.Combine(dir, "other");
        dest = Path.Combine(dir, "backup");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(dest);
        Put("state.json", "{\"a\":1}");
        Put("queue.json", "[]");
        Put("msal-cache.json", "secret");
        Put("old.json.bak-1", "x");
        Put("app.log", "log");
        Put("handlers/marketing/1.json", "{}");
        Put("m365-edge-profile/Default/Cookies", "cookie");
        Put("broadband/work.json", "{}");
        File.WriteAllText(Path.Combine(other, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(other, ".credentials.json"), "token");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private void Put(string relative, string text, DateTime? modified = null)
    {
        var path = Path.Combine(live, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (modified is { } m)
            File.SetLastWriteTimeUtc(path, m);
    }

    private string Settings(string extra = "") => $"""
        destination = '{dest}'
        machine = "PC1"
        log_file = '{dir}\rhb.log'
        state_file = '{dir}\state.json'
        max_snapshot_mb = 1

        [[source]]
        name = "ea-state"
        folder = '{live}'
        include = ["*.json", "handlers/**", "**/Cookies"]
        exclude = ["*.bak-*"]

        [[source]]
        name = "claude"
        folder = '{other}'
        include = ["*"]

        [restore]
        staging = '{dir}\staging'
        stop_tasks = ["tool watch"]
        warn_if_older = ["ea-state/state.json"]
        {extra}
        """;

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Env = name => name == "COMPUTERNAME" ? "PC1" : null, Now = () => now, Lock = false,
            Notify = (title, lines) => notes.Add(title + ": " + string.Join(" | ", lines)), Sleep = _ => { },
            TaskRunning = running.Contains, AfterCopy = afterCopy, Task = () => task,
        });
        return (code, output.ToString(), errors.ToString());
    }

    private readonly FakeTask task = new();

    private string Machine => Path.Combine(dest, "PC1");

    private string[] SnapshotNames() => Directory.GetDirectories(Machine).Select(Path.GetFileName).Where(n => !n!.EndsWith(".partial")).Order().ToArray()!;

    // ---- patterns and the refusal list ----

    [Theory]
    [InlineData("*.json", "state.json", true)]
    [InlineData("*.json", "a/state.json", false)]
    [InlineData("**/*.json", "a/b/state.json", true)]
    [InlineData("**/*.json", "state.json", true)]
    [InlineData("handlers/**", "handlers/x/y.json", true)]
    [InlineData("handlers/**", "handlers", false)]
    [InlineData("HANDLERS/**", "handlers/x", true)]
    [InlineData("ev/*.png", "ev/2026-10-07.png", true)]
    [InlineData("ev/*.png", "ev/sub/a.png", false)]
    [InlineData("usage.json*", "usage.json.123.tmp", true)]
    [InlineData("a?c", "abc", true)]
    public void Patterns_match_within_folders_and_across_them_with_two_stars(string pattern, string path, bool match) =>
        Assert.Equal(match, Glob.Parse(pattern).IsMatch(path));

    [Fact]
    public void A_walk_only_goes_into_folders_a_pattern_could_reach()
    {
        var memory = Glob.Parse("projects/C--x/memory/**");
        Assert.True(memory.CouldContain("projects"));
        Assert.True(memory.CouldContain("projects/C--x"));
        Assert.False(memory.CouldContain("projects/C--other"));
        Assert.False(Glob.Parse("*.json").CouldContain("broadband"));
        Assert.True(Glob.Parse("**/x").CouldContain("any/where"));
        Assert.Throws<FormatException>(() => Glob.Parse("../x"));
        Assert.Throws<FormatException>(() => Glob.Parse(@"C:\x"));
        Assert.Throws<FormatException>(() => Glob.Parse("/x"));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\claude-ea\m365-edge-profile\Default\Preferences", true)]
    [InlineData(@"C:\Users\me\AppData\Local\claude-ea\msal-cache.json", true)]
    [InlineData(@"C:\Users\me\.claude\.credentials.json", true)]
    [InlineData(@"x\Default\Cookies", true)]
    [InlineData(@"C:\Users\me\AppData\Local\claude-ea\org-directory-state.json", true)]
    [InlineData(@"C:\Users\me\.ssh\id_ed25519", true)]
    [InlineData(@"C:\Users\me\AppData\Local\claude-ea\pending.json", false)]
    [InlineData(@"C:\Users\me\.claude\settings.json", false)]
    public void Tokens_profiles_credentials_and_contact_details_are_always_refused(string path, bool refused) =>
        Assert.Equal(refused, Blocked.Why(path) is not null);

    // ---- run ----

    [Fact]
    public void A_run_copies_what_the_settings_name_with_a_manifest_and_never_a_blocked_file()
    {
        var (code, output, error) = Run(Settings(), "run");
        Assert.True(code == 0, error + output);
        var name = Assert.Single(SnapshotNames());
        Assert.Equal("2026-11-19-1000", name);
        var snap = Path.Combine(Machine, name);
        var manifest = Manifest.Load(snap);
        Assert.Equal(["claude/settings.json", "ea-state/handlers/marketing/1.json", "ea-state/queue.json", "ea-state/state.json"],
            manifest.Files.Select(f => f.Path).ToArray());
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(snap, "ea-state", "state.json")));
        var state = manifest.Files.Single(f => f.Path == "ea-state/state.json");
        Assert.Equal(Backup.Hash(File.ReadAllBytes(Path.Combine(live, "state.json"))), state.Sha256);
        Assert.Equal(7, state.Size);
        Assert.Contains(manifest.Blocked, b => b.Path == "ea-state/msal-cache.json");
        Assert.Contains(manifest.Blocked, b => b.Path == "ea-state/m365-edge-profile/");
        Assert.Contains(manifest.Blocked, b => b.Path == "claude/.credentials.json");
        Assert.DoesNotContain(manifest.Blocked, b => b.Path.Contains("bak"));   // excluded, not blocked
        var copied = Directory.GetFiles(snap, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("msal-cache.json", copied);
        Assert.DoesNotContain(".credentials.json", copied);
        Assert.DoesNotContain("Cookies", copied);
        Assert.DoesNotContain("old.json.bak-1", copied);
        var latest = JsonNode.Parse(File.ReadAllText(Path.Combine(Machine, "latest.json")))!;
        Assert.Equal("2026-11-19T10:00:00", (string)latest["checked"]!);
        Assert.Equal(name, (string)latest["snapshot"]!);
        Assert.Equal(0, Run(Settings(), "verify").Code);
    }

    [Fact]
    public void Nothing_is_written_when_nothing_changed_and_a_change_makes_a_new_snapshot()
    {
        Run(Settings(), "run");
        now = now.AddHours(2);
        var (_, output, _) = Run(Settings(), "run");
        Assert.Contains("No change since 2026-11-19-1000", output);
        Assert.Single(SnapshotNames());
        Assert.Equal("2026-11-19T12:00:00", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(Machine, "latest.json")))!["checked"]!);

        now = now.AddHours(2);
        Put("queue.json", "[1]");
        Run(Settings(), "run");
        Assert.Equal(["2026-11-19-1000", "2026-11-19-1400"], SnapshotNames());

        now = now.AddHours(2);
        File.Delete(Path.Combine(live, "queue.json"));   // a file gone is a change too
        Run(Settings(), "run");
        Assert.Equal(3, SnapshotNames().Length);
    }

    [Fact]
    public void A_dry_run_lists_and_writes_nothing()
    {
        var (code, output, _) = Run(Settings(), "run", "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("ea-state/state.json", output);
        Assert.Contains("blocked: ea-state/msal-cache.json (a sign-in token cache)", output);
        Assert.Contains("a run would write one", output);
        Assert.Empty(Directory.GetDirectories(dest));
    }

    [Fact]
    public void Too_much_stops_the_run_and_is_notified_at_once()
    {
        Put("handlers/big.bin", new string('x', 2 * 1024 * 1024));
        var (code, _, error) = Run(Settings(), "run");
        Assert.Equal(1, code);
        Assert.Contains("over max_snapshot_mb (1)", error);
        Assert.Contains("ea-state/handlers/big.bin", error);
        Assert.Single(notes);
        Assert.Empty(Directory.GetDirectories(dest));
    }

    [Fact]
    public void A_missing_destination_is_notified_on_the_third_failure_in_a_row_only()
    {
        var gone = Settings().Replace($"destination = '{dest}'", $"destination = '{dir}\\no\\such\\folder'");
        for (var i = 0; i < 4; i++)
            Assert.Equal(1, Run(gone, "run").Code);
        Assert.Single(notes);
        Assert.Equal(0, Run(Settings(), "run").Code);
        Assert.Null(Cli.LoadState(Path.Combine(dir, "state.json"))["failures"]);
    }

    [Fact]
    public void A_file_changing_during_the_pass_is_copied_again_so_the_snapshot_is_one_moment()
    {
        afterCopy = attempt =>
        {
            if (attempt == 1)
                Put("queue.json", "[\"changed\"]");
        };
        Run(Settings(), "run");
        var manifest = Manifest.Load(Path.Combine(Machine, SnapshotNames().Single()));
        Assert.Equal(2, manifest.Attempts);
        Assert.Empty(manifest.Unsettled);
        Assert.Equal("[\"changed\"]", File.ReadAllText(Path.Combine(Machine, SnapshotNames().Single(), "ea-state", "queue.json")));
    }

    [Fact]
    public void A_file_that_never_settles_is_taken_after_three_attempts_and_said_so()
    {
        afterCopy = attempt => Put("queue.json", $"[{attempt}]");
        var (_, output, _) = Run(Settings(), "run");
        var manifest = Manifest.Load(Path.Combine(Machine, SnapshotNames().Single()));
        Assert.Equal(3, manifest.Attempts);
        Assert.Equal(["ea-state/queue.json"], manifest.Unsettled);
        Assert.Contains("still changing after 3 attempts", output);
    }

    [Fact]
    public void A_locked_file_is_skipped_and_notified_after_three_runs()
    {
        using (new FileStream(Path.Combine(live, "queue.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i < 4; i++)
            {
                now = now.AddHours(2);
                Put("state.json", $"{{\"run\":{i}}}");
                Run(Settings(), "run");
            }
        }
        var manifest = Manifest.Load(Path.Combine(Machine, SnapshotNames().Last()));
        Assert.Contains(manifest.Skipped, s => s.Path == "ea-state/queue.json");
        Assert.Single(notes);
        Assert.Contains("ea-state/queue.json", notes[0]);
        now = now.AddHours(2);
        Run(Settings(), "run");   // readable again: forgotten
        Assert.Null(Cli.LoadState(Path.Combine(dir, "state.json"))["skips"]);
    }

    // ---- keeping ----

    [Fact]
    public void Keeping_is_everything_for_48_hours_then_one_a_day_then_one_a_week()
    {
        var at = new DateTime(2026, 11, 19, 12, 0, 0);
        var list = new List<SnapshotInfo>();
        void Add(DateTime t) => list.Add(new SnapshotInfo(t.ToString("yyyy-MM-dd-HHmm"), t, ""));
        Add(at.AddHours(-1));
        Add(at.AddHours(-3));
        Add(at.AddHours(-47));
        Add(at.AddDays(-3).AddHours(2));   // 16 Nov 14:00: the newest of its day
        Add(at.AddDays(-3));               // 16 Nov 12:00: dropped (the 14:00 is that day's)
        Add(at.AddDays(-3).AddHours(-1));  // 16 Nov 11:00: dropped
        Add(at.AddDays(-40));              // 10 Oct: the newest of its week
        Add(at.AddDays(-40).AddHours(-2)); // dropped
        Add(at.AddDays(-100));             // past 12 weeks: dropped
        var drop = Backup.ToDrop(list, new Keep(48, 30, 12), at).Select(s => s.Name).ToList();
        Assert.Equal(["2026-11-16-1200", "2026-11-16-1100", "2026-10-10-1000", "2026-08-11-1200"], drop);
        Assert.Empty(Backup.ToDrop([list[^1]], new Keep(0, 0, 0), at));   // the newest is always kept
    }

    [Fact]
    public void Pruning_removes_old_snapshots_and_nothing_it_did_not_write()
    {
        for (var i = 0; i < 3; i++)
        {
            Put("state.json", $"{{\"n\":{i}}}");
            Run(Settings(), "run");
            now = now.AddDays(1);
        }
        Directory.CreateDirectory(Path.Combine(Machine, "keep-me"));
        Directory.CreateDirectory(Path.Combine(Machine, "2020-01-01-0000"));   // no manifest: not ours
        now = now.AddDays(10);
        Put("state.json", "{\"n\":9}");
        Run(Settings("[keep]\nall_hours = 1\ndaily_days = 2\nweekly_weeks = 0"), "run");
        Assert.Equal(["2020-01-01-0000", "2026-12-02-1000", "keep-me"], SnapshotNames());
    }

    // ---- verify and restore ----

    [Fact]
    public void Verify_finds_a_changed_or_missing_file()
    {
        Run(Settings(), "run");
        var snap = Path.Combine(Machine, SnapshotNames().Single());
        File.WriteAllText(Path.Combine(snap, "ea-state", "state.json"), "{\"a\":2}");
        File.Delete(Path.Combine(snap, "ea-state", "queue.json"));
        var (code, output, _) = Run(Settings(), "verify");
        Assert.Equal(1, code);
        Assert.Contains("ea-state/state.json: its SHA-256 doesn't match", output);
        Assert.Contains("ea-state/queue.json: missing", output);
        Assert.Equal(1, Run(Settings(), "restore").Code);   // and a restore refuses it
    }

    [Fact]
    public void A_restore_goes_to_staging_and_compares_without_touching_live()
    {
        var t = new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
        Put("state.json", "{\"a\":1}", t);
        Put("queue.json", "[]", t);
        Run(Settings(), "run");
        Put("state.json", "{\"a\":2}", t.AddHours(1));   // live moved on: the snapshot's is older
        Put("queue.json", "[9]", t.AddHours(-1));        // live is older than the snapshot's
        File.Delete(Path.Combine(live, "handlers", "marketing", "1.json"));
        var (code, output, error) = Run(Settings(), "restore");
        Assert.True(code == 0, error + output);
        Assert.Matches(@"older\s+ea-state/state\.json .*\[!\]", output);
        Assert.Matches(@"newer\s+ea-state/queue\.json", output);
        Assert.Matches(@"new\s+ea-state/handlers/marketing/1\.json", output);
        Assert.Matches(@"same\s+claude/settings\.json", output);
        Assert.Contains("stops something being done twice", output);
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(live, "state.json")));   // live untouched
        var staged = Path.Combine(dir, "staging", "PC1-2026-11-19-1000");
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(staged, "ea-state", "state.json")));
        Assert.True(File.Exists(Path.Combine(staged, "comparison.txt")));
    }

    [Fact]
    public void Apply_needs_a_staging_restore_first_and_refuses_while_a_task_runs()
    {
        Run(Settings(), "run");
        Put("state.json", "{\"a\":2}");
        var (code, output, _) = Run(Settings(), "restore", "--apply");
        Assert.Equal(1, code);
        Assert.Contains("without --apply", output);

        Run(Settings(), "restore");
        running.Add("tool watch");
        (code, output, _) = Run(Settings(), "restore", "--apply");
        Assert.Equal(1, code);
        Assert.Contains("'tool watch' is running", output);
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(live, "state.json")));

        running.Clear();
        (code, output, _) = Run(Settings(), "restore", "--apply");
        Assert.Equal(0, code);
        Assert.Contains("Restored 1 file(s)", output);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(live, "state.json")));
        var replaced = Directory.GetDirectories(Path.Combine(dir, "staging", "PC1-2026-11-19-1000"), "replaced-*").Single();
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(replaced, "ea-state", "state.json")));
        Assert.Contains("Nothing to write", Run(Settings(), "restore", "--apply").Out);
    }

    [Fact]
    public void Only_restores_the_files_named_and_from_restores_another_machines_snapshot()
    {
        Run(Settings(), "run");
        Directory.Move(Machine, Path.Combine(dest, "OLDPC"));   // as if taken on the old PC
        Put("state.json", "{\"a\":2}");
        Put("queue.json", "[5]");
        Assert.Contains("Other machines: OLDPC", Run(Settings(), "list").Out);
        Run(Settings(), "restore", "--from", "OLDPC", "--only", "ea-state/queue.json");
        var (code, output, _) = Run(Settings(), "restore", "--from", "OLDPC", "--only", "ea-state/queue.json", "--apply");
        Assert.Equal(0, code);
        Assert.Contains("1 files:", output);
        Assert.Equal("[]", File.ReadAllText(Path.Combine(live, "queue.json")));
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(live, "state.json")));
    }

    [Fact]
    public void A_restore_never_writes_a_blocked_file_or_one_with_no_source()
    {
        Run(Settings(), "run");
        var snap = Path.Combine(Machine, SnapshotNames().Single());
        // a snapshot changed by hand to carry a token, its manifest to match
        var manifest = Manifest.Load(snap);
        File.WriteAllText(Path.Combine(snap, "ea-state", "msal-cache.json"), "secret2");
        Directory.CreateDirectory(Path.Combine(snap, "gone"));
        File.WriteAllText(Path.Combine(snap, "gone", "x.json"), "{}");
        manifest.Files.Add(new ManifestFile("ea-state/msal-cache.json", 7, Backup.Hash("secret2"u8.ToArray()), DateTime.UtcNow));
        manifest.Files.Add(new ManifestFile("gone/x.json", 2, Backup.Hash("{}"u8.ToArray()), DateTime.UtcNow));
        JsonFile.Write(Path.Combine(snap, Manifest.FileName), manifest.ToJson());
        Run(Settings(), "restore");
        var (_, output, _) = Run(Settings(), "restore", "--apply");
        Assert.Matches(@"refused\s+ea-state/msal-cache\.json\s+a sign-in token cache", output);
        Assert.Matches(@"no source\s+gone/x\.json", output);
        Assert.Equal("secret", File.ReadAllText(Path.Combine(live, "msal-cache.json")));
    }

    // ---- settings and autostart ----

    [Fact]
    public void Settings_are_read_strictly()
    {
        Assert.Contains("unknown setting 'colour'", Run("colour = 1\n" + Settings(), "run").Err);
        var overlap = Settings().Replace($"destination = '{dest}'", $"destination = '{live}\\backup'");
        Assert.Contains("overlap", Run(overlap, "run").Err);
        var unset = Settings().Replace($"destination = '{dest}'", "destination = '%NOT_SET_ANYWHERE%/x'");
        Assert.Contains("%NOT_SET_ANYWHERE%, which isn't set", Run(unset, "run").Err);
        Assert.Contains("needs include", Run(Settings().Replace("include = [\"*\"]", ""), "run").Err);
        Assert.Contains("[source] has an unknown setting 'includes'", Run(Settings().Replace("include = [\"*\"]", "includes = [\"*\"]"), "run").Err);
    }

    [Fact]
    public void A_base_file_gives_its_paths_as_variables()
    {
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), $"login_hint = \"x\"\n[paths]\nSTATE = '{live}'\n");
        var settings = "base = \"base-config.toml\"\n" + Settings().Replace($"folder = '{live}'", "folder = \"%STATE%\"");
        Assert.Equal(0, Run(settings, "run").Code);
        Assert.Contains(Manifest.Load(Path.Combine(Machine, SnapshotNames().Single())).Files, f => f.Path == "ea-state/state.json");
    }

    [Fact]
    public void Autostart_runs_every_few_hours_at_logon_and_after_unlocking()
    {
        var (code, output, _) = Run(Settings("").Replace("max_snapshot_mb = 1", "max_snapshot_mb = 1\nevery_minutes = 120\nafter_unlock_minutes = 5"), "autostart", "on");
        Assert.Equal(0, code);
        Assert.Equal(TimeSpan.FromMinutes(120), task.Schedule!.Every);
        Assert.Equal(TimeSpan.FromMinutes(5), task.Schedule.AfterUnlock);
        Assert.False(task.Schedule.KeepRunning);
        Assert.Equal("run", task.Args[^1]);
        Assert.Contains("every 120 minutes", output);
    }
}
