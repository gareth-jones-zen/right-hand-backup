using System.Text;
using System.Text.RegularExpressions;

namespace RightHandBackup;

/// <summary>A file pattern, relative to a folder, with "/" between folders: <c>*</c> and <c>?</c> stay within one
/// folder, <c>**</c> spans any number of them (<c>handlers/**</c> is everything below handlers). Ignores case, as
/// Windows does. "\" is taken as "/".</summary>
public sealed class Glob
{
    private readonly string[] parts;
    private readonly Regex?[] regexes;   // null for "**"

    public string Text { get; }

    private Glob(string text, string[] parts)
    {
        Text = text;
        this.parts = parts;
        regexes = parts.Select(p => p == "**" ? null : Segment(p)).ToArray();
    }

    /// <summary>Throws FormatException for a pattern that's empty, absolute, or goes up a folder.</summary>
    public static Glob Parse(string text)
    {
        var t = text.Replace('\\', '/').Trim();
        if (t.Length == 0)
            throw new FormatException("a pattern can't be empty.");
        if (t.StartsWith('/') || (t.Length >= 2 && t[1] == ':'))
            throw new FormatException($"'{text}' should be relative to the folder, not absolute.");
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".."))
            throw new FormatException($"'{text}' can't go up a folder ('..').");
        parts = parts.Where(p => p != ".").ToArray();
        if (parts.Length == 0)
            throw new FormatException($"'{text}' names no files.");
        return new Glob(text, parts);
    }

    private static Regex Segment(string part)
    {
        var pattern = new StringBuilder("^");
        foreach (var c in part)
            pattern.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Whether a file's path (relative, "/" between folders) matches.</summary>
    public bool IsMatch(string relative) => Match(0, Split(relative), 0);

    /// <summary>Whether a file somewhere below this folder (relative, "/" between folders) could match: lets a walk
    /// skip folders no pattern reaches into.</summary>
    public bool CouldContain(string relativeFolder) => Prefix(0, Split(relativeFolder), 0);

    private static string[] Split(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private bool Match(int pi, string[] path, int si)
    {
        if (pi == parts.Length)
            return si == path.Length;
        if (regexes[pi] is null)
        {
            // a trailing ** is "something below": at least one more name
            var from = pi == parts.Length - 1 ? si + 1 : si;
            for (var k = from; k <= path.Length; k++)
                if (Match(pi + 1, path, k))
                    return true;
            return false;
        }
        return si < path.Length && regexes[pi]!.IsMatch(path[si]) && Match(pi + 1, path, si + 1);
    }

    private bool Prefix(int pi, string[] folder, int di)
    {
        if (di == folder.Length)
            return pi < parts.Length;
        if (pi == parts.Length)
            return false;
        if (regexes[pi] is null)
            return true;
        return regexes[pi]!.IsMatch(folder[di]) && Prefix(pi + 1, folder, di + 1);
    }

    public override string ToString() => Text;
}
