namespace Reguliq.Api.Infrastructure;

/// <summary>Reads BCP settings from appsettings (nested or flat keys).</summary>
public static class BcpConfiguration
{
    public static string? GetString(IConfiguration config, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = config[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public static bool IsTrue(IConfiguration config, params string[] keys) =>
        keys.Any(k => string.Equals(config[k], "true", StringComparison.OrdinalIgnoreCase));

    public static bool IsFalse(IConfiguration config, params string[] keys) =>
        keys.Any(k => config[k] is "false" or "0");

    public static int GetInt(IConfiguration config, int defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            var raw = config[key];
            if (int.TryParse(raw, out var value))
                return value;
        }

        return defaultValue;
    }
}
