using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RightHandBackup;

/// <summary>One file in a snapshot: its path in the snapshot ("source/relative/path"), size, SHA-256 (lower-case hex)
/// and when it was last changed (UTC).</summary>
public sealed record ManifestFile(string Path, long Size, string Sha256, DateTime ModifiedUtc);

/// <summary>A file left out of a snapshot, and why.</summary>
public sealed record Note(string Path, string Why);

/// <summary>manifest.json, one per snapshot: what's in it, what was skipped or refused, and whether every file was
/// still as copied at the end of the pass.</summary>
public sealed class Manifest
{
    public const string FileName = "manifest.json";

    public string Machine { get; init; } = "";
    public DateTime Created { get; init; }
    public string Version { get; init; } = "";
    public int Attempts { get; init; } = 1;
    public List<SourceInfo> Sources { get; init; } = [];
    public List<ManifestFile> Files { get; init; } = [];
    public List<Note> Skipped { get; init; } = [];
    public List<Note> Blocked { get; init; } = [];

    /// <summary>Files that were still changing after the last attempt: taken as they were, and said so.</summary>
    public List<string> Unsettled { get; init; } = [];

    public long Bytes => Files.Sum(f => f.Size);

    public sealed record SourceInfo(string Name, string Folder);

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string Utc(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", En);

    public static DateTime ParseUtc(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-ddTHH:mm:ssZ", En, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public JsonObject ToJson() => new()
    {
        ["tool"] = "right-hand-backup",
        ["version"] = Version,
        ["machine"] = Machine,
        ["created"] = Created.ToString("yyyy-MM-ddTHH:mm:ss", En),
        ["attempts"] = Attempts,
        ["sources"] = new JsonArray(Sources.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["folder"] = s.Folder }).ToArray()),
        ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256, ["modified"] = Utc(f.ModifiedUtc),
        }).ToArray()),
        ["skipped"] = new JsonArray(Skipped.Select(n => (JsonNode)new JsonObject { ["path"] = n.Path, ["why"] = n.Why }).ToArray()),
        ["blocked"] = new JsonArray(Blocked.Select(n => (JsonNode)new JsonObject { ["path"] = n.Path, ["why"] = n.Why }).ToArray()),
        ["unsettled"] = new JsonArray(Unsettled.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
    };

    /// <summary>Throws FormatException for a manifest that can't be read.</summary>
    public static Manifest Parse(string text)
    {
        try
        {
            var o = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("not a JSON object");
            static List<Note> Notes(JsonNode? node) =>
                node?.AsArray().Select(n => new Note((string)n!["path"]!, (string?)n["why"] ?? "")).ToList() ?? [];
            return new Manifest
            {
                Machine = (string?)o["machine"] ?? "",
                Version = (string?)o["version"] ?? "",
                Created = DateTime.ParseExact((string)o["created"]!, "yyyy-MM-ddTHH:mm:ss", En),
                Attempts = (int?)o["attempts"] ?? 1,
                Sources = o["sources"]?.AsArray().Select(s => new SourceInfo((string)s!["name"]!, (string)s["folder"]!)).ToList() ?? [],
                Files = o["files"]!.AsArray().Select(f => new ManifestFile((string)f!["path"]!, (long)f["size"]!, (string)f["sha256"]!,
                    ParseUtc((string)f["modified"]!))).ToList(),
                Skipped = Notes(o["skipped"]),
                Blocked = Notes(o["blocked"]),
                Unsettled = o["unsettled"]?.AsArray().Select(p => (string)p!).ToList() ?? [],
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or KeyNotFoundException
                                          or FormatException or ArgumentException)
        {
            throw new FormatException($"the manifest can't be read ({error.Message})", error);
        }
    }

    public static Manifest Load(string snapshotFolder)
    {
        var path = Path.Combine(snapshotFolder, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No {FileName} in {snapshotFolder}: not a complete snapshot.");
        return Parse(File.ReadAllText(path));
    }
}

/// <summary>JSON written indented, without reflection (Native AOT), and replaced atomically.</summary>
public static class JsonFile
{
    public static string Text(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static void Write(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Text(node), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public static JsonObject Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return [];
        }
    }
}
