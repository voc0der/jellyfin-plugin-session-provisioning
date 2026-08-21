using System;
using System.Net.Mime;
using System.Security;
using System.Threading.Tasks;
using Jellyfin.Plugin.SessionProvisioning.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SessionProvisioning.Api;

/// <summary>
/// Admin-authorized provisioning of normal Jellyfin user device sessions.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SessionProvisioning")]
[Produces(MediaTypeNames.Application.Json)]
public class SessionProvisioningController : ControllerBase
{
    /// <summary>
    /// The application identity recorded for provisioned sessions.
    /// </summary>
    /// <remarks>
    /// Deliberately fixed rather than caller-supplied. <c>AuthenticationRequest</c>
    /// permits any App value; accepting one from the request would turn this into a
    /// generic client-impersonation API.
    /// </remarks>
    private const string ProvisionedApp = "Jellyfin MPV Shim";

    /// <summary>
    /// Reads the configured secret hash from the deployment environment. Stateless, so
    /// a single shared instance is enough.
    /// </summary>
    private static readonly ProvisioningSecretSource SecretSource = new();

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SessionProvisioningController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionProvisioningController"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public SessionProvisioningController(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILogger<SessionProvisioningController> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Provisions a normal Jellyfin session for an existing user.
    /// </summary>
    /// <param name="request">The provisioning request.</param>
    /// <response code="200">Session provisioned. The access token is returned once.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="401">Caller is not authenticated to Jellyfin.</response>
    /// <response code="403">Caller is not elevated, or the provisioning secret is missing or wrong.</response>
    /// <response code="404">The requested user does not exist.</response>
    /// <response code="409">Jellyfin refused the session for the target user.</response>
    /// <returns>The provisioned session credential.</returns>
    [HttpPost("Mint")]
    [ProducesResponseType(typeof(MintSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MintSessionResponse>> MintSession([FromBody] MintSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gate two. Jellyfin's elevation policy is gate one, applied by [Authorize]
        // above. Both are mandatory; neither is ever conditional.
        var configuredHash = SecretSource.GetConfiguredHash();
        if (!ProvisioningSecretVerifier.IsConfigured(configuredHash))
        {
            _logger.LogWarning("Session provisioning rejected: no provisioning secret is configured");
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        Request.Headers.TryGetValue(ProvisioningSecretVerifier.HeaderName, out var presentedSecret);
        if (!ProvisioningSecretVerifier.Verify(configuredHash, presentedSecret))
        {
            _logger.LogWarning("Session provisioning rejected: invalid or missing provisioning secret");
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request.UserId.Equals(Guid.Empty))
        {
            return BadRequest();
        }

        // Resolve the target user before touching the session machinery, so an unknown
        // user is a clean 404 rather than Jellyfin's "Invalid username or password".
        var user = _userManager.GetUserById(request.UserId);
        if (user is null)
        {
            _logger.LogWarning("Session provisioning rejected: unknown user {UserId}", request.UserId);
            return NotFound();
        }

        var authenticationRequest = new AuthenticationRequest
        {
            UserId = user.Id,
            App = ProvisionedApp,
            AppVersion = request.AppVersion,
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString()
        };

        AuthenticationResult result;
        try
        {
            // Jellyfin creates and persists the device, session, and token. This plugin
            // never generates a token or writes to Jellyfin's database itself.
            result = await _sessionManager.AuthenticateDirect(authenticationRequest).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            // The user existed a moment ago, so this is a race (deleted concurrently).
            _logger.LogWarning("Session provisioning failed: user {UserId} could not be authenticated", request.UserId);
            return NotFound();
        }
        catch (SecurityException)
        {
            // Device access restrictions or the target user's MaxActiveSessions limit.
            _logger.LogWarning(
                "Session provisioning refused by Jellyfin for user {UserId} device {DeviceId}",
                request.UserId,
                request.DeviceId);
            return Conflict();
        }

        // Audit line: shape of the operation only. Never the token.
        _logger.LogInformation(
            "Session provisioning succeeded user={UserId} device={DeviceId}",
            user.Id,
            request.DeviceId);

        return Ok(new MintSessionResponse
        {
            UserId = user.Id,
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            AccessToken = result.AccessToken
        });
    }
}
