

namespace WzSync.Core;

public static class ManagedPolicy
{
    public static IReadOnlyList<string> Rules { get; } = Array.AsReadOnly(new[]
    {
        "config/DistantHorizons.toml", "config/analoglib/analoglib.client.toml",
        "config/better_cloud_shadows.json", "config/cirrus-client.json",
        "config/eclipticseasons-client.toml", "config/entity_texture_features.json",
        "config/livingwind-client.toml", "config/particlerain/config.json",
        "config/particlerain/particles.json", "config/pb_world_impact-client.toml",
        "config/sable_ragdoll_revival-client.toml", "config/seasonalfoliage-client.toml",
        "config/sodium-options.json", "config/streamsreflowing-client.properties",
        "config/subtle_effects/entities.toml", "config/subtle_effects/environment.toml",
        "config/subtle_effects/general.toml", "config/vista-client.toml", "mods/"
    });

    internal static void ValidateRelative(string path, bool directory = false)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') || path.Any(char.IsControl))
            throw new InvalidDataException("Invalid relative path.");
        var value = directory && path.EndsWith('/') ? path[..^1] : path;
        foreach (var part in value.Split('/'))
        {
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>',':','"','|','?','*']) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                part.Equals(".wzsync", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe path segment.");
        }
    }

    public static void ValidateFile(string path)
    {
        ValidateRelative(path);
        if (!(path.StartsWith("mods/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1) && !Rules.Contains(path, StringComparer.Ordinal))
            throw new InvalidDataException("Path is outside client policy.");
    }

    internal static void ValidateRule(string rule)
    {
        ValidateRelative(rule, true);
        if (!Rules.Contains(rule, StringComparer.Ordinal) && !(rule.StartsWith("mods/", StringComparison.Ordinal) && !rule.EndsWith('/')))
            throw new InvalidDataException("Managed scope widens client policy.");
        if (rule != "mods/") ValidateFile(rule);
    }

    internal static bool Includes(IEnumerable<string> rules, string path) => rules.Any(rule => rule == path || rule == "mods/" && path.StartsWith("mods/", StringComparison.Ordinal));
}
