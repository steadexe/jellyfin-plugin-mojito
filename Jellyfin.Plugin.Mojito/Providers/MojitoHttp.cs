using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mojito.Providers;

/// <summary>
/// Shared HTTP helper for the plugin (single connection pool, no server auth interplay).
/// </summary>
public class MojitoHttp
{
    /// <summary>
    /// Gets the shared HTTP client used for Radarr/Sonarr calls.
    /// </summary>
    public HttpClient Http { get; } = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Sends a GET request with the *arr API key and deserializes the JSON body.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="baseUrl">The *arr base URL.</param>
    /// <param name="apiKey">The *arr API key.</param>
    /// <param name="path">The API path, e.g. "/api/v3/movie/lookup?term=...".</param>
    /// <returns>The deserialized response.</returns>
    public async Task<T> GetArrAsync<T>(string baseUrl, string apiKey, string path)
    {
        var url = baseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync<T>(response).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"Empty response from {url}");
    }

    /// <summary>
    /// Sends a POST request with a JSON body to a *arr API.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="baseUrl">The *arr base URL.</param>
    /// <param name="apiKey">The *arr API key.</param>
    /// <param name="path">The API path.</param>
    /// <param name="payload">The JSON payload.</param>
    /// <returns>The deserialized response.</returns>
    public async Task<T> PostArrAsync<T>(string baseUrl, string apiKey, string path, object payload)
    {
        var url = baseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync<T>(response).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"Empty response from {url}");
    }

    /// <summary>
    /// Sends a DELETE request to a *arr API.
    /// </summary>
    /// <param name="baseUrl">The *arr base URL.</param>
    /// <param name="apiKey">The *arr API key.</param>
    /// <param name="path">The API path.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteArrAsync(string baseUrl, string apiKey, string path)
    {
        var url = baseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Downloads a .torrent file from a *arr download URL.
    /// </summary>
    /// <param name="apiKey">The *arr API key (appended as query param by *arr download URLs).</param>
    /// <param name="downloadUrl">The download URL.</param>
    /// <returns>The torrent file bytes.</returns>
    public async Task<byte[]> DownloadTorrentFileAsync(string apiKey, string downloadUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (!downloadUrl.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ConfigureAwait(false);
        }
    }
}
