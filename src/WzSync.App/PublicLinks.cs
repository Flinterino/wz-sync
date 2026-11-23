using System.Text.RegularExpressions;

namespace WzSync.App;

internal static class PublicLinks
{
    private static readonly Regex CurseForgeProject = new(
        @"^/(?:minecraft/mc-mods/[A-Za-z0-9_-]+|projects/[0-9]+)/?\z", RegexOptions.CultureInvariant);
    private static readonly Regex ModrinthProject = new(
        @"^/mod/[A-Za-z0-9_-]+/?\z", RegexOptions.CultureInvariant);

    internal static Uri? GetModPage(string source, Uri? page)
    {
        if (page is not { IsAbsoluteUri: true, Scheme: "https", Port: 443 } ||
            page.UserInfo.Length != 0 || page.Query.Length != 0 || page.Fragment.Length != 0 ||
            page.OriginalString.Contains('\\') || page.OriginalString.Any(char.IsControl))
            return null;
        var allowed = source switch
        {
            "curseforge" => page.Host is "www.curseforge.com" or "curseforge.com" &&
                            CurseForgeProject.IsMatch(page.AbsolutePath),
            "modrinth" => page.Host == "modrinth.com" && ModrinthProject.IsMatch(page.AbsolutePath),
            "github" => page.Host == "github.com" &&
                        page.AbsolutePath.TrimEnd('/') == "/Flinterino/wz-sync",
            _ => false
        };
        return allowed ? page : null;
    }
}
