using System.Net;
using System.Text;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class TosuServiceOwnershipTests
{
    [Fact]
    public async Task ExistingCompatibleServerIsAttachedWithoutLaunchingOrOwningAProcess()
    {
        var executableLookupCalled = false;
        var processStartCalled = false;
        using var client = new HttpClient(new StubHandler(
            "{\"state\":{\"name\":\"SelectPlay\",\"number\":1}}",
            analyzerAvailable: true));
        using var service = new TosuService(
            client,
            () =>
            {
                executableLookupCalled = true;
                return "unused-tosu.exe";
            },
            _ =>
            {
                processStartCalled = true;
                return null;
            });

        TosuStateChangedEventArgs? state = null;
        service.StateChanged += (_, args) => state = args;

        await service.StartAsync();

        Assert.True(service.IsRunning);
        Assert.Equal(TosuInstanceOwnership.External, service.Ownership);
        Assert.False(executableLookupCalled);
        Assert.False(processStartCalled);
        Assert.Equal(TosuConnectionState.Running, state?.State);
        Assert.Equal("status.tosu_connected_existing", state?.Message);

        service.Stop();

        Assert.False(service.IsRunning);
        Assert.Equal(TosuInstanceOwnership.None, service.Ownership);
        Assert.Equal(TosuConnectionState.Stopped, state?.State);
        Assert.Equal("status.tosu_disconnected", state?.Message);
    }

    [Fact]
    public async Task UnrecognizableServerDoesNotClaimExternalOwnership()
    {
        var executableLookupCalled = false;
        using var client = new HttpClient(new StubHandler("{}", analyzerAvailable: true));
        using var service = new TosuService(
            client,
            () =>
            {
                executableLookupCalled = true;
                return null;
            },
            _ => throw new InvalidOperationException("No process should be launched without an executable."));

        await service.StartAsync();

        Assert.True(executableLookupCalled);
        Assert.False(service.IsRunning);
        Assert.Equal(TosuInstanceOwnership.None, service.Ownership);
        Assert.Equal(TosuConnectionState.Unavailable, service.ConnectionState);
    }

    [Fact]
    public async Task ExistingApiWithoutAnalyzerIsNotKilledOrReplaced()
    {
        var executableLookupCalled = false;
        var processStartCalled = false;
        using var client = new HttpClient(new StubHandler(
            "{\"state\":{\"name\":\"SelectPlay\",\"number\":1}}",
            analyzerAvailable: false));
        using var service = new TosuService(
            client,
            () =>
            {
                executableLookupCalled = true;
                return "unused-tosu.exe";
            },
            _ =>
            {
                processStartCalled = true;
                return null;
            });

        await service.StartAsync();

        Assert.False(service.IsRunning);
        Assert.Equal(TosuInstanceOwnership.None, service.Ownership);
        Assert.Equal(TosuConnectionState.Unavailable, service.ConnectionState);
        Assert.False(executableLookupCalled);
        Assert.False(processStartCalled);
    }

    private sealed class StubHandler(string content, bool analyzerAvailable) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/ManiaMapAnalyser/")
            {
                return Task.FromResult(new HttpResponseMessage(
                    analyzerAvailable ? HttpStatusCode.OK : HttpStatusCode.NotFound));
            }

            Assert.Equal("/json/v2", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
