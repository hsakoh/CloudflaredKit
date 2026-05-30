using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CloudflaredKit;

/// <summary>
/// Configuration options for the Cloudflare tunnel service.
/// </summary>
public sealed class CloudflaredOptions
{
    /// <summary>
    /// Gets or sets the local port to expose via the tunnel. Default is <c>80</c>.
    /// Used only in TryCloudflare mode (when <see cref="TunnelToken"/> is <see langword="null"/>).
    /// </summary>
    public int LocalPort { get; set; } = 80;

    /// <summary>
    /// Gets or sets the local host name or IP address to expose via the tunnel. Default is <c>localhost</c>.
    /// Used only in TryCloudflare mode (when <see cref="TunnelToken"/> is <see langword="null"/>).
    /// </summary>
    public string LocalHostName { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the Cloudflare tunnel token for permanent tunnels.
    /// Obtain this from the Cloudflare Zero Trust dashboard.
    /// When <see langword="null"/>, TryCloudflare mode is used: no account is needed and a
    /// temporary <c>https://xxxxx.trycloudflare.com</c> URL is issued at runtime.
    /// </summary>
    public string? TunnelToken { get; set; }

    /// <summary>
    /// Gets or sets the absolute path to an existing <c>cloudflared</c> executable.
    /// When <see langword="null"/>, the binary is automatically downloaded from GitHub Releases
    /// and cached in <c>%LOCALAPPDATA%/TryCloudflare/</c>.
    /// </summary>
    public string? CloudflaredPath { get; set; }

    /// <summary>
    /// Gets or sets the directory where the downloaded <c>cloudflared</c> binary is cached.
    /// When <see langword="null"/>, the platform default cache location is used.
    /// </summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>
/// Extension methods for registering TryCloudflare services with the DI container.
/// </summary>
public static class RegistrationExtensions
{
    /// <summary>
    /// Registers TryCloudflare services and binds options from an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Cloudflare": { "LocalPort": 5000 } }
    /// services.AddTryCloudflare(configuration.GetSection("Cloudflare"));
    /// </code>
    /// </example>
    public static IServiceCollection AddTryCloudflare(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddTryCloudflareCore(services)
            .Bind(configuration);

        return services;
    }

    /// <summary>
    /// Registers TryCloudflare services with an optional inline configuration delegate.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddTryCloudflare(options =>
    /// {
    ///     options.LocalPort = 5000;
    ///     // options.TunnelToken = "my-token"; // omit for TryCloudflare (temporary URL) mode
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddTryCloudflare(
        this IServiceCollection services,
        Action<CloudflaredOptions>? configure = null)
    {
        var builder = AddTryCloudflareCore(services);

        if (configure is not null)
        {
            builder.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="ICloudflaredLifetimeHook"/> implementation.
    /// </summary>
    public static IServiceCollection AddCloudflaredLifetimeHook<THook>(
        this IServiceCollection services)
        where THook : class, ICloudflaredLifetimeHook
    {
        services.AddSingleton<ICloudflaredLifetimeHook, THook>();
        return services;
    }

    /// <summary>
    /// Core DI registrations shared by all <c>AddTryCloudflare</c> overloads.
    /// </summary>
    private static OptionsBuilder<CloudflaredOptions> AddTryCloudflareCore(IServiceCollection services)
    {
        services.AddLogging();

        // HttpClient for CloudflaredDownloader, configured to follow GitHub Releases redirects.
        // GitHub's "latest" release URL redirects to the versioned asset URL, so AllowAutoRedirect must be true (default).
        services.AddHttpClient<ICloudflaredDownloader, CloudflaredDownloader>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TryCloudflare/1.0 (github.com/cloudflare/cloudflared)");
            client.Timeout = TimeSpan.FromMinutes(5); // binary download may take a while on slow connections
        });

        services.AddSingleton<ICloudflaredProcess, CloudflaredProcess>();
        services.AddSingleton<ICloudflaredService, CloudflaredService>();

        return services.AddOptions<CloudflaredOptions>();
    }
}
