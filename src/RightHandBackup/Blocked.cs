namespace RightHandBackup;

/// <summary>What is never copied, whatever the settings say: live sign-ins, credentials and keys, and personal
/// contact details. A careless include (<c>**</c>, or <c>*.json</c> in a folder holding a token cache) can't copy one.
/// Signing in again is the safer recovery for any of these. Checked against every folder and file name in a path, so
/// a source folder inside an Edge profile is refused as a whole. Applies to a restore too.</summary>
public static class Blocked
{
    private static readonly (Glob Name, string Why)[] Rules =
    [
        (Glob.Parse("*edge-profile*"), "an Edge profile (live cookies and sign-ins)"),
        (Glob.Parse("User Data"), "a browser profile folder (live cookies and sign-ins)"),
        (Glob.Parse("Cookies"), "browser cookies"),
        (Glob.Parse("Cookies-journal"), "browser cookies"),
        (Glob.Parse("Login Data*"), "saved browser passwords"),
        (Glob.Parse("msal-cache*"), "a sign-in token cache"),
        (Glob.Parse("*token-cache*"), "a sign-in token cache"),
        (Glob.Parse(".credentials.json"), "sign-in credentials"),
        (Glob.Parse("credentials.json"), "sign-in credentials"),
        (Glob.Parse(".ssh"), "keys"),
        (Glob.Parse(".gnupg"), "keys"),
        (Glob.Parse("*.pfx"), "a private key or certificate"),
        (Glob.Parse("*.p12"), "a private key or certificate"),
        (Glob.Parse("*.pem"), "a private key or certificate"),
        (Glob.Parse("*.key"), "a private key"),
        (Glob.Parse("org-directory-state.json"), "personal contact details (the emergency contact sheets' source)"),
        (Glob.Parse("*.vcf"), "contact cards"),
    ];

    /// <summary>Why a path is refused, or null if it isn't.</summary>
    public static string? Why(string path)
    {
        foreach (var name in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            foreach (var (glob, why) in Rules)
                if (glob.IsMatch(name))
                    return why;
        return null;
    }
}
