using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudflaredKit.Models;

namespace CloudflaredKit;

/// <summary>
/// Starts and manages the <c>cloudflared</c> child process.
/// Supports two modes:
/// <list type="bullet">
///   <item><b>TryCloudflare</b> (no token) – runs <c>cloudflared tunnel --url http://{host}:{port}</c>
///   and extracts the public URL from stderr.</item>
///   <item><b>Permanent tunnel</b> (token provided) – runs <c>cloudflared tunnel run --token {token}</c>.</item>
/// </list>
/// </summary>
public sealed partial class CloudflaredProcess : ICloudflaredProcess, IAsyncDisposable
{
    // cloudflared writes the TryCloudflare URL to stderr, e.g.:
    //   "Your quick Tunnel has been created! Visit it at https://abc-def-123.trycloudflare.com"
    [GeneratedRegex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com")]
    private static partial Regex TryCloudflareUrlRegex();

    private readonly IOptionsMonitor<CloudflaredOptions> _options;
    private readonly ICloudflaredDownloader _downloader;
    private readonly ILogger<CloudflaredProcess> _logger;

    private Process? _process;

    /// <summary>Initializes a new instance of <see cref="CloudflaredProcess"/>.</summary>
    public CloudflaredProcess(
        IOptionsMonitor<CloudflaredOptions> options,
        ICloudflaredDownloader downloader,
        ILogger<CloudflaredProcess> logger)
    {
        _options = options;
        _downloader = downloader;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TunnelInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        // Kill any leftover process from a previous Start/Stop cycle.
        await StopAsync();

        var options = _options.CurrentValue;

        // Resolve executable path: use the user-supplied path or auto-download.
        var executablePath = options.CloudflaredPath is not null
            ? options.CloudflaredPath
            : await _downloader.EnsureExecutableAsync(cancellationToken);

        bool isTryCloudflare;
        var arguments = new List<string>();
        string redactedArguments;

        if (options.TunnelToken is null)
        {
            // TryCloudflare mode: no account needed, temporary URL issued at runtime.
            arguments.AddRange(["tunnel", "--url", $"http://{options.LocalHostName}:{options.LocalPort}"]);
            redactedArguments = string.Join(' ', arguments);
            isTryCloudflare = true;
        }
        else
        {
            // Permanent tunnel mode: a Cloudflare Zero Trust tunnel token is required.
            arguments.AddRange(["tunnel", "--no-autoupdate", "run", "--token", options.TunnelToken]);
            redactedArguments = "tunnel --no-autoupdate run --token <redacted>";
            isTryCloudflare = false;
        }

        var urlSource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitSource = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogTrace("[cloudflared stdout] {Line}", e.Data);
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            _logger.LogTrace("[cloudflared stderr] {Line}", e.Data);

            if (isTryCloudflare)
            {
                var match = TryCloudflareUrlRegex().Match(e.Data);
                if (match.Success)
                {
                    urlSource.TrySetResult(match.Value);
                }
            }
        };

        _process.Exited += (sender, _) =>
        {
            _logger.LogWarning("cloudflared process exited unexpectedly");
            var exitCode = sender is Process process ? process.ExitCode : -1;
            exitSource.TrySetResult(exitCode);
            urlSource.TrySetException(
                new InvalidOperationException("cloudflared process exited before a URL was reported."));
        };

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch
        {
            _process.Dispose();
            _process = null;
            throw;
        }

        _logger.LogInformation("cloudflared started (PID={Pid}, args={Args})", _process.Id, redactedArguments);

        if (!isTryCloudflare)
        {
            await EnsurePermanentTunnelKeepsRunningAsync(exitSource.Task, cancellationToken);

            // For permanent tunnels the URL is pre-configured in the dashboard; nothing to parse.
            return new TunnelInfo { PublicUrl = null };
        }

        // Wait for cloudflared to emit the trycloudflare.com URL (typically within a few seconds).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var publicUrl = await urlSource.Task.WaitAsync(timeoutCts.Token);
            _logger.LogInformation("TryCloudflare tunnel URL: {Url}", publicUrl);
            return new TunnelInfo { PublicUrl = publicUrl };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync();

            // The 30-second timeout fired, not the caller's token.
            throw new TimeoutException(
                "Timed out waiting for cloudflared to emit a TryCloudflare URL. " +
                "Check that the local port is reachable and cloudflared is not blocked by a firewall.");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            _process = null;
            return;
        }

        _logger.LogInformation("Stopping cloudflared process (PID={Pid})", _process.Id);

        try
        {
            // Kill the entire process tree to avoid orphaned child processes on Linux.
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Waits briefly after starting a permanent tunnel so immediate authentication or
    /// configuration failures are reported before the service marks the tunnel as active.
    /// </summary>
    private async Task EnsurePermanentTunnelKeepsRunningAsync(
        Task<int> exitTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var startupDelay = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var completedTask = await Task.WhenAny(exitTask, startupDelay);

            if (completedTask == exitTask)
            {
                await StopAsync();
                throw new InvalidOperationException(
                    $"cloudflared process exited during startup with exit code {await exitTask}.");
            }

            await startupDelay;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }
}
