using System.Text.Json;

namespace AI.Configuration;

public static class AppConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<AppConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken);
        return config ?? throw new InvalidOperationException($"Failed to parse configuration at {path}");
    }
}
