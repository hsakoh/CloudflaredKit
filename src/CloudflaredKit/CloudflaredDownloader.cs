using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudflaredKit;

/// <summary>
/// Downloads the <c>cloudflared</c> binary from the official Cloudflare GitHub Releases page
/// and caches it to <c>%LOCALAPPDATA%/TryCloudflare/</c> (Windows),
/// <c>~/.local/share/TryCloudflare/</c> (Linux), or <c>~/Library/Caches/TryCloudflare/</c> (macOS).
/// Subsequent calls return the cached path without re-downloading.
/// </summary>
public sealed class CloudflaredDownloader : ICloudflaredDownloader
{
    // GitHub Releases provides plain binaries for Windows/Linux, and .tgz archives for macOS.
    private const string GitHubDownloadBase =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudflaredDownloader> _logger;
    private readonly IOptionsMonitor<CloudflaredOptions> _options;

    /// <summary>Initializes a new instance of <see cref="CloudflaredDownloader"/>.</summary>
    public CloudflaredDownloader(
        HttpClient httpClient,
        ILogger<CloudflaredDownloader> logger,
        IOptionsMonitor<CloudflaredOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc/>
    public async Task<string> EnsureExecutableAsync(CancellationToken cancellationToken = default)
    {
        var executablePath = GetCachedExecutablePath(_options.CurrentValue.CacheDirectory);

        if (File.Exists(executablePath))
        {
            _logger.LogTrace("cloudflared already cached at {Path}", executablePath);
            return executablePath;
        }

        var fileName = GetDownloadFileName();
        var downloadUrl = GitHubDownloadBase + fileName;

        _logger.LogInformation("Downloading cloudflared from {Url}", downloadUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);

        // Download to a temp file first to avoid partial writes on failure.
        var tempPath = executablePath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS release is a .tgz archive containing the cloudflared binary.
                await using var tgzStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await ExtractFromTgzAsync(tgzStream, tempPath, cancellationToken);
            }
            else
            {
                await using var fileStream = File.Create(tempPath);
                await response.Content.CopyToAsync(fileStream, cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        // On Linux/macOS, the binary must be executable (chmod +x).
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(
                tempPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        File.Move(tempPath, executablePath, overwrite: true);

        _logger.LogInformation("cloudflared downloaded to {Path}", executablePath);
        return executablePath;
    }

    /// <summary>Returns the local path where <c>cloudflared</c> is cached.</summary>
    public static string GetCachedExecutablePath(string? cacheDirectory = null)
    {
        string cacheDir;

        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            cacheDir = cacheDirectory;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TryCloudflare");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS conventional cache location: ~/Library/Caches/
            cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "TryCloudflare");
        }
        else
        {
            cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TryCloudflare");
        }

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cloudflared.exe"
            : "cloudflared";

        return Path.Combine(cacheDir, fileName);
    }

    /// <summary>Returns the filename to download from GitHub Releases for the current OS/architecture.</summary>
    private static string GetDownloadFileName()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return arch switch
            {
                Architecture.X64 => "cloudflared-windows-amd64.exe",
                Architecture.X86 => "cloudflared-windows-386.exe",
                _ => throw new CloudflaredUnsupportedException(arch)
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return arch switch
            {
                Architecture.X64   => "cloudflared-linux-amd64",
                Architecture.X86   => "cloudflared-linux-386",
                Architecture.Arm64 => "cloudflared-linux-arm64",
                // ARM 32-bit: distinguish armhf (hard-float) from armel (soft-float) via ELF header.
                Architecture.Arm   => IsArmHardFloat() ? "cloudflared-linux-armhf" : "cloudflared-linux-arm",
                _ => throw new CloudflaredUnsupportedException(arch)
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return arch switch
            {
                Architecture.X64   => "cloudflared-darwin-amd64.tgz",
                Architecture.Arm64 => "cloudflared-darwin-arm64.tgz",
                _ => throw new CloudflaredUnsupportedException(arch)
            };
        }

        throw new CloudflaredUnsupportedException();
    }

    /// <summary>
    /// Detects whether the current Linux ARM process uses the hard-float ABI (armhf).
    /// Reads the ELF e_flags field at offset 0x24 and checks for <c>EF_ARM_ABI_FLOAT_HARD</c> (0x400).
    /// Returns <see langword="false"/> on any read error so the caller falls back to armel.
    /// </summary>
    private static bool IsArmHardFloat()
    {
        try
        {
            using var fs = File.OpenRead("/proc/self/exe");
            Span<byte> header = stackalloc byte[40];
            fs.ReadExactly(header);

            // Verify ELF magic: 0x7F 'E' 'L' 'F'
            if (header[0] != 0x7F || header[1] != (byte)'E' ||
                header[2] != (byte)'L' || header[3] != (byte)'F')
            {
                return false;
            }

            // e_flags is a 4-byte little-endian value at offset 36 (0x24).
            // EF_ARM_ABI_FLOAT_HARD = 0x00000400
            var eFlags = BitConverter.ToUInt32(header[36..]);
            return (eFlags & 0x0000_0400u) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the <c>cloudflared</c> binary from a <c>.tgz</c> stream and writes it to
    /// <paramref name="destinationPath"/>.
    /// </summary>
    private static async Task ExtractFromTgzAsync(
        Stream tgzStream,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(tgzStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip, leaveOpen: false);

        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            // The archive contains a single entry named "cloudflared".
            if (!string.Equals(
                    Path.GetFileName(entry.Name),
                    "cloudflared",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var destination = File.Create(destinationPath);
            await entry.DataStream!.CopyToAsync(destination, cancellationToken);
            return;
        }

        throw new InvalidOperationException(
            "cloudflared binary not found inside the downloaded .tgz archive.");
    }
}
