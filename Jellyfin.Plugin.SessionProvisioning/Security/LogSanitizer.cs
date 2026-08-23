using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SessionProvisioning.Security;

/// <summary>
/// Renders caller-supplied values safe to write into a log line.
/// </summary>
/// <remarks>
/// Defence in depth, not the primary control. <see cref="Api.MintSessionRequest"/>
/// already rejects control characters and overlong values, and <c>[ApiController]</c>
/// enforces those attributes before the action body runs — the smoke test asserts 400
/// for a newline in a device name and for a device ID containing a space. This exists
/// so the audit lines stay un-forgeable on their own terms: a log statement should not
/// depend on a validation attribute declared in a different file to keep an attacker
/// from injecting a second line into the log (CWE-117).
/// </remarks>
public static partial class LogSanitizer
{
    /// <summary>
    /// Longest caller-supplied value written to a log line. Matches the longest
    /// <c>StringLength</c> on <see cref="Api.MintSessionRequest"/>, so a valid value is
    /// never truncated and a value that somehow evaded validation cannot flood the log.
    /// </summary>
    private const int MaxLoggedLength = 128;

    private const string EmptyPlaceholder = "(empty)";
    private const string TruncationMarker = "...";
    private const string ReplacementCharacter = "?";

    /// <summary>
    /// Returns a value that cannot alter the structure of the log line carrying it.
    /// </summary>
    /// <param name="value">The caller-supplied value.</param>
    /// <returns>
    /// The value with control and format characters replaced and its length bounded, or
    /// a placeholder when it is null or empty.
    /// </returns>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyPlaceholder;
        }

        var bounded = value.Length <= MaxLoggedLength
            ? value
            : value[..MaxLoggedLength] + TruncationMarker;

        return UnsafeForLog().Replace(bounded, ReplacementCharacter);
    }

    /// <summary>
    /// Everything that can forge a log entry or disguise one.
    /// </summary>
    /// <remarks>
    /// <c>Cc</c> and <c>Cf</c> are the classes <see cref="Api.MintSessionRequest"/>
    /// rejects: carriage returns and newlines, which start a new entry, and bidi
    /// overrides, which reorder how an existing one reads. <c>Zl</c> and <c>Zp</c> are
    /// added because U+2028 and U+2029 are neither, yet .NET's own
    /// <c>ReplaceLineEndings</c> and many log viewers do break lines on them.
    /// <para>
    /// Deliberately not the whole <c>C</c> group: that also covers <c>Cs</c>
    /// (surrogates), and every non-BMP character is a surrogate pair in UTF-16, so it
    /// would mangle any legitimate value containing an emoji.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]")]
    private static partial Regex UnsafeForLog();
}
