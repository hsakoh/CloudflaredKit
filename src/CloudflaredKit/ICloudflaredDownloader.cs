namespace CloudflaredKit;

/// <summary>
/// Downloads and caches the <c>cloudflared</c> binary from GitHub Releases.
/// </summary>
public interface ICloudflaredDownloader
{
    /// <summary>
    /// Ensures the <c>cloudflared</c> executable is available on disk.
    /// Downloads from GitHub Releases if not already cached.
    /// </summary>
    /// <returns>The absolute path to the executable.</returns>
    Task<string> EnsureExecutableAsync(CancellationToken cancellationToken = default);
}
