using System;

namespace Jellyfin.Plugin.SessionProvisioning.Api;

/// <summary>
/// The result of provisioning a session.
/// </summary>
/// <remarks>
/// This is the only place the minted access token is ever disclosed. It must not be
/// logged, cached, or written anywhere else by this plugin.
/// </remarks>
public class MintSessionResponse
{
    /// <summary>
    /// Gets the ID of the user the session belongs to.
    /// </summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// Gets the device ID the session was provisioned for.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// Gets the device name recorded in Jellyfin.
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// Gets the target user's Jellyfin access token.
    /// </summary>
    public required string AccessToken { get; init; }
}
