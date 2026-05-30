using System.Runtime.InteropServices;

namespace CloudflaredKit;

/// <summary>
/// Thrown when the current OS/architecture combination is not supported by TryCloudflare.
/// Supported platforms: Linux x64/x86/arm64/arm, macOS x64/arm64, and Windows x64/x86.
/// </summary>
public sealed class CloudflaredUnsupportedException : Exception
{
    /// <summary>Initializes a new instance with a message describing the current platform.</summary>
    public CloudflaredUnsupportedException()
        : base(
            $"The current platform ({RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}) " +
            "is not supported. Supported platforms: Linux x64/x86/arm64/arm, macOS x64/arm64, and Windows x64/x86.")
    {
    }

    /// <summary>Initializes a new instance with a message describing the unsupported architecture.</summary>
    public CloudflaredUnsupportedException(Architecture architecture)
        : base(
            $"Architecture '{architecture}' is not supported on the current OS ({RuntimeInformation.OSDescription}). " +
            "Supported architectures: Linux x64/x86/arm64/arm, macOS x64/arm64, and Windows x64/x86.")
    {
    }
}
