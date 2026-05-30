using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// Provides lifecycle callbacks invoked when a Cloudflare tunnel is created or destroyed.
/// Register implementations via <see cref="RegistrationExtensions.AddCloudflaredLifetimeHook{THook}"/>.
/// </summary>
/// <example>
/// <code>
/// public class MyHook : ICloudflaredLifetimeHook
/// {
///     public Task OnCreatedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
///     {
///         Console.WriteLine($"Tunnel created: {tunnel.PublicUrl}");
///         return Task.CompletedTask;
///     }
///
///     public Task OnDestroyedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
///     {
///         Console.WriteLine($"Tunnel destroyed: {tunnel.PublicUrl}");
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
public interface ICloudflaredLifetimeHook
{
    /// <summary>Called after a tunnel has been successfully started.</summary>
    Task OnCreatedAsync(TunnelInfo tunnel, CancellationToken cancellationToken);

    /// <summary>Called after a tunnel has been stopped.</summary>
    Task OnDestroyedAsync(TunnelInfo tunnel, CancellationToken cancellationToken);
}
