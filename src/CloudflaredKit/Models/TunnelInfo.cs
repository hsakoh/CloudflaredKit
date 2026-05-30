namespace CloudflaredKit.Models;

/// <summary>
/// Represents an active Cloudflare tunnel.
/// </summary>
public sealed class TunnelInfo
{
    /// <summary>
    /// Gets the public URL of the tunnel.
    /// For TryCloudflare mode, this is a temporary <c>https://xxxxx.trycloudflare.com</c> URL.
    /// For permanent tunnel mode, this is <see langword="null"/> because the URL is pre-configured in the Cloudflare dashboard.
    /// </summary>
    public string? PublicUrl { get; init; }
}
