using CloudflaredKit;
using CloudflaredKit.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CloudflaredKit.Tests;

/// <summary>
/// Verifies that <see cref="RegistrationExtensions"/> correctly registers all required
/// services in the DI container.
/// </summary>
public sealed class RegistrationExtensionsTests
{
    [Fact]
    public void AddTryCloudflare_WithDelegate_RegistersICloudflaredService()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare(options => options.LocalPort = 5000);
        var provider = services.BuildServiceProvider();

        var service = provider.GetService<ICloudflaredService>();

        Assert.NotNull(service);
    }

    [Fact]
    public void AddTryCloudflare_WithoutConfigure_UsesDefaultOptions()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudflaredOptions>>().Value;

        Assert.Equal(80, options.LocalPort);
        Assert.Equal("localhost", options.LocalHostName);
        Assert.Null(options.TunnelToken);
        Assert.Null(options.CloudflaredPath);
    }

    [Fact]
    public void AddTryCloudflare_MultipleRegistrations_ResolveSameInstance()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare();
        var provider = services.BuildServiceProvider();

        // ICloudflaredService is registered as Singleton.
        var a = provider.GetRequiredService<ICloudflaredService>();
        var b = provider.GetRequiredService<ICloudflaredService>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddCloudflaredLifetimeHook_RegistersHook()
    {
        var services = new ServiceCollection();
        services.AddTryCloudflare();
        services.AddCloudflaredLifetimeHook<NoOpHook>();
        var provider = services.BuildServiceProvider();

        var hooks = provider.GetServices<ICloudflaredLifetimeHook>().ToList();

        Assert.Contains(hooks, h => h is NoOpHook);
    }

    private sealed class NoOpHook : ICloudflaredLifetimeHook
    {
        public Task OnCreatedAsync(TunnelInfo tunnel, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnDestroyedAsync(TunnelInfo tunnel, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
