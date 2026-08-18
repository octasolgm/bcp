using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reguliq.Api.Infrastructure;

/// <summary>Build/deploy stamp written by scripts/write-deploy-version.ps1 at publish time.</summary>
public sealed class DeployVersionInfo
{
    public string Label { get; init; } = "dev";

    public string Api { get; init; } = "dev";

    public string Web { get; init; } = "dev";

    public string? Commit { get; init; }

    public string? Branch { get; init; }

    public string? BuiltAt { get; init; }

    public string? Notes { get; init; }

    public static DeployVersionInfo Load(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "deploy-version.json");
        if (!File.Exists(path))
            return new DeployVersionInfo();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeployVersionInfo>(json, JsonOptions)
                ?? new DeployVersionInfo();
        }
        catch
        {
            return new DeployVersionInfo();
        }
    }

    public object ToPayload(string? persistence = null, string? bootstrap = null) => new
    {
        label = Label,
        api = Api,
        web = Web,
        commit = Commit,
        branch = Branch,
        builtAt = BuiltAt,
        notes = Notes,
        persistence,
        bootstrap,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
