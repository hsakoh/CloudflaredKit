using CloudflaredKit;
using CloudflaredKit.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudflaredKit.Tests;

/// <summary>
/// Unit tests for <see cref="CloudflaredService"/>.
/// These tests verify the service orchestrates the downloader, process, and lifecycle hooks
/// correctly, without starting a real cloudflared process.
/// </summary>
public sealed class CloudflaredServiceTests
{
    private readonly ICloudflaredDownloader _downloader = Substitute.For<ICloudflaredDownloader>();
    private readonly ICloudflaredProcess _process = Substitute.For<ICloudflaredProcess>();
    private readonly ICloudflaredLifetimeHook _hook = Substitute.For<ICloudflaredLifetimeHook>();
    private readonly IOptionsMonitor<CloudflaredOptions> _options = Substitute.For<IOptionsMonitor<CloudflaredOptions>>();
    private readonly CloudflaredService _sut;

    public CloudflaredServiceTests()
    {
        _options.CurrentValue.Returns(new CloudflaredOptions());
        _sut = new CloudflaredService(
            _downloader,
            _process,
            [_hook],
            NullLogger<CloudflaredService>.Instance,
            _options);
    }

    [Fact]
    public async Task InitializeAsync_CallsDownloaderOnce()
    {
        await _sut.InitializeAsync();

        await _downloader.Received(1).EnsureExecutableAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_DownloadsOnlyOnce()
    {
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();

        await _downloader.Received(1).EnsureExecutableAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_WhenCloudflaredPathIsConfigured_DoesNotDownload()
    {
        var options = Substitute.For<IOptionsMonitor<CloudflaredOptions>>();
        options.CurrentValue.Returns(new CloudflaredOptions { CloudflaredPath = "existing-cloudflared" });
        var sut = new CloudflaredService(
            _downloader,
            _process,
            [_hook],
            NullLogger<CloudflaredService>.Instance,
            options);

        await sut.InitializeAsync();

        await _downloader.DidNotReceive().EnsureExecutableAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ReturnsTunnelAndSetsActiveTunnel()
    {
        var expected = new TunnelInfo { PublicUrl = "https://example.trycloudflare.com" };
        _process.StartAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.StartAsync();

        Assert.Equal(expected.PublicUrl, result.PublicUrl);
        Assert.NotNull(_sut.ActiveTunnel);
        Assert.Equal(expected.PublicUrl, _sut.ActiveTunnel!.PublicUrl);
    }

    [Fact]
    public async Task StartAsync_InvokesOnCreatedHook()
    {
        var tunnel = new TunnelInfo { PublicUrl = "https://example.trycloudflare.com" };
        _process.StartAsync(Arg.Any<CancellationToken>()).Returns(tunnel);

        await _sut.StartAsync();

        await _hook.Received(1).OnCreatedAsync(
            Arg.Is<TunnelInfo>(t => t.PublicUrl == tunnel.PublicUrl),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_ClearsActiveTunnelAndInvokesOnDestroyedHook()
    {
        var tunnel = new TunnelInfo { PublicUrl = "https://example.trycloudflare.com" };
        _process.StartAsync(Arg.Any<CancellationToken>()).Returns(tunnel);
        await _sut.StartAsync();

        await _sut.StopAsync();

        Assert.Null(_sut.ActiveTunnel);
        await _hook.Received(1).OnDestroyedAsync(
            Arg.Is<TunnelInfo>(t => t.PublicUrl == tunnel.PublicUrl),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_WhenNoActiveTunnel_DoesNotInvokeHook()
    {
        await _sut.StopAsync();

        await _hook.DidNotReceive().OnDestroyedAsync(
            Arg.Any<TunnelInfo>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_HookThrows_DoesNotPropagateException()
    {
        var tunnel = new TunnelInfo { PublicUrl = "https://example.trycloudflare.com" };
        _process.StartAsync(Arg.Any<CancellationToken>()).Returns(tunnel);
        _hook.OnCreatedAsync(Arg.Any<TunnelInfo>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("hook failure"));

        // Should not throw even though the hook throws.
        var result = await _sut.StartAsync();

        Assert.Equal(tunnel.PublicUrl, result.PublicUrl);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ReturnsWhenActiveTunnelIsSet()
    {
        var tunnel = new TunnelInfo { PublicUrl = "https://example.trycloudflare.com" };
        _process.StartAsync(Arg.Any<CancellationToken>()).Returns(tunnel);

        // Start in background, then wait.
        var startTask = _sut.StartAsync();
        await _sut.WaitUntilReadyAsync();
        await startTask;

        Assert.NotNull(_sut.ActiveTunnel);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_CancellationToken_RespectsCancel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should not throw OperationCanceledException and should return quickly
        // because the cancellation is checked in the while condition.
        await _sut.WaitUntilReadyAsync(cts.Token);
    }
}
