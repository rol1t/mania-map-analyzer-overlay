using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task ReadsLatestReleaseAndAssetMetadataThroughInjectedHttpClient()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"tag_name\":\"v2.3.0\",\"assets\":[{\"name\":\"tosu.zip\",\"browser_download_url\":\"https://example.test/tosu.zip\",\"digest\":\"sha256:abc\"}]}",
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new GitHubReleaseClient(
            httpClient,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        GitHubRelease release = await client.GetLatestAsync("owner/repository");

        Assert.Equal("v2.3.0", release.TagName);
        GitHubAsset asset = Assert.Single(release.Assets);
        Assert.Equal("tosu.zip", asset.Name);
        Assert.Equal("sha256:abc", asset.Digest);
        Assert.Equal(
            "https://api.github.com/repos/owner/repository/releases/latest",
            handler.RequestUri!.ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public Uri? RequestUri
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
