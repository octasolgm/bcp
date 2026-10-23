using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reguliq.Api.Infrastructure.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class SupabaseJwtValidatorTests
{
    private const string Url = "https://project.supabase.co";
    private const string Secret = "legacy-hs256-secret-for-tests-only-0123456789";
    private const string Kid = "key-1";
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>Serves the JWKS; any Auth API call answers 401 and is counted, so a test can tell which path ran.</summary>
    private sealed class FakeSupabase(ECDsa signingKey) : HttpMessageHandler
    {
        public int JwksCalls;
        public int AuthApiCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/jwks.json"))
            {
                Interlocked.Increment(ref JwksCalls);
                var p = signingKey.ExportParameters(false);
                var jwks = JsonSerializer.Serialize(new
                {
                    keys = new[]
                    {
                        new { kty = "EC", crv = "P-256", alg = "ES256", kid = Kid, x = B64(p.Q.X!), y = B64(p.Q.Y!) },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) });
            }
            Interlocked.Increment(ref AuthApiCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static SupabaseJwtValidator Validator(FakeSupabase fake) =>
        new(
            Options.Create(new SupabaseJwtOptions { JwtSecret = Secret, Url = Url, ServiceRoleKey = "service" }),
            NullLogger<SupabaseJwtValidator>.Instance,
            new Factory(fake),
            new Env());

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Header, string Payload) Parts(string alg, long exp) => (
        B64(JsonSerializer.SerializeToUtf8Bytes(new { alg, typ = "JWT", kid = Kid })),
        B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = UserId.ToString(), email = "user@example.com", aud = "authenticated", iss = Url + "/auth/v1", exp,
        })));

    private static long InOneHour => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

    private static string Es256(ECDsa key, long exp)
    {
        var (h, p) = Parts("ES256", exp);
        var sig = key.SignData(Encoding.UTF8.GetBytes($"{h}.{p}"), HashAlgorithmName.SHA256);
        return $"{h}.{p}.{B64(sig)}";
    }

    private static string Hs256(long exp)
    {
        var (h, p) = Parts("HS256", exp);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return $"{h}.{p}.{B64(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{h}.{p}")))}";
    }

    [Fact]
    public async Task Es256_token_is_verified_with_the_published_key_without_calling_the_auth_api()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fake = new FakeSupabase(key);
        var validator = Validator(fake);

        var first = await validator.ValidateTokenAsync("Bearer " + Es256(key, InOneHour));
        var second = await validator.ValidateTokenAsync("Bearer " + Es256(key, InOneHour));

        Assert.Equal(UserId, first?.UserId);
        Assert.Equal("user@example.com", first?.Email);
        Assert.Equal(UserId, second?.UserId);
        Assert.Equal(1, fake.JwksCalls); // keys are cached
        Assert.Equal(0, fake.AuthApiCalls);
    }

    [Fact]
    public async Task Es256_token_signed_by_another_key_is_rejected()
    {
        using var published = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fake = new FakeSupabase(published);

        var user = await Validator(fake).ValidateTokenAsync(Es256(attacker, InOneHour));

        Assert.Null(user);
        Assert.Equal(1, fake.AuthApiCalls); // falls back to the Auth API, which also says no
    }

    [Fact]
    public async Task Expired_es256_token_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var user = await Validator(new FakeSupabase(key))
            .ValidateTokenAsync(Es256(key, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()));

        Assert.Null(user);
    }

    [Fact]
    public async Task Legacy_hs256_token_still_works_and_a_wrong_secret_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fake = new FakeSupabase(key);
        var validator = Validator(fake);

        var ok = await validator.ValidateTokenAsync(Hs256(InOneHour));
        var token = Hs256(InOneHour);
        var tampered = token[..^4] + (token[^4..] == "AAAA" ? "BBBB" : "AAAA");
        var bad = await validator.ValidateTokenAsync(tampered);

        Assert.Equal(UserId, ok?.UserId);
        Assert.Null(bad);
        Assert.Equal(0, fake.JwksCalls);
    }
}
