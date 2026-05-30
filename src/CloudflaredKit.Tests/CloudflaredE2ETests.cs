using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CloudflaredKit.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CloudflaredKit.Tests;

/// <summary>
/// Marks a test as an E2E test that requires network access and cloudflared binary download.
/// Set the environment variable <c>CLOUDFLARED_E2E=1</c> to enable these tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLOUDFLARED_E2E") != "1")
            Skip = "E2E test — set CLOUDFLARED_E2E=1 to run";
    }
}

/// <summary>
/// Marks a test as a permanent-tunnel E2E test.
/// Requires <c>CLOUDFLARED_E2E=1</c>, <c>CLOUDFLARED_PERMANENT_E2E=1</c>, and
/// <c>CLOUDFLARE_TUNNEL_TOKEN</c> to be set.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PermanentTunnelFactAttribute : FactAttribute
{
    public PermanentTunnelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLOUDFLARED_E2E") != "1")
            Skip = "E2E test — set CLOUDFLARED_E2E=1 to run";
        else if (Environment.GetEnvironmentVariable("CLOUDFLARED_PERMANENT_E2E") != "1")
            Skip = "Permanent tunnel E2E test — set CLOUDFLARED_PERMANENT_E2E=1 to run";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_TOKEN")))
            Skip = "Permanent tunnel E2E test — set CLOUDFLARE_TUNNEL_TOKEN to run";
    }
}

