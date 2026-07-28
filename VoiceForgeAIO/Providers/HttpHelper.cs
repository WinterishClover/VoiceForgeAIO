using System.Net;

namespace VoiceForgeAIO.Providers;

/// <summary>
/// One shared HttpClient for every provider (no BaseAddress/default headers, since
/// each provider needs its own), plus a common retry-with-backoff wrapper so each
/// provider class doesn't reimplement 429/5xx handling.
/// </summary>
internal static class HttpHelper
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Sends a request built fresh by <paramref name="requestFactory"/> on every attempt
    /// (HttpRequestMessage can't be resent), retrying on 429/5xx with exponential
    /// backoff + jitter, and throws with the response body on any other failure.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct, int maxRetries = 5)
    {
        const double baseDelaySeconds = 1.0;
        const double maxDelaySeconds = 30.0;
        var rng = Random.Shared;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = requestFactory();
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                ((int)response.StatusCode >= 500 && (int)response.StatusCode < 600))
            {
                if (attempt >= maxRetries)
                    return response;

                double delay = Math.Min(baseDelaySeconds * Math.Pow(2, attempt), maxDelaySeconds) + rng.NextDouble();
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                continue;
            }

            return response;
        }
    }

    public static async Task ThrowIfUnsuccessful(HttpResponseMessage response, string providerName)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"{providerName} HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
