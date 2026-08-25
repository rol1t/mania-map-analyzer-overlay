using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Downloads one external component archive and verifies its GitHub SHA-256
/// digest before the installer is allowed to consume it.
/// </summary>
public sealed class ComponentDownloader : IComponentArchiveDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _userAgent;

    public ComponentDownloader(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? throw new ArgumentException("A user agent is required.", nameof(userAgent))
            : userAgent;
    }

    public async Task DownloadAndVerifyAsync(
        GitHubAsset asset,
        string destination,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            throw new ArgumentException("The asset has no download URL.", nameof(asset));
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("A destination path is required.", nameof(destination));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long? total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true))
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total is > 0)
                {
                    progress?.Report((int)Math.Clamp(copied * 100 / total.Value, 0, 100));
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (new FileInfo(destination).Length == 0)
        {
            throw new InvalidOperationException("The downloaded file is empty.");
        }

        if (string.IsNullOrWhiteSpace(asset.Digest) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GitHub did not provide a SHA-256 digest for the downloaded component.");
        }

        await using var hashStream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
        var expected = asset.Digest["sha256:".Length..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The downloaded file failed its SHA-256 integrity check.");
        }
    }
}
