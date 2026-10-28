using System.Text.RegularExpressions;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulPipelineV3Tests
{
    // AML-CFT Guidelines for FIs, clause 3.5, as extracted (wrapped lines, running header mid-clause).
    private const string Clause35 = """
3.5 Money Laundering
(AML-CFT Law Articles 2.1-3, 4, 29.3, AML-CFT Decision Article 1)
The AML-CFT Law defines money laundering as engaging in any of the following acts wilfully,
having knowledge that the funds are the proceeds of a felony or a misdemeanour (i.e., a
predicate offence):
· Transferring or moving proceeds or conducting any transaction with the aim of concealing
or disguising their Illegal source;
· Concealing or disguising the true nature, source or location of the proceeds as well as the
method involving their disposition, movement, ownership of or rights with respect to said
proceeds;
· Acquiring, possessing or using proceeds upon receipt;
· Assisting the perpetrator of the predicate offense to escape punishment.
Both the AML-CFT Law and the AML-CFT Decision define "funds" in a very broad sense as
"assets in whatever form, whether tangible, intangible, movable or immovable including
national currency, foreign currencies, documents or notes evidencing the ownership of those
assets or associated rights in any forms including electronic or digital forms or any interests,
profits or income originating or earned from these assets." They likewise define "proceeds"
as "funds generated directly or indirectly from the commitment of any crime or felony including
profits, privileges, and economic interests, or any similar funds converted wholly or partly into
other funds."
Anti-Money Laundering and Combating the Financing of Terrorism and Illegal Organisations Guidelines for Financial Institutions
Therefore, in order to be considered money laundering, it is not necessary for any of the
above-stipulated acts to involve only money or monetary instruments per se, but any number
of tangible or intangible assets such as, but not limited to:
· Funds bank or other financial accounts, including so-called virtual or crypto currencies;
· Financial instruments or securities, such as shares, bonds, notes, commercial paper,
promissory notes, IOUs, share warrants, options, rights (including land rights), or other
transferrable securities or bearer negotiable instruments;
· Contracts, loan instruments, titles, claims, insurance policies, or their assignment;
· Intellectual property (including but not limited to patents or registered trademarks),
royalties, licenses, or the rights thereto;
· Physical property, including but not limited to commodities, land, precious metals and
stones, motor vehicles or vessels, works of art, or any other goods exchanged as payment-
in-kind.
The size or monetary value of the financial or commercial transaction, the timeframe during
which it took place, and the nature of the funds or proceeds (whether in liquid funds or some
other tangible or intangible asset) are irrelevant to the suspicion and reporting of a suspicious
transaction.
The AML-CFT Law designates money laundering as a criminal offence. Its prosecution is
independent of that of any predicate offence to which it is related or from which the proceeds
are derived. The suspicion of money laundering is not dependent on proving that a predicate
offence has actually occurred or on proving the illicit source of the proceeds involved, but can
be inferred from certain information, including indicators or behavioural patterns.
According to the 2018 National Risk Assessment, professional third-party money laundering
has been identified as one of the top ML/FT threats in the UAE.
""";

    private const string Clause33 = """
3.3 Protection against Liability for Reporting Persons
(AML-CFT Law Article 27; AML-CFT Decision Article 17.3)
The AML-CFT Law and the AML-CFT Decision provide Financial Institutions, as well as their
board members, employees and authorised representatives, with protection from any
administrative, civil or criminal liability resulting from their good-faith performance of their
statutory obligation to report suspicious activity to the FIU. This protection is also applicable
if they did not know precisely what the underlying criminal activity was, and regardless of
whether illegal activity actually occurred.
""";

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>Every sentence-sized fragment of the clause must appear in some piece.</summary>
    private static void AssertNothingDropped(string clause, IReadOnlyList<string> pieces)
    {
        var joined = Flat(string.Join(" ", pieces));
        foreach (var line in clause.Split('\n').Select(l => l.Trim().TrimStart('·', ' ')).Where(l => l.Length > 0))
            Assert.Contains(Flat(line), joined);
    }

    [Fact]
    public void V2_split_keeps_only_8_parts_and_loses_the_timeframe_paragraph()
    {
        var parts = SubObligationSplitter.Split(Clause35, NdRegulPipelineVersions.V2ExpandedWording);
        Assert.Equal(8, parts.Count);
        Assert.DoesNotContain(parts, p => p.Contains("timeframe", StringComparison.Ordinal));
    }

    [Fact]
    public void V3_split_searches_every_line_of_clause_3_5()
    {
        var parts = SubObligationSplitter.Split(Clause35, NdRegulPipelineVersions.V3RelevanceSelection);

        Assert.True(parts.Count > 8, $"expected more than 8 parts, got {parts.Count}");
        AssertNothingDropped(Clause35, parts);
        Assert.Contains(parts, p => p.Contains("the timeframe during", StringComparison.Ordinal));
        Assert.Contains(parts, p => p.Contains("Its prosecution is", StringComparison.Ordinal)
                                    || p.Contains("prosecution is independent", StringComparison.Ordinal));
        Assert.Contains(parts, p => Flat(p).Contains("not dependent on proving that a predicate offence", StringComparison.Ordinal));
    }

    [Fact]
    public void V3_split_puts_the_list_lead_in_before_each_item_but_never_twice()
    {
        var parts = SubObligationSplitter.Split(Clause35, NdRegulPipelineVersions.V3RelevanceSelection).Select(Flat).ToList();

        var acquiring = Assert.Single(parts, p => p.Contains("Acquiring, possessing or using proceeds", StringComparison.Ordinal));
        Assert.Contains("defines money laundering as engaging in any of the following acts", acquiring);
        Assert.DoesNotContain("29.3, AML-CFT Decision Article 1)", acquiring); // heading and citation are not repeated

        // Items of the second list get the second list's own lead-in, not the first one.
        var intellectual = Assert.Single(parts, p => p.Contains("Intellectual property", StringComparison.Ordinal));
        Assert.Contains("such as, but not limited to:", intellectual);
        Assert.DoesNotContain("defines money laundering as engaging", intellectual);

        // The text after the last bullet is its own piece, not glued to "Physical property".
        var physical = Assert.Single(parts, p => p.Contains("Physical property", StringComparison.Ordinal));
        Assert.DoesNotContain("timeframe", physical);

        foreach (var p in parts)
            Assert.True(Regex.Matches(p, "defines money laundering as engaging").Count <= 1, p);
    }

    [Fact]
    public void V3_split_keeps_a_short_prose_clause_whole_or_complete()
    {
        var parts = SubObligationSplitter.Split(Clause33, NdRegulPipelineVersions.V3RelevanceSelection);
        AssertNothingDropped(Clause33, parts);
    }

    [Fact]
    public void V3_split_reads_ocr_dot_bullets_and_inline_lettered_items()
    {
        const string ocr = """
3.10 ML/FT Typologies
Examples of some of the key ML/FT typologies include:
. Currency exchanges / cash conversion: used to assist with smuggling to another jurisdiction;
. Cash couriers / currency smuggling: concealed movement of currency to avoid reporting;
· Structuring (smurfing): numerous small transactions to avoid detection thresholds.
""";
        var parts = SubObligationSplitter.SplitComplete(ocr);
        Assert.Contains(parts, p => p.Contains("Currency exchanges", StringComparison.Ordinal) && !p.Contains("Cash couriers", StringComparison.Ordinal));
        Assert.Contains(parts, p => p.Contains("Cash couriers", StringComparison.Ordinal) && !p.Contains("Structuring", StringComparison.Ordinal));

        const string inline = "LFIs must: (a) identify the customer before onboarding; (b) verify the beneficial owner; (c) keep records for five years.";
        var items = SubObligationSplitter.SplitComplete(inline);
        Assert.Equal(3, items.Count);
        Assert.All(items, p => Assert.StartsWith("LFIs must:", p));
    }

    [Fact]
    public void V3_split_merges_a_short_item_instead_of_dropping_it()
    {
        const string text = "Policy scope.\n· Customers and accounts held by the bank;\n· PEPs.\nThe policy is reviewed annually by the board.";
        var parts = SubObligationSplitter.SplitComplete(text);
        AssertNothingDropped(text, parts);
        Assert.Contains(parts, p => p.Contains("PEPs.", StringComparison.Ordinal));
    }

    private static readonly Guid DocId = Guid.NewGuid();

    private static RegulEmbeddingRetrievalService.Bm25Match Bm25(Guid id, double score) =>
        new(id, "x", "", DocId, "Doc", 1, score);

    private static RegulEmbeddingRetrievalService.RetrievalMatch Emb(Guid id, double sim) =>
        new(id, "x", "", DocId, "Doc", 1, sim);

    [Fact]
    public void V2_fusion_drops_the_best_keyword_only_match_and_v3_keeps_it()
    {
        var both = Guid.NewGuid();
        var keywordOnly = Guid.NewGuid();
        var bm25 = new List<RegulEmbeddingRetrievalService.Bm25Match> { Bm25(both, 10), Bm25(keywordOnly, 9.5) };
        var emb = new List<RegulEmbeddingRetrievalService.RetrievalMatch> { Emb(both, 0.74) };
        for (var i = 1; i < 120; i++) emb.Add(Emb(Guid.NewGuid(), 0.74 - 0.001 * i));

        var v2 = HybridFusionSelector.SelectDynamic(HybridFusionSelector.Fuse(bm25, emb));
        Assert.Equal(60, v2.Count);
        Assert.DoesNotContain(v2, m => m.SectionId == keywordOnly);

        var rank = new Dictionary<Guid, double>
        {
            [both] = HybridFusionSelector.RankScore(0) * 2,
            [keywordOnly] = HybridFusionSelector.RankScore(1),
        };
        var v3 = HybridFusionSelector.FuseByRank(bm25, [Emb(both, 0.74)], rank);
        Assert.Equal(2, v3.Count);
        Assert.Equal(both, v3[0].SectionId);
        Assert.Contains(v3, m => m.SectionId == keywordOnly && m.Bm25Score == 9.5 && m.EmbeddingSimilarity == null);
    }

    [Fact]
    public void Relevance_gate_keeps_sections_that_stand_out_and_follows_the_scores_not_a_count()
    {
        // 500 background sections around 0.60, three clear matches.
        var rng = new Random(7);
        var scores = Enumerable.Range(0, 500).Select(_ => (Guid.NewGuid(), 0.60 + rng.NextDouble() * 0.04)).ToList();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        scores.AddRange([(a, 0.80), (b, 0.78), (c, 0.77)]);

        var kept = HybridFusionSelector.SelectRelevant(scores.OrderByDescending(s => s.Item2).ToList(), scores.Count);
        Assert.Equal(new[] { a, b, c }, kept.Select(k => k.SectionId).ToArray());

        // Same shape with 40 strong matches: all 40 are kept (no maximum).
        var many = Enumerable.Range(0, 500).Select(_ => (Guid.NewGuid(), 0.60 + rng.NextDouble() * 0.04)).ToList();
        many.AddRange(Enumerable.Range(0, 40).Select(i => (Guid.NewGuid(), 0.80 - i * 0.0005)));
        Assert.Equal(40, HybridFusionSelector.SelectRelevant(many.OrderByDescending(s => s.Item2).ToList(), many.Count).Count);
    }

    [Fact]
    public void Relevance_gate_counts_unscored_sections_as_zero_for_bm25()
    {
        // BM25 returns only sections containing a query word; the other 995 sections scored 0.
        var hit1 = Guid.NewGuid();
        var hit2 = Guid.NewGuid();
        var weak = Guid.NewGuid();
        var kept = HybridFusionSelector.SelectRelevant([(hit1, 12.0), (hit2, 9.0), (weak, 0.4)], 1000);
        Assert.Contains(kept, k => k.SectionId == hit1);
        Assert.Contains(kept, k => k.SectionId == hit2);
        Assert.DoesNotContain(kept, k => k.SectionId == weak);
    }

    [Fact]
    public void Relevance_gate_keeps_the_best_match_when_nothing_stands_out()
    {
        var best = Guid.NewGuid();
        var flat = Enumerable.Range(0, 50).Select(_ => (Guid.NewGuid(), 0.5)).Prepend((best, 0.5)).ToList();
        Assert.Equal(best, Assert.Single(HybridFusionSelector.SelectRelevant(flat, flat.Count)).SectionId);
    }
}
