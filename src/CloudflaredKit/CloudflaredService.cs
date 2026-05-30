using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// Default implementation of <see cref="ICloudflaredService"/>.
/// Coordinates binary download, process management, and lifetime hooks.
/// </summary>
public sealed class CloudflaredService : ICloudflaredService
{
    private readonly ICloudflaredDownloader _downloader;
    private readonly ICloudflaredProcess _process;
    private readonly IEnumerable<ICloudflaredLifetimeHook> _hooks;
    private readonly ILogger<CloudflaredService> _logger;
    private readonly IOptionsMonitor<CloudflaredOptions> _options;

    private bool _isInitialized;
    private TaskCompletionSource<TunnelInfo> _readySource = CreateReadySource();

    /// <inheritdoc/>
    public TunnelInfo? ActiveTunnel { get; private set; }

    /// <summary>Initializes a new instance of <see cref="CloudflaredService"/>.</summary>
    public CloudflaredService(
        ICloudflaredDownloader downloader,
        ICloudflaredProcess process,
        IEnumerable<ICloudflaredLifetimeHook> hooks,
        ILogger<CloudflaredService> logger,
        IOptionsMonitor<CloudflaredOptions> options)
    {
        _downloader = downloader;
        _process = process;
        _hooks = hooks;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        _isInitialized = true;

        if (_options.CurrentValue.CloudflaredPath is null)
        {
            // Pre-warm the binary cache so StartAsync does not block on download.
            await _downloader.EnsureExecutableAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_readySource.Task.IsCompleted)
        {
            _readySource = CreateReadySource();
        }

        TunnelInfo tunnel;
        try
        {
            tunnel = await _process.StartAsync(cancellationToken);
            ActiveTunnel = tunnel;
            _readySource.TrySetResult(tunnel);
        }
        catch (Exception ex)
        {
            _readySource.TrySetException(ex);
            throw;
        }

        await InvokeHooksAsync(
            hook => hook.OnCreatedAsync(tunnel, cancellationToken),
            nameof(ICloudflaredLifetimeHook.OnCreatedAsync));

        return tunnel;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var tunnel = ActiveTunnel;
        ActiveTunnel = null;

        await _process.StopAsync();

        if (tunnel is not null)
        {
            await InvokeHooksAsync(
                hook => hook.OnDestroyedAsync(tunnel, cancellationToken),
                nameof(ICloudflaredLifetimeHook.OnDestroyedAsync));
        }

        _readySource = CreateReadySource();
    }

    /// <inheritdoc/>
    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveTunnel is not null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await _readySource.Task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<TunnelInfo> CreateReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Invokes all registered lifetime hooks, logging (but not re-throwing) any hook exceptions
    /// so that a single misbehaving hook cannot block the others.
    /// </summary>
    private async Task InvokeHooksAsync(
        Func<ICloudflaredLifetimeHook, Task> action,
        string hookMethodName)
    {
        await Task.WhenAll(_hooks.Select(async hook =>
        {
            try
            {
                await action(hook);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Lifetime hook {HookMethod} threw an exception in {HookType}",
                    hookMethodName,
                    hook.GetType().Name);
            }
        }));
    }
}
