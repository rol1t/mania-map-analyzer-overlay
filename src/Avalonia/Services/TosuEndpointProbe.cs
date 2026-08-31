using System.Net.Http;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

internal enum TosuEndpointCompatibility
{
    Unavailable = 0,
    ApiOnly = 1,
    ApplicationCompatible = 2
}

internal static class TosuEndpointProbe
{
    internal static readonly Uri DefaultBaseUri = new("http://127.0.0.1:24050/");

    public static async Task<TosuEndpointCompatibility> CheckAsync(
        HttpClient httpClient,
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);

        try
        {
            using var response = await httpClient.GetAsync(
                new Uri(baseUri, "json/v2?overlay_probe=1"),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return TosuEndpointCompatibility.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TosuEndpointCompatibility.Unavailable;
            }

            JsonElement root = document.RootElement;
            bool isTosuPayload = root.TryGetProperty("state", out _) ||
                root.TryGetProperty("beatmap", out _) ||
                root.TryGetProperty("game", out _) ||
                root.TryGetProperty("menu", out _);
            if (!isTosuPayload)
            {
                return TosuEndpointCompatibility.Unavailable;
            }

            using var analyzerResponse = await httpClient.GetAsync(
                new Uri(baseUri, "ManiaMapAnalyser/"),
                cancellationToken).ConfigureAwait(false);
            return analyzerResponse.IsSuccessStatusCode
                ? TosuEndpointCompatibility.ApplicationCompatible
                : TosuEndpointCompatibility.ApiOnly;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return TosuEndpointCompatibility.Unavailable;
        }
        catch (TaskCanceledException)
        {
            return TosuEndpointCompatibility.Unavailable;
        }
        catch (JsonException)
        {
            return TosuEndpointCompatibility.Unavailable;
        }
        catch (IOException)
        {
            return TosuEndpointCompatibility.Unavailable;
        }
    }
}
