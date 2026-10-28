using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Reguliq.Api.Services.NewDashboard.CorrectedDocs;

/// <summary>
/// Writes resolved action-plan notes into a copy of a source Word document: a real inserted paragraph
/// immediately after the paragraph whose text matches the note's anchor (a short excerpt of the cited
/// gap, or the clause number as a last resort), not just appended at the end — DOCX, unlike PDF, can
/// take a genuine mid-document insertion because OpenXML paragraphs are already reflowable.
/// </summary>
public static class NdCorrectedDocxEmbedder
{
    public static byte[] Embed(byte[] sourceDocx, IReadOnlyList<NdActionPlanEmbedTarget> targets)
    {
        if (targets.Count == 0) return sourceDocx;

        using var stream = new MemoryStream();
        stream.Write(sourceDocx, 0, sourceDocx.Length);
        stream.Position = 0;

        using (var wordDoc = WordprocessingDocument.Open(stream, isEditable: true))
        {
            var body = wordDoc.MainDocumentPart?.Document?.Body;
            if (body == null) return sourceDocx;

            var paragraphs = body.Elements<Paragraph>().ToList();

            foreach (var target in targets)
            {
                var anchorParagraph = FindAnchorParagraph(paragraphs, target.AnchorText, target.ClauseNo);
                var note = BuildNoteParagraph(NdActionPlanEmbedNote.Build(target));

                if (anchorParagraph != null)
                    anchorParagraph.InsertAfterSelf(note);
                else
                    body.AppendChild(note);

                // Re-read so a second target in the same batch can anchor after this note too, keeping
                // several resolved actions for the same passage grouped together in document order.
                paragraphs = body.Elements<Paragraph>().ToList();
            }

            wordDoc.MainDocumentPart!.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph? FindAnchorParagraph(List<Paragraph> paragraphs, string? anchorText, string clauseNo)
    {
        Paragraph? Search(string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return null;
            var normalized = Normalize(needle);
            // Clause numbers like "3.5" are only 3 characters — too short a floor would reject the
            // clause-number fallback anchor entirely, while too low a floor risks matching noise; 2 is
            // the shortest a real clause or section number needs.
            if (normalized.Length < 2) return null;
            return paragraphs.FirstOrDefault(p => Normalize(p.InnerText).Contains(normalized, StringComparison.Ordinal));
        }

        return Search(anchorText ?? "") ?? Search(clauseNo);
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();

    private static Paragraph BuildNoteParagraph(string noteText)
    {
        var paragraph = new Paragraph
        {
            ParagraphProperties = new ParagraphProperties
            {
                ParagraphBorders = new ParagraphBorders
                {
                    LeftBorder = new LeftBorder { Val = BorderValues.Single, Size = 12, Color = "2E7D32" },
                },
                Shading = new Shading { Val = ShadingPatternValues.Clear, Fill = "EAF6EC" },
                SpacingBetweenLines = new SpacingBetweenLines { Before = "120", After = "120" },
            },
        };

        var lines = noteText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var run = new Run(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            run.RunProperties = new RunProperties();
            if (i == 0) run.RunProperties.Append(new Bold());
            paragraph.Append(run);
            if (i < lines.Length - 1) paragraph.Append(new Run(new Break()));
        }

        return paragraph;
    }
}
