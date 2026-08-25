using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class ComponentDownloaderTests
{
    [Fact]
    public async Task DownloadsToInjectedPathAndVerifiesDigest()
    {
        byte[] payload = Encoding.UTF8.GetBytes("component archive");
        string digest = Convert.ToHexString(SHA256.HashData(payload));
        using var root = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler(payload));
        var downloader = new ComponentDownloader(httpClient, "test-agent/1.0");
        var progress = new List<int>();
        string destination = Path.Combine(root.Path, "nested", "component.zip");

        await downloader.DownloadAndVerifyAsync(
            new GitHubAsset
            {
                Name = "component.zip",
                DownloadUrl = "https://example.test/component.zip",
                Digest = "sha256:" + digest
            },
            destination,
            progress: new Progress<int>(progress.Add));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Contains(100, progress);
    }

    [Fact]
    public async Task RejectsDigestMismatchAfterWritingTheArchive()
    {
        using var root = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler(Encoding.UTF8.GetBytes("payload")));
        var downloader = new ComponentDownloader(httpClient, "test-agent/1.0");
        string destination = Path.Combine(root.Path, "component.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAndVerifyAsync(
            new GitHubAsset
            {
                Name = "component.zip",
                DownloadUrl = "https://example.test/component.zip",
                Digest = "sha256:" + new string('0', 64)
            },
            destination));
        Assert.True(File.Exists(destination));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StubHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-agent/1.0", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ManiaMapAnalyzerOverlay-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
