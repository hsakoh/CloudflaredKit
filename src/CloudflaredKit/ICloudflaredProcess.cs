using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// Manages the lifecycle of the <c>cloudflared</c> process.
/// </summary>
public interface ICloudflaredProcess
{
    /// <summary>
    /// Starts the <c>cloudflared</c> process.
    /// In TryCloudflare mode, waits until the public URL is emitted and returns it.
    /// In permanent tunnel mode (token specified), returns with a <see langword="null"/> <see cref="TunnelInfo.PublicUrl"/>.
    /// </summary>
    Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills the <c>cloudflared</c> process if it is running.</summary>
    Task StopAsync();
}
