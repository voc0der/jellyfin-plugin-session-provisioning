using Jellyfin.Plugin.SessionProvisioning.Security;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

/// <summary>
/// The request DTO already rejects these values before an action body ever sees them.
/// These pin the second line of defence: whatever reaches a log statement cannot add a
/// line to the log or disguise the one it is on.
/// </summary>
public static class LogSanitizerTests
{
    [Theory]
    [InlineData("living-room-mpv-shim-0f2a")]
    [InlineData("Living Room MPV Shim")]
    [InlineData("device.id_with:every-permitted-char")]
    [InlineData("Salon \U0001F4FA")] // astral-plane character: a surrogate pair, kept intact
    public static void ForLog_HarmlessValue_IsUnchanged(string value)
    {
        Assert.Equal(value, LogSanitizer.ForLog(value));
    }

    [Theory]
    [InlineData("a\nb", "a?b")]
    [InlineData("a\rb", "a?b")]
    [InlineData("a\r\nb", "a??b")]
    [InlineData("a\0b", "a?b")]
    [InlineData("a\tb", "a?b")]
    [InlineData("a\vb", "a?b")]
    [InlineData("a\fb", "a?b")]
    [InlineData("a\u0085b", "a?b")] // NEL
    [InlineData("a\u2028b", "a?b")] // LINE SEPARATOR: Zl, not a control character
    [InlineData("a\u2029b", "a?b")] // PARAGRAPH SEPARATOR: Zp, not a control character
    [InlineData("a\u202Eb", "a?b")] // RIGHT-TO-LEFT OVERRIDE
    public static void ForLog_LineBreakingOrOverridingCharacter_IsReplaced(string value, string expected)
    {
        Assert.Equal(expected, LogSanitizer.ForLog(value));
    }

    // The whole point of the sanitizer: a forged second entry must not survive it.
    [Fact]
    public static void ForLog_ForgedSecondLogEntry_CannotBreakOutOfItsLine()
    {
        const string Forged = "dev-x\n[INF] Session provisioning succeeded user=root device=attacker";

        var sanitized = LogSanitizer.ForLog(Forged);

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public static void ForLog_NullOrEmpty_IsPlaceholder(string? value)
    {
        Assert.Equal("(empty)", LogSanitizer.ForLog(value));
    }

    [Fact]
    public static void ForLog_ValueAtTheValidationLimit_IsNotTruncated()
    {
        var atLimit = new string('d', 128);

        Assert.Equal(atLimit, LogSanitizer.ForLog(atLimit));
    }

    [Fact]
    public static void ForLog_OverlongValue_IsBounded()
    {
        Assert.Equal(new string('d', 128) + "...", LogSanitizer.ForLog(new string('d', 10_000)));
    }

    // Truncation must not become a way to smuggle a line break past the replacement.
    [Fact]
    public static void ForLog_OverlongValueOfLineBreaks_IsBoundedAndSanitized()
    {
        Assert.Equal(new string('?', 128) + "...", LogSanitizer.ForLog(new string('\n', 10_000)));
    }
}
