namespace Reguliq.Api.Models;

/// <summary>Resolved internal document for analysis (markdown + optional PDF bytes).</summary>
/// <param name="FileHash">
/// Landing AI cache key for ND internal parse (<c>nd-reg:{storedDocumentId}</c>).
/// Legacy callers may still pass a content SHA-256.
/// </param>
public sealed record InternalDocPayload(
    string FileHash,
    string FileName,
    string Markdown,
    byte[]? Pdf);
