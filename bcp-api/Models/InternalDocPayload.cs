namespace Reguliq.Api.Models;

/// <summary>Resolved internal document for analysis (markdown + optional PDF bytes).</summary>
public sealed record InternalDocPayload(
    string FileHash,
    string FileName,
    string Markdown,
    byte[]? Pdf);
