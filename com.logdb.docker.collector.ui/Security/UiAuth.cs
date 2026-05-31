using System.Security.Cryptography;
using System.Text;

namespace com.logdb.docker.collector.ui.Security;

/// <summary>
/// Helpers for the operator UI's single shared-password login.
/// </summary>
public static class UiAuth
{
    /// <summary>Constant-time string comparison to avoid leaking credential length/timing.</summary>
    public static bool FixedTimeEquals(string? a, string? b)
    {
        var ab = Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bb = Encoding.UTF8.GetBytes(b ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>Only allow same-site relative redirect targets (defends against open redirect).</summary>
    public static bool IsLocalUrl(string? url)
        => !string.IsNullOrEmpty(url)
           && url.StartsWith('/')
           && !url.StartsWith("//")
           && !url.StartsWith("/\\");
}
