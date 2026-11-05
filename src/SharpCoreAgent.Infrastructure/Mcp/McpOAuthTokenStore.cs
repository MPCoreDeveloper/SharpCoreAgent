using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The small, explicit token record written only when a token file was named.
/// </summary>
/// <param name="AccessToken">The bearer token.</param>
/// <param name="RefreshToken">The refresh token, when the server issued one.</param>
/// <param name="ExpiresAt">When the access token expires, when known.</param>
/// <param name="ClientId">The registered public client, needed to refresh it later.</param>
internal sealed record McpStoredToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("client_id")] string? ClientId);

/// <summary>
/// Reads and writes one OAuth token file without ever including it in a log line.
/// </summary>
/// <remarks>
/// Persistence is opt-in. The file is written with user-only permissions on Unix and inherits the profile directory's
/// ACL on Windows; it is never placed under the workspace. A malformed or unreadable file is treated as absent so a
/// stale cache cannot stop the interactive flow.
/// </remarks>
internal static class McpOAuthTokenStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Tries to read a stored token.</summary>
    /// <param name="path">The token file path.</param>
    /// <param name="token">The token, when the file is usable.</param>
    /// <returns><see langword="true"/> when a token was read.</returns>
    public static bool TryLoad(string path, out McpStoredToken? token)
    {
        token = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            token = JsonSerializer.Deserialize<McpStoredToken>(File.ReadAllText(path), Json);
            return token is not null && !string.IsNullOrWhiteSpace(token.AccessToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            token = null;
            return false;
        }
    }

    /// <summary>Writes a token file, replacing the previous one atomically.</summary>
    /// <param name="path">The token file path.</param>
    /// <param name="token">The token to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The write.</returns>
    public static async Task SaveAsync(string path, McpStoredToken token, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(token, Json), cancellationToken)
                .ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
