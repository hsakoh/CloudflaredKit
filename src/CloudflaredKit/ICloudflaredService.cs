using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// High-level service that coordinates binary download, process management, and lifetime hooks
/// for a Cloudflare tunnel.
/// </summary>
/// <example>
/// <code>
/// var services = new ServiceCollection();
/// services.AddTryCloudflare(options => options.LocalPort = 5000);
///
/// var provider = services.BuildServiceProvider();
/// var service = provider.GetRequiredService&lt;ICloudflaredService&gt;();
///
/// await service.InitializeAsync();      // pre-downloads binary (optional)
/// var tunnel = await service.StartAsync();
/// Console.WriteLine(tunnel.PublicUrl);  // TryCloudflare mode: https://xxxxx.trycloudflare.com
///
/// await service.StopAsync();
/// </code>
/// </example>
public interface ICloudflaredService
{
    /// <summary>Gets the currently active tunnel, or <see langword="null"/> if no tunnel is running.</summary>
    TunnelInfo? ActiveTunnel { get; }

    /// <summary>
    /// Raised when the cloudflared process exits unexpectedly
    /// (i.e., not as a result of calling <see cref="StopAsync"/>).
    /// The argument is the process exit code.
    /// Subscribers are responsible for deciding whether and how to restart the tunnel.
    /// </summary>
    event Action<int>? TunnelExitedUnexpectedly;

    /// <summary>
    /// Pre-downloads the <c>cloudflared</c> binary so that <see cref="StartAsync"/> is faster.
    /// Calling this method is optional; <see cref="StartAsync"/> will download on demand if needed.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the tunnel and returns the <see cref="TunnelInfo"/> once it is ready.
    /// Invokes all registered <see cref="ICloudflaredLifetimeHook.OnCreatedAsync"/> hooks.
    /// </summary>
    Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the tunnel and invokes all registered <see cref="ICloudflaredLifetimeHook.OnDestroyedAsync"/> hooks.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits asynchronously until <see cref="ActiveTunnel"/> is non-null (i.e., the tunnel is ready).
    /// </summary>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
}
