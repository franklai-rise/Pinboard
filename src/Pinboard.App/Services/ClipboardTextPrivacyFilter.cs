using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Pinboard.App.Services;

/// <summary>
/// Reduces common accidental secret captures. This is a best-effort safeguard,
/// not a password detector: applications may not expose clipboard ownership and
/// sensitive text can use formats that no heuristic recognizes.
/// </summary>
public sealed class ClipboardTextPrivacyFilter
{
    private static readonly Regex CredentialAssignment = new(
        @"(?im)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization)\b\s*[:=]\s*['""]?\S{4,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex TokenShape = new(
        @"(?ix)(?:\bBearer\s+[A-Za-z0-9._~+\-/]+=*|\bgh[pousr]_[A-Za-z0-9_]{20,}|\bsk-[A-Za-z0-9_-]{20,}|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly HashSet<string> _excludedApplications;

    public ClipboardTextPrivacyFilter(IEnumerable<string>? excludedApplications = null)
    {
        _excludedApplications = new HashSet<string>(
            (excludedApplications ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.GetFileNameWithoutExtension(name.Trim()))
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool ShouldExcludeCurrentClipboardOwner() =>
        TryGetClipboardOwnerProcessName() is { } processName && _excludedApplications.Contains(processName);

    public bool LooksSensitive(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase)
            && value.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (CredentialAssignment.IsMatch(value) || TokenShape.IsMatch(value))
        {
            return true;
        }

        // A copied value consisting only of 4–8 digits is commonly an OTP or PIN.
        if (value.Length is >= 4 and <= 8 && value.All(char.IsAsciiDigit))
        {
            return true;
        }

        return LooksLikePaymentCard(value);
    }

    public static string? TryGetClipboardOwnerProcessName()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(owner, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikePaymentCard(string text)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        // Avoid classifying normal prose containing many unrelated numbers.
        if (text.Any(character => !char.IsAsciiDigit(character) && character is not ' ' and not '-'))
        {
            return false;
        }

        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index] - '0';
            if (doubleDigit && (digit *= 2) > 9)
            {
                digit -= 9;
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
