# CloudflaredKit

[![Build & Test](https://github.com/Hsakoh/CloudflaredKit/actions/workflows/build.yml/badge.svg)](https://github.com/Hsakoh/CloudflaredKit/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/CloudflaredKit.svg)](https://www.nuget.org/packages/CloudflaredKit)

A .NET library that manages the [`cloudflared`](https://github.com/cloudflare/cloudflared) binary and Cloudflare Tunnel lifecycle programmatically.  
Inspired by [FluffySpoon.Ngrok](https://github.com/ffMathy/FluffySpoon.Ngrok).

## Features

- **TryCloudflare mode** — No account or pre-configuration needed. Issues a random `https://xxxxx.trycloudflare.com` URL at startup.
- **Permanent tunnel mode** — Specify a Cloudflare Zero Trust tunnel token to run with a fixed URL.
- **Automatic binary management** — Downloads and caches the `cloudflared` binary from GitHub Releases.
- **DI support** — Works with `Microsoft.Extensions.DependencyInjection` (console apps, generic host, etc.).
- **Lifetime hooks** — `ICloudflaredLifetimeHook` callbacks fired on tunnel creation and destruction.

## Supported Platforms

| OS      | Architecture                |
| ------- | --------------------------- |
| Linux   | x64                         |
| Linux   | x86 (32-bit)                |
| Linux   | arm64                       |
| Linux   | arm (32-bit, armhf / armel) |
| macOS   | x64                         |
| macOS   | arm64 (Apple Silicon)       |
| Windows | x64                         |
| Windows | x86 (32-bit)                |

## Installation

```sh
dotnet add package CloudflaredKit
```

## Usage

### Console application (TryCloudflare mode)

No account or pre-configuration required. Just specify the local port and a temporary public URL is issued automatically.

```csharp
using CloudflaredKit;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddTryCloudflare(options =>
{
    options.LocalPort = 5000;
});

var provider = services.BuildServiceProvider();
var service = provider.GetRequiredService<ICloudflaredService>();

// Downloads cloudflared on first run, then starts the tunnel.
var tunnel = await service.StartAsync();
Console.WriteLine($"Public URL: {tunnel.PublicUrl}");
// => Public URL: https://abc-def-123.trycloudflare.com

// ... application logic ...

await service.StopAsync();
```

### Permanent tunnel mode (tunnel token)

Create a tunnel in the [Cloudflare Zero Trust dashboard](https://one.dash.cloudflare.com/) and pass the generated token.

```csharp
services.AddTryCloudflare(options =>
{
    options.TunnelToken = "your-cloudflare-tunnel-token";
});
```

In permanent tunnel mode, the public URL is managed in the Cloudflare dashboard,
so `TunnelInfo.PublicUrl` is `null`.

### Configuration via appsettings.json

```json
{
  "Cloudflare": {
    "LocalPort": 5000
  }
}
```

```csharp
builder.Services.AddTryCloudflare(builder.Configuration.GetSection("Cloudflare"));
```

### Lifetime hooks

Implement `ICloudflaredLifetimeHook` to run custom logic whenever a tunnel is created or destroyed.
This is useful when something outside the tunnel startup flow needs to react to the public URL —
for example, registering it with a remote service.

```csharp
public class MyHook : ICloudflaredLifetimeHook
{
    public async Task OnCreatedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
    {
        // Called after the tunnel is up. tunnel.PublicUrl contains the public URL.
        await RegisterSomewhereAsync(tunnel.PublicUrl, cancellationToken);
    }

    public async Task OnDestroyedAsync(TunnelInfo tunnel, CancellationToken cancellationToken)
    {
        // Called after the tunnel is stopped.
        await UnregisterSomewhereAsync(tunnel.PublicUrl, cancellationToken);
    }
}
```

```csharp
services.AddTryCloudflare(options => options.LocalPort = 5000);
services.AddCloudflaredLifetimeHook<MyHook>();
```

Hooks are optional. If you only need the URL at the call site, use the return value of `StartAsync` instead.

### Waiting for the tunnel to be ready

`StartAsync` already blocks until the tunnel is up and returns `TunnelInfo` directly,
so in most cases you do not need `WaitUntilReadyAsync`.

```csharp
// Normal usage — URL is available immediately after await.
var tunnel = await service.StartAsync();
Console.WriteLine(tunnel.PublicUrl);
```

`WaitUntilReadyAsync` is only needed when `StartAsync` is called without being awaited
(fire-and-forget), and a separate part of the code needs to wait for readiness.

```csharp
// Fire-and-forget start (unusual).
_ = service.StartAsync();

// Somewhere else, wait until the tunnel is ready.
await service.WaitUntilReadyAsync();
Console.WriteLine(service.ActiveTunnel?.PublicUrl);
```

### Using a pre-installed cloudflared binary

Skip the automatic download and point to an existing binary.

```csharp
services.AddTryCloudflare(options =>
{
    options.LocalPort = 5000;
    options.CloudflaredPath = "/usr/local/bin/cloudflared";
});
```

### Selecting the local host name

By default, TryCloudflare forwards to `http://localhost:{LocalPort}`.
If your local server is bound to IPv4 loopback only, or `localhost` resolves to an address your server is not listening on, specify `127.0.0.1` explicitly.

```csharp
services.AddTryCloudflare(options =>
{
    options.LocalPort = 5000;
    options.LocalHostName = "127.0.0.1";
});
```

## Options

| Property          | Type      | Default     | Description                                                                                                                 |
| ----------------- | --------- | ----------- | --------------------------------------------------------------------------------------------------------------------------- |
| `LocalPort`       | `int`     | `80`        | Local port to expose (TryCloudflare mode only)                                                                              |
| `LocalHostName`   | `string`  | `localhost` | Local host name or IP address to expose (TryCloudflare mode only)                                                           |
| `TunnelToken`     | `string?` | `null`      | Cloudflare tunnel token. When `null`, TryCloudflare mode is used                                                            |
| `CloudflaredPath` | `string?` | `null`      | Path to an existing `cloudflared` binary. When `null`, auto-downloaded                                                      |
| `CacheDirectory`  | `string?` | `null`      | Directory used to cache the downloaded `cloudflared` binary. When `null`, the platform default cache location below is used |

## Binary Cache Location

| OS      | Path                                           |
| ------- | ---------------------------------------------- |
| Windows | `%LOCALAPPDATA%\TryCloudflare\cloudflared.exe` |
| Linux   | `~/.local/share/TryCloudflare/cloudflared`     |
| macOS   | `~/Library/Caches/TryCloudflare/cloudflared`   |

## Running E2E Tests Locally

TryCloudflare E2E tests can be run locally without a Cloudflare account. They download `cloudflared`, open a temporary public tunnel, and verify that traffic reaches a local test server.

```powershell
$env:CLOUDFLARED_E2E = "1"
dotnet test .\src\CloudflaredKit.Tests\CloudflaredKit.Tests.csproj -c Release --filter "FullyQualifiedName~CloudflaredE2ETests"
```

Permanent tunnel E2E tests are skipped unless `CLOUDFLARED_PERMANENT_E2E=1` and `CLOUDFLARE_TUNNEL_TOKEN` are also set.

## License

MIT
