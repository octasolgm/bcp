namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Where a business operates: a country (jurisdiction) grouped under a region. One setting drives three
/// things for a workspace - the default date convention, which regulator rulebooks it imports, and what the
/// platform admin sees in the Business workspaces list.
///
/// Adding a country: add an entry here, make sure <see cref="Jurisdiction.DateFormat"/> is one of
/// WorkspacesController.ValidDateFormatRegions, and add a matching country node to the rulebook seed pack
/// (SeedData/rulebook-categories-middle-east.json or a new pack for another region).
/// </summary>
public static class JurisdictionCatalog
{
    /// <param name="Code">Stable short code stored on the workspace (ISO 3166-1 alpha-2, lower case).</param>
    /// <param name="Country">Display name, also the country node name in the rulebook seed pack.</param>
    /// <param name="Region">Region this country is grouped under, and the seed pack's top-level node.</param>
    /// <param name="DateFormat">Date convention this country defaults to (a ValidDateFormatRegions value).</param>
    /// <param name="Regulators">Regulators whose rulebooks this country imports (display only).</param>
    /// <param name="IncludePacks">
    /// Other seed packs whose top-level authorities also belong to this country. Each country's regulator is
    /// held in its own pack, read from that regulator's own published rulebook, so one country can be
    /// re-read and replaced without touching the others. The region pack only lists which countries exist.
    /// </param>
    public sealed record Jurisdiction(
        string Code,
        string Country,
        string Region,
        string DateFormat,
        string[] Regulators,
        string[] IncludePacks);

    public static readonly IReadOnlyList<Jurisdiction> All =
    [
        new("ae", "United Arab Emirates", "Middle East", "uae",
            [
                "Central Bank of the UAE",
                "Dubai Financial Services Authority",
                "Abu Dhabi Global Market",
                "Capital Market Authority",
            ],
            ["rulebook-categories-cbuae.json"]),
        new("sa", "Saudi Arabia", "Middle East", "uae", ["Saudi Central Bank (SAMA)"],
            ["rulebook-categories-saudi-arabia.json"]),
        new("qa", "Qatar", "Middle East", "uae", ["Qatar Financial Centre Regulatory Authority (QFCRA)"],
            ["rulebook-categories-qatar.json"]),
        new("om", "Oman", "Middle East", "uae", ["Central Bank of Oman (CBO)"],
            ["rulebook-categories-oman.json"]),
        new("bh", "Bahrain", "Middle East", "uae", ["Central Bank of Bahrain (CBB)"],
            ["rulebook-categories-bahrain.json"]),
    ];

    /// <summary>Seed pack holding each region's rulebook tree, by region name.</summary>
    public static readonly IReadOnlyDictionary<string, string> SeedFileByRegion =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Middle East"] = "rulebook-categories-middle-east.json",
        };

    public static Jurisdiction? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(j => string.Equals(j.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsValid(string? code) => Find(code) != null;

    /// <summary>Normalised code to store, or null when unset/"none".</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(code)?.Code;
    }
}