/// <summary>
/// Serializes E2E tests because they share a local port and create external tunnels.
/// </summary>
[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollectionDefinition;

/// <summary>
/// End-to-end tests that start a real cloudflared tunnel and verify actual HTTP traffic.
/// These tests download the cloudflared binary and establish a TryCloudflare tunnel.
/// Run with <c>CLOUDFLARED_E2E=1</c> environment variable.
/// </summary>
[Collection("E2E")]
public sealed class CloudflaredE2ETests
{
    private const int TestPort = 15678;

    private static readonly Regex PublicUrlPattern =
        new(@"^https://[a-zA-Z0-9-]+\.trycloudflare\.com$", RegexOptions.Compiled);

    /// <summary>
    /// StartAsync returns a trycloudflare.com URL and sets ActiveTunnel.
    /// After StopAsync, ActiveTunnel is cleared.
    /// </summary>
    [E2EFact]
    public async Task StartAsync_ReturnsTrycloudflareUrlAndSetsActiveTunnel()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare(options => options.LocalPort = TestPort);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tunnel = await service.StartAsync(cts.Token);

        Assert.NotNull(tunnel.PublicUrl);
        Assert.Matches(PublicUrlPattern, tunnel.PublicUrl);
        Assert.Equal(tunnel.PublicUrl, service.ActiveTunnel?.PublicUrl);

        await service.StopAsync();

        Assert.Null(service.ActiveTunnel);
    }

    /// <summary>
    /// HTTP requests to the public URL are routed through cloudflared to the local server.
    /// </summary>
    [E2EFact]
    public async Task StartAsync_TunnelRoutesTrafficToLocalServer()
    {
        const string expectedContent = "cloudflared-ok";
        await using var server = new SimpleHttpServer(TestPort, expectedContent);

        var services = new ServiceCollection();
        services.AddTryCloudflare(options =>
        {
            options.LocalPort = TestPort;
            options.LocalHostName = "127.0.0.1";
        });
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tunnel = await service.StartAsync(cts.Token);
        Assert.NotNull(tunnel.PublicUrl);

        try
        {
            await AssertUrlReachableAsync($"http://127.0.0.1:{TestPort}/", expectedContent);
            await AssertUrlReachableAsync(
                tunnel.PublicUrl,
                expectedContent,
                () => $"Local server request count: {server.RequestCount}",
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    /// <summary>
    /// ICloudflaredLifetimeHook.OnCreatedAsync is called after the tunnel starts,
    /// and OnDestroyedAsync is called after StopAsync.
    /// </summary>
    [E2EFact]
    public async Task LifetimeHook_FiredOnCreateAndDestroy()
    {
        await using var server = new SimpleHttpServer(TestPort, "hook-test");

        var hook = new RecordingHook();
        var services = new ServiceCollection();
        services.AddTryCloudflare(options =>
        {
            options.LocalPort = TestPort;
            options.LocalHostName = "127.0.0.1";
        });
        services.AddSingleton<ICloudflaredLifetimeHook>(hook);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        Assert.Null(hook.CreatedTunnel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tunnel = await service.StartAsync(cts.Token);
        Assert.NotNull(tunnel.PublicUrl);

        try
        {
            Assert.NotNull(hook.CreatedTunnel);
            Assert.Equal(tunnel.PublicUrl, hook.CreatedTunnel.PublicUrl);
            Assert.False(hook.IsDestroyed);
        }
        finally
        {
            await service.StopAsync();
        }

        Assert.True(hook.IsDestroyed);
        Assert.Equal(tunnel.PublicUrl, hook.DestroyedTunnel?.PublicUrl);
    }

    /// <summary>
    /// InitializeAsync downloads the cloudflared binary and caches it to disk.
    /// </summary>
    [E2EFact]
    public async Task InitializeAsync_CachesBinaryToDisk()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare(options => options.LocalPort = TestPort);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await service.InitializeAsync(cts.Token);

        var path = CloudflaredDownloader.GetCachedExecutablePath();
        Assert.True(File.Exists(path), $"Binary not found at expected cache path: {path}");
    }

    // ---------------------------------------------------------------------------
    // Permanent tunnel tests
    // ---------------------------------------------------------------------------

    /// <summary>
    /// StartAsync with a real tunnel token completes without error and sets ActiveTunnel.
    /// The tunnel token is read from the CLOUDFLARE_TUNNEL_TOKEN environment variable.
    /// </summary>
    [PermanentTunnelFact]
    public async Task StartAsync_PermanentTunnel_CompletesWithoutError()
    {
        var token = Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_TOKEN")!;

        var services = new ServiceCollection();
        services.AddTryCloudflare(options => options.TunnelToken = token);
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tunnel = await service.StartAsync(cts.Token);

        Assert.NotNull(service.ActiveTunnel);

        await service.StopAsync();
        Assert.Null(service.ActiveTunnel);
    }

    /// <summary>
    /// When CLOUDFLARE_TUNNEL_URL is also set, verifies that HTTP traffic reaches the local server
    /// through the permanent tunnel.
    /// Requires CLOUDFLARED_E2E=1, CLOUDFLARED_PERMANENT_E2E=1,
    /// CLOUDFLARE_TUNNEL_TOKEN, and CLOUDFLARE_TUNNEL_URL.
    /// </summary>
    [PermanentTunnelFact]
    public async Task StartAsync_PermanentTunnel_RoutesTrafficToLocalServer()
    {
        var tunnelUrl = Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_URL");
        if (string.IsNullOrEmpty(tunnelUrl))
        {
            // Traffic verification requires the pre-configured public URL.
            return;
        }

        var token = Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_TOKEN")!;

        await using var server = new SimpleHttpServer(TestPort, "permanent-tunnel-ok");

        var services = new ServiceCollection();
        services.AddTryCloudflare(options =>
        {
            options.TunnelToken = token;
            options.LocalPort = TestPort;  // informational; routing is configured in the dashboard
        });
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICloudflaredService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await service.StartAsync(cts.Token);

        try
        {
            await AssertUrlReachableAsync(tunnelUrl, expectedContent: "permanent-tunnel-ok");
        }
        finally
        {
            await service.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Retries GET requests until the response body contains <paramref name="expectedContent"/>
    /// or the 90-second deadline is exceeded.
    /// </summary>
    private static async Task AssertUrlReachableAsync(
        string url,
        string expectedContent,
        Func<string>? getFailureDetails = null,
        TimeSpan? initialDelay = null)
    {
        await WaitForDnsReadinessAsync(url);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        string lastResult = "No response received.";

        if (initialDelay is not null)
        {
            await Task.Delay(initialDelay.Value);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                lastResult = $"Last response: {(int)response.StatusCode} {response.ReasonPhrase}; body: {Truncate(body, 500)}";

                if (response.IsSuccessStatusCode)
                {
                    if (body.Contains(expectedContent))
                        return;
                }
            }
            catch (HttpRequestException ex)
            {
                lastResult = $"Last HTTP error: {ex.Message}";
            }
            catch (TaskCanceledException ex)
            {
                lastResult = $"Last timeout/cancellation: {ex.Message}";
            }

            await Task.Delay(1000);
        }

        var details = getFailureDetails is null ? string.Empty : Environment.NewLine + getFailureDetails();
        Assert.Fail(
            $"Timeout: {url} did not return \"{expectedContent}\" within 90 seconds. " +
            lastResult + details);
    }

    /// <summary>
    /// TryCloudflare may return a public URL before the runner can resolve its DNS record.
    /// Wait for name resolution first so transient propagation lag does not fail the traffic test.
    /// </summary>
    private static async Task WaitForDnsReadinessAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                if (addresses.Length > 0)
                {
                    return;
                }
            }
            catch (SocketException)
            {
            }

            await Task.Delay(1000);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }

    // ---------------------------------------------------------------------------
    // Inner types
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Minimal HTTP server backed by <see cref="TcpListener"/> on IPv4 loopback only.
    /// Serves every incoming request with the same plain-text body.
    /// </summary>
    private sealed class SimpleHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _body;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public SimpleHttpServer(int port, string body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _loop = RunAsync(_cts.Token);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }

                _ = RespondAsync(client, ct);
            }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            using var _ = client;
            var bodyBytes = Encoding.UTF8.GetBytes(_body);
            var headerBytes = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n");

            var stream = client.GetStream();
            await stream.WriteAsync(headerBytes, ct);
            await stream.WriteAsync(bodyBytes, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Records the tunnel passed to each lifetime hook callback.</summary>
    private sealed class RecordingHook : ICloudflaredLifetimeHook
    {
        public TunnelInfo? CreatedTunnel { get; private set; }
        public TunnelInfo? DestroyedTunnel { get; private set; }
        public bool IsDestroyed { get; private set; }

        public Task OnCreatedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
        {
            CreatedTunnel = tunnel;
            return Task.CompletedTask;
        }

        public Task OnDestroyedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
        {
            DestroyedTunnel = tunnel;
            IsDestroyed = true;
            return Task.CompletedTask;
        }
    }
}
