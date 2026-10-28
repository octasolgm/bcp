using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Compliance verdicts a clause can carry.</summary>
public static class ClauseStatuses
{
    public const string Compliant = "compliant";
    public const string Partial = "partial_compliant";
    public const string NonCompliant = "non_compliant";

    public static bool IsValid(string? value) =>
        value is Compliant or Partial or NonCompliant;

    public static string? Normalize(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return v switch
        {
            "compliant" => Compliant,
            "partial" or "partial_compliant" or "partially_compliant" => Partial,
            "non_compliant" or "noncompliant" or "not_compliant" => NonCompliant,
            _ => null,
        };
    }
}

/// <summary>
/// Manual overrides of a clause's compliance verdict. The automatic rule lives in
/// <see cref="NdGapStatusResolver"/>, which walks clause → gap → action rather than
/// reading every action on the clause at once.
/// </summary>
public static class NdClauseStatusResolver
{
    /// <inheritdoc cref="NdGapStatusResolver.RecomputeAsync"/>
    public static Task<int> RecomputeAsync(
        AppDbContext db,
        IEnumerable<Guid> pointIds,
        CancellationToken ct) => NdGapStatusResolver.RecomputeAsync(db, pointIds, ct);

    /// <summary>Records a status a user picked, so the automatic rule stops managing it.</summary>
    public static void ApplyManual(NdAnalysisPoint point, string status)
    {
        point.AiFinalStatus ??= point.FinalStatus;
        point.FinalStatus = status;
        point.FinalStatusSource = "manual";
    }

    /// <summary>Hands the clause back to the pipeline verdict and the automatic rule.</summary>
    public static void ClearManual(NdAnalysisPoint point)
    {
        if (point.FinalStatusSource != "manual") return;
        point.FinalStatus = point.AiFinalStatus ?? point.FinalStatus;
        point.FinalStatusSource = null;
    }
}
