namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Resume chunked PDF parse after pause; partial markdown is stored in parse cache.</summary>
public sealed class RegulationParseCheckpoint
{
    /// <summary>0-based index of the next chunk to parse.</summary>
    public int ResumeFromChunkIndex { get; init; }

    public string? PartialMarkdown { get; init; }

    public Func<int, string, Task>? OnChunkParsedAsync { get; init; }
}
