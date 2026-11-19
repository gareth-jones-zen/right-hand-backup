using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RightHandBackup;

/// <summary>A file a run would copy: its source, its path in that source ("/" between folders), where it is, and its
/// size when the folders were walked.</summary>
public sealed record Candidate(string Source, string Relative, string FullPath, long Size)
{
    public string Path => Source + "/" + Relative;
}

/// <summary>What a walk of the sources found: the files to copy, the ones refused, and notes (a missing folder).</summary>
public sealed record ScanResult(List<Candidate> Files, List<Note> Blocked, List<string> Notes)
{
    public long Bytes => Files.Sum(f => f.Size);
}

/// <summary>A file as it was copied: its content, hash and time.</summary>
public sealed record Copied(Candidate File, byte[] Data, string Sha256, DateTime ModifiedUtc);

/// <summary>A quiet pass: every file as copied, those skipped (locked), and those still changing after the last
/// attempt.</summary>
public sealed record PassResult(List<Copied> Files, List<Note> Skipped, List<string> Unsettled, int Attempts);

/// <summary>A snapshot folder: its name and when it was taken (local time, from the name).</summary>
public sealed record SnapshotInfo(string Name, DateTime Time, string Folder);

public static partial class Backup
{
    public const string LatestFile = "latest.json";
    public const string PartialSuffix = ".partial";
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}-\d{4})(-\d+)?$")]
    private static partial Regex SnapshotName();

    // ---- walking the sources ----

    public static ScanResult Scan(Config config)
    {
        var files = new List<Candidate>();
        var blocked = new List<Note>();
        var notes = new List<string>();
        foreach (var source in config.Sources)
        {
            if (Blocked.Why(source.Folder) is { } whyFolder)
            {
                blocked.Add(new Note(source.Name + "/", whyFolder));
                continue;
            }
            if (!Directory.Exists(source.Folder))
            {
                notes.Add($"source {source.Name}: {source.Folder} doesn't exist, so nothing from it");
                continue;
            }
            var before = files.Count + blocked.Count;
            Walk(source, source.Folder, "", files, blocked, notes);
            if (files.Count + blocked.Count == before)
                notes.Add($"source {source.Name}: nothing matches its include patterns");
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        blocked.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ScanResult(files, blocked, notes);
    }

    private static void Walk(Source source, string folder, string relative, List<Candidate> files, List<Note> blocked, List<string> notes)
    {
        string[] names, folders;
        try
        {
            names = Directory.GetFiles(folder);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            notes.Add($"source {source.Name}: couldn't list {(relative.Length == 0 ? "its folder" : relative)} ({error.Message.TrimEnd()})");
            return;
        }
        foreach (var full in names)
        {
            var rel = Join(relative, System.IO.Path.GetFileName(full));
            if (!source.Include.Any(g => g.IsMatch(rel)) || source.Exclude.Any(g => g.IsMatch(rel)))
                continue;
            if (Blocked.Why(rel) is { } why)
            {
                blocked.Add(new Note(source.Name + "/" + rel, why));
                continue;
            }
            long size;
            try
            {
                var info = new FileInfo(full);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // a link: what it points to may be anywhere
                size = info.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;   // gone since the listing
            }
            files.Add(new Candidate(source.Name, rel, full, size));
        }
        foreach (var sub in folders)
        {
            var rel = Join(relative, System.IO.Path.GetFileName(sub));
            if (!source.Include.Any(g => g.CouldContain(rel)))
                continue;
            if (Blocked.Why(rel) is { } why)
            {
                blocked.Add(new Note(source.Name + "/" + rel + "/", why));
                continue;
            }
            try
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    continue;   // a link or junction: could loop, or lead out of the source
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            Walk(source, sub, rel, files, blocked, notes);
        }
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + "/" + name;

    // ---- the quiet pass ----

    /// <summary>Copy every file (into memory), then hash each again: if any changed meanwhile (a queue and the record
    /// that must match it), try the whole pass again, up to <paramref name="attempts"/> times, so the snapshot is one
    /// moment. After the last attempt, take it as it is and list what was still changing. A file another program
    /// holds without sharing is skipped and listed; one deleted meanwhile is left out.</summary>
    public static PassResult Pass(IReadOnlyList<Candidate> files, Action<TimeSpan> sleep, int attempts = 3, Action<int>? afterCopy = null)
    {
        PassResult? result = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var copied = new List<Copied>();
            var skipped = new List<Note>();
            foreach (var file in files)
            {
                try
                {
                    var modified = File.GetLastWriteTimeUtc(file.FullPath);
                    var data = ReadShared(file.FullPath);
                    copied.Add(new Copied(file, data, Hash(data), modified));
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (IOException error) when (Locked(error))
                {
                    skipped.Add(new Note(file.Path, "locked by another program"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new Note(file.Path, error.Message.TrimEnd()));
                }
            }
            afterCopy?.Invoke(attempt);   // tests: change a file between the copy and the check
            var unsettled = new List<string>();
            foreach (var c in copied)
            {
                try
                {
                    if (Hash(ReadShared(c.File.FullPath)) != c.Sha256)
                        unsettled.Add(c.File.Path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    unsettled.Add(c.File.Path);
                }
            }
            result = new PassResult(copied, skipped, unsettled, attempt);
            if (unsettled.Count == 0)
                break;
            if (attempt < attempts)
                sleep(TimeSpan.FromSeconds(2 * attempt));
        }
        return result!;
    }

    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string HashFile(string path) => Hash(ReadShared(path));

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).</summary>
    private static bool Locked(IOException error) => (error.HResult & 0xFFFF) is 32 or 33;

    // ---- snapshots ----

    public static List<SnapshotInfo> Snapshots(string machineFolder)
    {
        var list = new List<SnapshotInfo>();
        if (!Directory.Exists(machineFolder))
            return list;
        foreach (var folder in Directory.GetDirectories(machineFolder))
        {
            var name = System.IO.Path.GetFileName(folder);
            var m = SnapshotName().Match(name);
            if (!m.Success || !File.Exists(System.IO.Path.Combine(folder, Manifest.FileName)))
                continue;
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd-HHmm", En, DateTimeStyles.None, out var time))
                list.Add(new SnapshotInfo(name, time, folder));
        }
        return list.OrderByDescending(s => s.Time).ThenByDescending(s => s.Name.Length).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The newest complete snapshot (by latest.json, else by name), or null.</summary>
    public static SnapshotInfo? Latest(string machineFolder)
    {
        var all = Snapshots(machineFolder);
        var named = (string?)JsonFile.Read(System.IO.Path.Combine(machineFolder, LatestFile))["snapshot"];
        return all.FirstOrDefault(s => s.Name == named) ?? all.FirstOrDefault();
    }

    /// <summary>Whether a pass found the same files, with the same content, as the last snapshot. Files skipped this
    /// time don't count either way.</summary>
    public static bool SameAs(PassResult pass, Manifest? last)
    {
        if (last is null)
            return false;
        var skipped = pass.Skipped.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        var now = pass.Files.ToDictionary(f => f.File.Path, f => f.Sha256, StringComparer.Ordinal);
        var then = last.Files.Where(f => !skipped.Contains(f.Path)).ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        return now.Count == then.Count && now.All(kv => then.TryGetValue(kv.Key, out var h) && h == kv.Value);
    }

    /// <summary>Write a snapshot: into a ".partial" folder, then renamed, so a half-written one never looks
    /// complete. Returns its name.</summary>
    public static string Write(string machineFolder, PassResult pass, ScanResult scan, Config config, DateTime now, string version)
    {
        Directory.CreateDirectory(machineFolder);
        var stem = now.ToString("yyyy-MM-dd-HHmm", En);
        var name = stem;
        for (var n = 2; Directory.Exists(System.IO.Path.Combine(machineFolder, name)); n++)
            name = $"{stem}-{n}";
        var final = System.IO.Path.Combine(machineFolder, name);
        var partial = final + PartialSuffix;
        if (Directory.Exists(partial))
            Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        foreach (var c in pass.Files)
        {
            var target = System.IO.Path.Combine(partial, c.File.Source, c.File.Relative.Replace('/', '\\'));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, c.Data);
            File.SetLastWriteTimeUtc(target, c.ModifiedUtc);
        }
        var manifest = new Manifest
        {
            Machine = config.Machine,
            Created = now,
            Version = version,
            Attempts = pass.Attempts,
            Sources = config.Sources.Select(s => new Manifest.SourceInfo(s.Name, s.Folder)).ToList(),
            Files = pass.Files.Select(c => new ManifestFile(c.File.Path, c.Data.LongLength, c.Sha256, c.ModifiedUtc)).ToList(),
            Skipped = pass.Skipped,
            Blocked = scan.Blocked,
            Unsettled = pass.Unsettled,
        };
        JsonFile.Write(System.IO.Path.Combine(partial, Manifest.FileName), manifest.ToJson());
        Directory.Move(partial, final);
        return name;
    }

    /// <summary>latest.json: when a run last checked (snapshot or not; what a watchdog reads) and the newest
    /// snapshot.</summary>
    public static void WriteLatest(string machineFolder, DateTime now, SnapshotInfo? latest, Manifest? manifest, bool changed, int skipped, string version)
    {
        var o = new JsonObject
        {
            ["checked"] = now.ToString("yyyy-MM-ddTHH:mm:ss", En),
            ["changed"] = changed,
            ["snapshot"] = latest?.Name,
            ["created"] = manifest?.Created.ToString("yyyy-MM-ddTHH:mm:ss", En),
            ["files"] = manifest?.Files.Count ?? 0,
            ["bytes"] = manifest?.Bytes ?? 0,
            ["skipped"] = skipped,
            ["version"] = version,
        };
        JsonFile.Write(System.IO.Path.Combine(machineFolder, LatestFile), o);
    }

    // ---- keeping ----

    /// <summary>Which snapshots the keep rules drop: everything from the last AllHours is kept, then the newest of
    /// each day for DailyDays, then the newest of each week (ISO) for WeeklyWeeks; the newest snapshot always.</summary>
    public static List<SnapshotInfo> ToDrop(IReadOnlyList<SnapshotInfo> snapshots, Keep keep, DateTime now)
    {
        var newestFirst = snapshots.OrderByDescending(s => s.Time).ThenByDescending(s => s.Name.Length).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (newestFirst.Count > 0)
            kept.Add(newestFirst[0].Name);
        var days = new HashSet<DateOnly>();
        var weeks = new HashSet<(int, int)>();
        foreach (var s in newestFirst)
        {
            var age = now - s.Time;
            if (age <= TimeSpan.FromHours(keep.AllHours))
                kept.Add(s.Name);
            var day = DateOnly.FromDateTime(s.Time);
            if (age <= TimeSpan.FromDays(keep.DailyDays) && days.Add(day))
                kept.Add(s.Name);
            var week = (ISOWeek.GetYear(s.Time), ISOWeek.GetWeekOfYear(s.Time));
            if (age <= TimeSpan.FromDays(7 * keep.WeeklyWeeks) && weeks.Add(week))
                kept.Add(s.Name);
        }
        return newestFirst.Where(s => !kept.Contains(s.Name)).ToList();
    }

    /// <summary>Delete what the keep rules drop, and ".partial" folders left by a run that died more than a day ago.
    /// Only folders named as snapshots, holding a manifest, are ever deleted. Returns their names.</summary>
    public static List<string> Prune(string machineFolder, Keep keep, DateTime now, bool dryRun)
    {
        var gone = new List<string>();
        foreach (var s in ToDrop(Snapshots(machineFolder), keep, now))
        {
            if (!dryRun)
                Directory.Delete(s.Folder, true);
            gone.Add(s.Name);
        }
        if (Directory.Exists(machineFolder))
            foreach (var folder in Directory.GetDirectories(machineFolder, "*" + PartialSuffix))
            {
                var name = System.IO.Path.GetFileName(folder);
                if (SnapshotName().IsMatch(name[..^PartialSuffix.Length]) && Directory.GetLastWriteTime(folder) < now.AddDays(-1))
                {
                    if (!dryRun)
                        Directory.Delete(folder, true);
                    gone.Add(name);
                }
            }
        return gone;
    }

    // ---- checking ----

    /// <summary>Re-hash a snapshot against its manifest: each problem (missing, wrong size or hash, an extra file),
    /// or none.</summary>
    public static List<string> Verify(string snapshotFolder, Manifest manifest)
    {
        var problems = new List<string>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            listed.Add(f.Path);
            if (!SafeRelative(f.Path))
            {
                problems.Add($"{f.Path}: not a safe path");
                continue;
            }
            var path = System.IO.Path.Combine(snapshotFolder, f.Path.Replace('/', '\\'));
            if (!File.Exists(path))
                problems.Add($"{f.Path}: missing");
            else
            {
                var data = ReadShared(path);
                if (data.LongLength != f.Size)
                    problems.Add($"{f.Path}: {data.LongLength} bytes, not {f.Size}");
                else if (Hash(data) != f.Sha256)
                    problems.Add($"{f.Path}: its SHA-256 doesn't match");
            }
        }
        foreach (var path in Directory.GetFiles(snapshotFolder, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(snapshotFolder, path).Replace('\\', '/');
            if (rel != Manifest.FileName && !listed.Contains(rel))
                problems.Add($"{rel}: not in the manifest");
        }
        return problems;
    }

    /// <summary>A "source/relative" path that stays inside its folder.</summary>
    public static bool SafeRelative(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'))
            return false;
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p != "." && p != "..");
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", En)} KB",
        _ => $"{(bytes / 1024.0 / 1024.0).ToString("0.#", En)} MB",
    };
}
