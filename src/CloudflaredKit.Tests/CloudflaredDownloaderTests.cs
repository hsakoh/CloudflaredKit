using System.Net;
using System.Runtime.InteropServices;
using CloudflaredKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudflaredKit.Tests;

/// <summary>
/// Unit tests for <see cref="CloudflaredDownloader"/>.
/// </summary>
public sealed class CloudflaredDownloaderTests
{
    [Fact]
    public void GetCachedExecutablePath_ReturnsPathUnderLocalAppData()
    {
        var path = CloudflaredDownloader.GetCachedExecutablePath();

        Assert.Contains("TryCloudflare", path);
        Assert.True(
            path.EndsWith("cloudflared.exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("cloudflared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureExecutableAsync_WhenAlreadyCached_DoesNotDownload()
    {
        var cacheDir = CreateTemporaryCacheDirectory();

        try
        {
            var exePath = CloudflaredDownloader.GetCachedExecutablePath(cacheDir);
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(exePath, "fake-binary");

            // The HttpClient handler will throw if called, proving no download occurs.
            var handler = new ThrowingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var sut = new CloudflaredDownloader(
                httpClient,
                NullLogger<CloudflaredDownloader>.Instance,
                new StaticOptionsMonitor(new CloudflaredOptions { CacheDirectory = cacheDir }));

            // Act & Assert: should not throw even though the handler would throw.
            var result = await sut.EnsureExecutableAsync();
            Assert.Equal(exePath, result);
        }
        finally
        {
            DeleteDirectoryIfExists(cacheDir);
        }
    }

    [Fact]
    public async Task EnsureExecutableAsync_WhenDownloadFails_ThrowsAndDoesNotLeavePartialFile()
    {
        var cacheDir = CreateTemporaryCacheDirectory();

        try
        {
            var exePath = CloudflaredDownloader.GetCachedExecutablePath(cacheDir);
            var handler = new FailingHttpMessageHandler(HttpStatusCode.InternalServerError);
            using var httpClient = new HttpClient(handler);
            var sut = new CloudflaredDownloader(
                httpClient,
                NullLogger<CloudflaredDownloader>.Instance,
                new StaticOptionsMonitor(new CloudflaredOptions { CacheDirectory = cacheDir }));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(
                () => sut.EnsureExecutableAsync());

            // The partial .tmp file must be cleaned up.
            Assert.False(File.Exists(exePath + ".tmp"));
        }
        finally
        {
            DeleteDirectoryIfExists(cacheDir);
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string CreateTemporaryCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "CloudflaredKit.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class StaticOptionsMonitor(CloudflaredOptions options) : IOptionsMonitor<CloudflaredOptions>
    {
        public CloudflaredOptions CurrentValue => options;

        public CloudflaredOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<CloudflaredOptions, string?> listener) => null;
    }

    /// <summary>An HttpMessageHandler that always throws; used to verify no HTTP call is made.</summary>
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP call should not have been made.");
    }

    /// <summary>An HttpMessageHandler that returns a fixed failing status code.</summary>
    private sealed class FailingHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
