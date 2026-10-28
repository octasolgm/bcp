using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Pipeline v6: the names a bank uses for itself in its own policies ("DIFC is protected ...", "UAE is obliged to
/// report ..."), found as the subject of duties in the run's own documents, so a clause that says "financial
/// institutions" or "the institution" is also searched with the bank's own name. Detected per run from the selected
/// documents only (no stored list), which keeps it inside the run's workspace.
/// </summary>
public static partial class InstitutionNames
{
    /// <summary>A name must be the subject of a duty at least this many times.</summary>
    public const int MinOccurrences = 5;

    /// <summary>At most this many names are used (the most frequent).</summary>
    public const int MaxNames = 3;

    [GeneratedRegex(@"\b((?:[A-Z][A-Za-z&\-]*)(?:\s+[A-Z][A-Za-z&\-]*){0,3})\s+(?:shall|must|will|is\s+(?:obliged|required|committed|protected|responsible))\b")]
    private static partial Regex DutySubject();

    [GeneratedRegex(@"\b(?:licensed\s+financial\s+institutions?|financial\s+institutions?|the\s+institution|institutions)\b|\bL?FIs?\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstitutionTerm();

    // Not the bank itself: people, functions, documents, regulators and authorities that also "shall" do things.
    private static readonly HashSet<string> NotSelfNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "It", "Each", "Every", "All", "Any", "Such", "If", "When", "Where", "He", "She",
        "They", "We", "You", "Staff", "Employee", "Employees", "Customer", "Customers", "Client", "Clients", "Management",
        "Senior", "Board", "Compliance", "MLRO", "Officer", "Department", "Unit", "Team", "Branch", "Bank", "Banks",
        "Institution", "Institutions", "FI", "FIs", "LFI", "LFIs", "FIU", "CBUAE", "DFSA", "FATF", "SCA", "Regulator",
        "Authority", "Authorities", "Government", "Ministry", "Court", "Law", "Article", "Section", "Policy", "Procedure",
        "Manual", "Report", "Reports", "Person", "Persons", "Entity", "Entities", "Committee", "Head", "Director",
        "Directors", "Auditor", "Audit", "Internal", "External", "Group",
        // Reports, processes and AML terms written in capitals ("SAR shall be filed ...") are not the bank.
        "SAR", "SARs", "STR", "STRs", "CTR", "CTRs", "AML", "CFT", "CTF", "CDD", "EDD", "SDD", "KYC", "KYB", "PEP", "PEPs",
        "UBO", "UBOs", "TFS", "NRA", "ML", "TF", "PF", "EOCN", "GoAML", "RFI", "RFIs", "MLRO", "DMLRO", "AMLCO",
        "SWIFT", "IBAN", "NPO", "NPOs", "DNFBP", "DNFBPs", "VASP", "VASPs",
    };

    public static IReadOnlyList<string> Detect(IEnumerable<string> texts)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            foreach (Match m in DutySubject().Matches(text ?? ""))
            {
                var words = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                while (words.Count > 0 && NotSelfNames.Contains(words[0]) && words[0] is "The" or "This" or "That") words.RemoveAt(0);
                if (words.Count == 0 || words.Any(w => NotSelfNames.Contains(w))) continue;
                var name = string.Join(' ', words);
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        return counts.Where(c => c.Value >= MinOccurrences)
            .OrderByDescending(c => c.Value)
            .Take(MaxNames)
            .Select(c => c.Key)
            .ToList();
    }

    /// <summary>The clause part reworded once per name, with every institution term replaced by that name; empty when
    /// the part has no institution term or there is no name.</summary>
    public static IReadOnlyList<string> Variants(string text, IReadOnlyList<string> names)
    {
        if (names.Count == 0 || !InstitutionTerm().IsMatch(text)) return [];
        return names.Select(n => InstitutionTerm().Replace(text, n)).Distinct(StringComparer.Ordinal).ToList();
    }
}
