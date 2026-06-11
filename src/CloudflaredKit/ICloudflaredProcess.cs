using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// Manages the lifecycle of the <c>cloudflared</c> process.
/// </summary>
public interface ICloudflaredProcess
{
    /// <summary>
    /// Raised when the <c>cloudflared</c> process exits unexpectedly
    /// (i.e., not as a result of calling <see cref="StopAsync"/>).
    /// The argument is the process exit code.
    /// </summary>
    event Action<int>? UnexpectedlyExited;

    /// <summary>
    /// Starts the <c>cloudflared</c> process.
    /// In TryCloudflare mode, waits until the public URL is emitted and returns it.
    /// In permanent tunnel mode (token specified), returns with a <see langword="null"/> <see cref="TunnelInfo.PublicUrl"/>.
    /// </summary>
    Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills the <c>cloudflared</c> process if it is running.</summary>
    Task StopAsync();
}
