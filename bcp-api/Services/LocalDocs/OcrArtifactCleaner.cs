namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Strips one specific, common OCR artifact: dense rows of dot-leaders in a table of contents
/// ("1. INTRODUCTION .......................... 3") get misread by Tesseract as one long run of
/// consonant-only garbage (e.g. "ccocveeresmssesssnssessssssssesssssssnssnssssssnnns"), because a
/// row of tiny dots looks, to the OCR model, like a dense string of tiny character-like marks.
///
/// Only strips tokens that are implausible as real words — long, and almost no vowels. Normal text
/// (including short words, numbers, and real long words, which always carry a normal vowel ratio in
/// English) is never touched. This does not catch every OCR misread — only this specific pattern.
/// </summary>
public static class OcrArtifactCleaner
{
    private const int MinSuspectLength = 12;
    private const double MaxVowelRatio = 0.15;

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var tokens = lines[i].Split(' ');
            var changed = false;
            for (var t = 0; t < tokens.Length; t++)
            {
                if (!IsDotLeaderArtifact(tokens[t])) continue;
                tokens[t] = "...";
                changed = true;
            }
            if (changed)
                lines[i] = CollapseRepeatedEllipses(string.Join(' ', tokens));
        }
        return string.Join('\n', lines);
    }

    private static bool IsDotLeaderArtifact(string token)
    {
        if (token.Length < MinSuspectLength) return false;
        if (!token.All(char.IsLetter)) return false;

        var vowels = token.Count(c => "aeiouAEIOU".IndexOf(c) >= 0);
        var ratio = (double)vowels / token.Length;
        return ratio < MaxVowelRatio;
    }

    private static string CollapseRepeatedEllipses(string line)
    {
        while (line.Contains("... ..."))
            line = line.Replace("... ...", "...");
        return line;
    }
}
