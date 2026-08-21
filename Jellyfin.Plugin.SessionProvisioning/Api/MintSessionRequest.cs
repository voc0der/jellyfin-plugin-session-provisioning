using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.SessionProvisioning.Api;

/// <summary>
/// A request to provision a session for an existing Jellyfin user.
/// </summary>
public class MintSessionRequest
{
    /// <summary>
    /// Character set permitted in a device ID: conservative on purpose, since this
    /// value is persistent provisioning state chosen by the provisioning service.
    /// </summary>
    private const string DeviceIdPattern = "^[A-Za-z0-9._:-]+$";

    /// <summary>
    /// Any character other than a control or format character. Keeps newlines, other
    /// control characters, and bidi/format overrides out of a value that is displayed
    /// in Jellyfin's UI and written to logs.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>Cc</c> and <c>Cf</c> rather than the whole <c>C</c> group:
    /// <c>C</c> also covers <c>Cs</c> (surrogates), and every non-BMP character is a
    /// surrogate pair in UTF-16, so <c>[^\p{C}]</c> rejects any device name containing
    /// an emoji or other astral-plane character.
    /// </remarks>
    private const string NoControlCharactersPattern = @"^[^\p{Cc}\p{Cf}]+$";

    /// <summary>
    /// Version-like strings only.
    /// </summary>
    private const string AppVersionPattern = "^[A-Za-z0-9._+-]+$";

    /// <summary>
    /// Gets or sets the ID of the existing Jellyfin user to provision a session for.
    /// </summary>
    /// <remarks>
    /// User IDs are used rather than usernames because usernames can change. An empty
    /// GUID is rejected by the controller; <see cref="RequiredAttribute"/> alone cannot
    /// reject it, because a non-nullable value type is always "present".
    /// </remarks>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the stable device ID for the managed installation.
    /// </summary>
    /// <remarks>
    /// One stable ID per managed logical installation. Reusing an ID rotates that
    /// installation's token; a new ID creates an additional device entry.
    /// </remarks>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression(DeviceIdPattern)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable device name shown in Jellyfin.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression(NoControlCharactersPattern)]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client application version.
    /// </summary>
    /// <remarks>
    /// Required because Jellyfin's own session machinery rejects an empty AppVersion.
    /// </remarks>
    [Required]
    [StringLength(32, MinimumLength = 1)]
    [RegularExpression(AppVersionPattern)]
    public string AppVersion { get; set; } = string.Empty;
}
