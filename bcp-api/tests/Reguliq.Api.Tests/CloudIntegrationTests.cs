using Microsoft.Extensions.Configuration;
using Reguliq.Api.Services.NewDashboard.Integrations;
using Xunit;

namespace Reguliq.Api.Tests;

public class CloudIntegrationTests
{
    private static CloudSecrets Secrets(string key = "a-long-test-secret-value-for-cloud-tokens") =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Integrations:EncryptionKey"] = key })
            .Build());

    private static CloudOAuthState State(long expiresAt, string origin = "http://localhost:3002") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "google", expiresAt, CloudSecrets.NewNonce(), origin);

    [Fact]
    public void Encrypt_then_decrypt_returns_the_original_and_hides_it()
    {
        var s = Secrets();
        var stored = s.Encrypt("1//refresh-token-value");
        Assert.DoesNotContain("refresh-token-value", stored);
        Assert.Equal("1//refresh-token-value", s.Decrypt(stored));
    }

    [Fact]
    public void Encrypting_twice_gives_different_text()
    {
        var s = Secrets();
        Assert.NotEqual(s.Encrypt("same"), s.Encrypt("same"));
    }

    [Fact]
    public void A_token_encrypted_with_another_key_or_tampered_with_is_refused()
    {
        var stored = Secrets("key-one-key-one-key-one-key-one").Encrypt("secret");
        Assert.Null(Secrets("key-two-key-two-key-two-key-two").Decrypt(stored));

        var s = Secrets();
        var good = s.Encrypt("secret");
        var tampered = good[..^2] + (good.EndsWith("AA") ? "BB" : "AA");
        Assert.Null(s.Decrypt(tampered));
        Assert.Null(s.Decrypt(""));
        Assert.Null(s.Decrypt("not-a-token"));
    }

    [Fact]
    public void Signed_state_round_trips()
    {
        var s = Secrets();
        var original = State(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var back = s.VerifyState(s.SignState(original));
        Assert.Equal(original, back);
    }

    [Fact]
    public void State_that_expired_was_edited_or_signed_elsewhere_is_refused()
    {
        var s = Secrets();
        Assert.Null(s.VerifyState(s.SignState(State(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()))));

        var signed = s.SignState(State(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
        var parts = signed.Split('.');
        var edited = s.SignState(State(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), "https://evil.example"));
        Assert.Null(s.VerifyState(edited.Split('.')[0] + "." + parts[1]));

        Assert.Null(Secrets("another-key-another-key-another-key").VerifyState(signed));
        Assert.Null(s.VerifyState("garbage"));
        Assert.Null(s.VerifyState(null));
    }

    [Fact]
    public void Google_drive_is_only_configured_with_both_client_id_and_secret()
    {
        static GoogleDriveProvider With(Dictionary<string, string?> values) =>
            new(new StubHttpFactory(), new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.False(With(new()).IsConfigured);
        Assert.False(With(new() { ["Integrations:Google:ClientId"] = "id" }).IsConfigured);
        var ready = With(new() { ["Integrations:Google:ClientId"] = "id", ["Integrations:Google:ClientSecret"] = "secret" });
        Assert.True(ready.IsConfigured);

        var url = ready.BuildAuthorizationUrl("http://localhost:5100/nd/integrations/google/callback", "STATE");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=id", url);
        Assert.Contains("state=STATE", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("drive.readonly", url);
        Assert.Contains(Uri.EscapeDataString("http://localhost:5100/nd/integrations/google/callback"), url);
        Assert.DoesNotContain("secret", url);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Answers each request from a list of (address fragment, status, body); records what was asked.</summary>
    private sealed class FakeGoogle(params (string Contains, int Status, object Body)[] routes) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add(url);
            foreach (var (contains, status, body) in routes)
            {
                if (!url.Contains(contains)) continue;
                HttpContent content = body is byte[] bytes
                    ? new ByteArrayContent(bytes)
                    : new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
                return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static GoogleDriveProvider Drive(FakeGoogle fake) =>
        new(fake, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:Google:ClientId"] = "id",
            ["Integrations:Google:ClientSecret"] = "secret",
        }).Build());

    [Fact]
    public async Task Listing_marks_folders_and_only_pdf_word_and_google_docs_as_importable()
    {
        var fake = new FakeGoogle(("/drive/v3/files?", 200, new
        {
            nextPageToken = "NEXT",
            files = new object[]
            {
                new { id = "f1", name = "Policies", mimeType = "application/vnd.google-apps.folder" },
                new { id = "d1", name = "AML.pdf", mimeType = "application/pdf", size = "2048", modifiedTime = "2026-10-01T10:00:00Z" },
                new { id = "d2", name = "Manual.docx", mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", size = "100" },
                new { id = "d3", name = "Notes", mimeType = "application/vnd.google-apps.document" },
                new { id = "d4", name = "Budget.xlsx", mimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", size = "5" },
            },
        }));

        var page = await Drive(fake).ListAsync("token", null, null, null, CancellationToken.None);

        Assert.Equal("NEXT", page.NextPageToken);
        Assert.Equal("My Drive", page.FolderName);
        Assert.Equal(new[] { true, false, false, false, false }, page.Items.Select(i => i.IsFolder).ToArray());
        Assert.Equal(new[] { false, true, true, true, false }, page.Items.Select(i => i.Importable).ToArray());
        Assert.Equal(2048, page.Items[1].SizeBytes);
        Assert.Contains("'root' in parents and trashed = false", fake.Requests[0]);
    }

    [Fact]
    public async Task Listing_refuses_a_folder_id_that_could_break_out_of_the_query_and_escapes_search_text()
    {
        var fake = new FakeGoogle(("/drive/v3/files?", 200, new { files = Array.Empty<object>() }));
        var drive = Drive(fake);

        await Assert.ThrowsAsync<CloudProviderException>(() =>
            drive.ListAsync("token", "x' or name contains '", null, null, CancellationToken.None));

        await drive.ListAsync("token", null, null, "it's", CancellationToken.None);
        Assert.Contains(@"name contains 'it\'s'", fake.Requests[^1]);
    }

    [Fact]
    public async Task Download_exports_a_google_doc_as_word_and_refuses_other_types_and_oversized_files()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var fake = new FakeGoogle(
            ("/files/doc1/export", 200, bytes),
            ("/files/doc1?fields", 200, new { id = "doc1", name = "Board paper", mimeType = "application/vnd.google-apps.document" }),
            ("/files/sheet1?fields", 200, new { id = "sheet1", name = "Budget", mimeType = "application/vnd.google-apps.spreadsheet" }),
            ("/files/big1?fields", 200, new { id = "big1", name = "Huge.pdf", mimeType = "application/pdf", size = "999999999" }));
        var drive = Drive(fake);

        var doc = await drive.DownloadAsync("token", "doc1", 50 * 1024 * 1024, CancellationToken.None);
        Assert.Equal("Board paper.docx", doc.FileName);
        Assert.Equal(bytes, doc.Bytes);
        Assert.Contains("wordprocessingml.document", doc.ContentType);

        var notAllowed = await Assert.ThrowsAsync<CloudProviderException>(() =>
            drive.DownloadAsync("token", "sheet1", 50 * 1024 * 1024, CancellationToken.None));
        Assert.Contains("not a PDF or Word", notAllowed.Message);

        var tooBig = await Assert.ThrowsAsync<CloudProviderException>(() =>
            drive.DownloadAsync("token", "big1", 50 * 1024 * 1024, CancellationToken.None));
        Assert.Contains("larger than 50 MB", tooBig.Message);
    }

    [Fact]
    public async Task A_refused_refresh_token_asks_the_user_to_connect_again()
    {
        var fake = new FakeGoogle(("oauth2.googleapis.com/token", 400, new { error = "invalid_grant" }));
        await Assert.ThrowsAsync<CloudReconnectRequiredException>(() =>
            Drive(fake).RefreshAsync("old-refresh-token", CancellationToken.None));
    }

    [Fact]
    public async Task Token_exchange_reads_access_and_refresh_tokens()
    {
        var fake = new FakeGoogle(("oauth2.googleapis.com/token", 200, new
        {
            access_token = "ya29.abc",
            refresh_token = "1//refresh",
            expires_in = 3599,
            scope = "https://www.googleapis.com/auth/drive.readonly",
        }));
        var tokens = await Drive(fake).ExchangeCodeAsync("code", "http://localhost:5100/cb", CancellationToken.None);
        Assert.Equal("ya29.abc", tokens.AccessToken);
        Assert.Equal("1//refresh", tokens.RefreshToken);
        Assert.Equal(3599, tokens.ExpiresInSeconds);
    }
}
