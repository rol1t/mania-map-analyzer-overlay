using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Reads GitHub release metadata. Downloading and installing assets remain
/// owned by UpdateService until those boundaries receive their own contracts.
/// </summary>
public sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public GitHubReleaseClient(HttpClient httpClient, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public async Task<GitHubRelease> GetLatestAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("A GitHub repository is required.", nameof(repository));
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{repository}/releases/latest",
            requestTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(
                content,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GitHub returned an empty release for {repository}.");
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest
    {
        get; set;
    }
}
