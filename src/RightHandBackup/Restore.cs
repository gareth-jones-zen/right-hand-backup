using System.Globalization;
using System.Text;

namespace RightHandBackup;

/// <summary>How a file in a snapshot compares with the live one.</summary>
public enum Status
{
    /// <summary>Not there live: restoring adds it.</summary>
    New,
    /// <summary>The same content: nothing to do.</summary>
    Same,
    /// <summary>Different, and the snapshot's copy was changed before the live one: restoring goes back in time.</summary>
    Older,
    /// <summary>Different, and the snapshot's copy is the later one.</summary>
    Newer,
    /// <summary>Different, changed at the same time.</summary>
    Differs,
    /// <summary>Its source isn't in the settings, so there's nowhere to put it.</summary>
    NoSource,
    /// <summary>A path that's never copied (see Blocked), or that would leave its folder.</summary>
    Refused,
}

/// <summary>One file of a restore: the snapshot's copy, where it goes, and how they compare.</summary>
public sealed record RestoreRow(ManifestFile File, Status Status, string? Live, DateTime? LiveModifiedUtc, bool Warn, string? Why)
{
    public bool ToWrite => Status is Status.New or Status.Older or Status.Newer or Status.Differs;
}

public static class Restore
{
    public const string ComparisonFile = "comparison.txt";
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string StagingFolder(Config config, string machine, string snapshot, string? to = null) =>
        Path.Combine(to ?? config.Staging, $"{machine}-{snapshot}");

    /// <summary>Each file in the snapshot (those matching <paramref name="only"/>, if any), compared with what's live
    /// where the current settings put that source.</summary>
    public static List<RestoreRow> Compare(Config config, Manifest manifest, IReadOnlyList<Glob> only)
    {
        var rows = new List<RestoreRow>();
        foreach (var f in manifest.Files)
        {
            if (only.Count > 0 && !only.Any(g => g.IsMatch(f.Path)))
                continue;
            if (!Backup.SafeRelative(f.Path))
            {
                rows.Add(new RestoreRow(f, Status.Refused, null, null, false, "not a safe path"));
                continue;
            }
            var cut = f.Path.IndexOf('/');
            var sourceName = f.Path[..cut];
            var relative = f.Path[(cut + 1)..];
            if (Blocked.Why(relative) is { } blocked)
            {
                rows.Add(new RestoreRow(f, Status.Refused, null, null, false, blocked));
                continue;
            }
            var source = config.Sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                rows.Add(new RestoreRow(f, Status.NoSource, null, null, false, $"no source named {sourceName} in the settings"));
                continue;
            }
            if (Blocked.Why(source.Folder) is { } blockedFolder)
            {
                rows.Add(new RestoreRow(f, Status.Refused, null, null, false, blockedFolder));
                continue;
            }
            var live = Path.Combine(source.Folder, relative.Replace('/', '\\'));
            var warn = config.WarnIfOlder.Any(g => g.IsMatch(f.Path));
            if (!File.Exists(live))
            {
                rows.Add(new RestoreRow(f, Status.New, live, null, false, null));
                continue;
            }
            var liveModified = File.GetLastWriteTimeUtc(live);
            string liveHash;
            try
            {
                liveHash = Backup.HashFile(live);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                rows.Add(new RestoreRow(f, Status.Differs, live, liveModified, warn, $"the live file couldn't be read ({error.Message.TrimEnd()})"));
                continue;
            }
            // the manifest keeps whole seconds
            var liveSeconds = new DateTime(liveModified.Ticks - liveModified.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var status = liveHash == f.Sha256 ? Status.Same
                : f.ModifiedUtc < liveSeconds ? Status.Older
                : f.ModifiedUtc > liveSeconds ? Status.Newer
                : Status.Differs;
            rows.Add(new RestoreRow(f, status, live, liveModified, warn && status is Status.Older or Status.Differs, null));
        }
        return rows;
    }

    /// <summary>The comparison, a line per file, then a count of each kind.</summary>
    public static string Table(IReadOnlyList<RestoreRow> rows, string heading)
    {
        var text = new StringBuilder();
        text.Append(heading).Append('\n');
        var width = rows.Count == 0 ? 10 : Math.Min(60, rows.Max(r => r.File.Path.Length));
        foreach (var r in rows)
        {
            var when = r.Status switch
            {
                Status.Same => "",
                Status.New => $"snapshot {Local(r.File.ModifiedUtc)}, not there live",
                Status.NoSource or Status.Refused => r.Why ?? "",
                _ => $"snapshot {Local(r.File.ModifiedUtc)}, live {Local(r.LiveModifiedUtc!.Value)}" + (r.Why is { } why ? $" ({why})" : ""),
            };
            var label = r.Status switch
            {
                Status.NoSource => "no source",
                Status.Refused => "refused",
                _ => r.Status.ToString().ToLowerInvariant(),
            };
            text.Append($"  {label,-9}  {r.File.Path.PadRight(width)}  {when}".TrimEnd()).Append(r.Warn ? "  [!]" : "").Append('\n');
        }
        var counts = Enum.GetValues<Status>().Select(s => (s, n: rows.Count(r => r.Status == s))).Where(x => x.n > 0)
            .Select(x => $"{x.n} {(x.s == Status.NoSource ? "no source" : x.s == Status.Refused ? "refused" : x.s.ToString().ToLowerInvariant())}");
        text.Append($"{rows.Count} files: {string.Join(", ", counts)}.\n");
        if (rows.Any(r => r.Warn))
            text.Append("[!] This file stops something being done twice (a booking, a forward), and the snapshot's copy is older than\n"
                        + "    the live one: restored, the tool could repeat what it did since. Before its task runs again, check what\n"
                        + "    it has done since the snapshot (its own status command, and any replies), and remove what it would redo.\n");
        return text.ToString();
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("dd/MM HH:mm", En);
}
