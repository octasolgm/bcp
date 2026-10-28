namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Small, conservative English word-root reducer for keyword search (pipeline v6): "report", "reports", "reported" and
/// "reporting" all become "report"; "suspicion" and "suspicious" share a root; "policies" becomes "policy". One suffix is
/// removed per word, the remaining root keeps at least 3 letters, and words of 3 letters or fewer (STR, AML, UAE) are
/// left alone. Used on both sides (policy passages and clause wording), so it only has to be consistent, not
/// dictionary-correct.
/// </summary>
public static class EnglishStemmer
{
    private static readonly string[] Suffixes =
    [
        "ations", "ation", "ingly", "ities", "ments", "ings", "ions", "ious", "ment", "ated", "ates",
        "ity", "ing", "ion", "ous", "ive", "ate", "ies", "es", "ed", "ly", "al", "s",
    ];

    public static string Stem(string word)
    {
        if (word.Length <= 3 || !word.All(char.IsLetter)) return word;
        var w = word.ToLowerInvariant();
        foreach (var suffix in Suffixes)
        {
            if (!w.EndsWith(suffix, StringComparison.Ordinal) || w.Length - suffix.Length < 3) continue;
            var root = w[..^suffix.Length];
            switch (suffix)
            {
                case "ies":
                    w = root + "y";
                    break;
                case "es" when !(root.EndsWith('s') || root.EndsWith('x') || root.EndsWith('z') || root.EndsWith("ch") || root.EndsWith("sh")):
                    // "cases" -> "case": treat as a plain "s"
                    w = w[..^1];
                    break;
                case "s" when w.EndsWith("ss") || w.EndsWith("us") || w.EndsWith("is"):
                    continue;
                case "ed" when root.EndsWith('e'):
                    // "proceed", "need": not a past tense
                    continue;
                case "ed" or "ing" or "ings" or "ingly":
                    w = UndoubleConsonant(root);
                    break;
                default:
                    w = root;
                    break;
            }

            break;
        }

        if (w.Length > 4 && w.EndsWith('e')) w = w[..^1];
        return w;
    }

    // "committed" -> "committ" -> "commit"; "running" -> "run". Not after l, s or z ("controlled" stays "controll").
    private static string UndoubleConsonant(string root) =>
        root.Length >= 4 && root[^1] == root[^2] && !"aeiouylsz".Contains(root[^1]) ? root[..^1] : root;
}
