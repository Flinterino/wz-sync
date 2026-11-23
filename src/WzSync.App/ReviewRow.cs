using WzSync.Core;

namespace WzSync.App;

internal sealed record ReviewRow(string Action, string Path, string CurrentSize, string DesiredSize,
    string CurrentHash, string DesiredHash, string Source, string Provider, Uri? Page, string? Notes)
{
    public bool HasPage => PublicLinks.GetModPage(Provider, Page) != null;

    internal static ReviewRow FromPlan(PlanEntry row)
    {
        var file = row.Desired;
        var provider = file?.Source ?? "";
        var page = PublicLinks.GetModPage(provider, file?.Page);
        var manual = file?.Availability == "manual";
        var source = file?.Url?.Host ?? page?.Host ?? (file == null ? "Local profile" : "Manual");
        if (manual && row.Action != PlanAction.Keep && page != null) source += " · manual";
        var reason = manual && row.Action != PlanAction.Keep ? FormatReason(file?.Reason) : null;
        return new(row.Action.ToString().ToUpperInvariant(), row.Path, Size(row.Current?.Size), Size(file?.Size),
            row.Current?.Sha256 ?? "—", file?.Sha256 ?? "—", source, provider, page, reason);
    }

    internal static ReviewRow FromRestore(RestoreEntry row) =>
        new(row.Action.ToString().ToUpperInvariant(), row.Path, Size(row.Current?.Size), Size(row.Desired?.Size),
            row.Current?.Sha256 ?? "—", row.Desired?.Sha256 ?? "—",
            row.Action is PlanAction.Add or PlanAction.Update ? "Local backup" : "Local profile", "", null, null);

    private static string Size(long? bytes) => bytes is long length ? $"{length:N0} B" : "—";

    private static string? FormatReason(string? reason) => reason == null
        ? null
        : new string(reason.Where(character => !char.IsControl(character)).Take(220).ToArray());
}
