using System;
using System.Globalization;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.SessionProvisioning.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SessionProvisioningPlugin = Jellyfin.Plugin.SessionProvisioning.Plugin;

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
    /// Non-standard status for "the caller went away", as used by nginx. Nothing reads
    /// it — the connection is gone — but it keeps the logs honest.
    /// </summary>
    private const int ClientClosedRequest = 499;

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IPluginManager _pluginManager;
    private readonly ProvisioningSecretSource _secretSource;
    private readonly MintRateLimiter _rateLimiter;
    private readonly MintSerializer _mintSerializer;
    private readonly ILogger<SessionProvisioningController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionProvisioningController"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="pluginManager">Instance of the <see cref="IPluginManager"/> interface.</param>
    /// <param name="secretSource">Supplies the configured provisioning secret hash.</param>
    /// <param name="rateLimiter">Bounds how often this endpoint does work.</param>
    /// <param name="mintSerializer">Ensures only one session is provisioned at a time.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public SessionProvisioningController(
        ISessionManager sessionManager,
        IUserManager userManager,
        IPluginManager pluginManager,
        ProvisioningSecretSource secretSource,
        MintRateLimiter rateLimiter,
        MintSerializer mintSerializer,
        ILogger<SessionProvisioningController> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _pluginManager = pluginManager;
        _secretSource = secretSource;
        _rateLimiter = rateLimiter;
        _mintSerializer = mintSerializer;
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
    /// <response code="429">Too many provisioning requests; retry after the indicated delay.</response>
    /// <response code="503">Another provisioning request is in flight; retry.</response>
    /// <returns>The provisioned session credential.</returns>
    [HttpPost("Mint")]
    [ProducesResponseType(typeof(MintSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MintSessionResponse>> MintSession([FromBody] MintSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gate zero: refuse while the plugin is not enabled. Controllers are registered
        // from loaded assemblies once at startup, independently of plugin state, so a
        // plugin disabled or malfunctioning at runtime keeps serving its routes until
        // the server restarts. For a capability this powerful, "disabled" must mean
        // "cannot mint" immediately, not "cannot mint after the next restart".
        if (!IsPluginActive())
        {
            _logger.LogWarning("Session provisioning rejected: the plugin is not active");
            return NotFound();
        }

        // Rate limit before the secret is examined, so an elevated caller cannot use
        // this endpoint to guess the secret quickly or to drive Jellyfin's session
        // machinery in a loop.
        if (!_rateLimiter.TryAcquire(out var retryAfter))
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            _logger.LogWarning("Session provisioning rejected: rate limit exceeded");
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Gate two. Jellyfin's elevation policy is gate one, applied by [Authorize]
        // above. Both are mandatory; neither is ever conditional.
        if (!IsSecretValid())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request.UserId.Equals(Guid.Empty))
        {
            return BadRequest();
        }

        // One mint at a time. Jellyfin's device/token replacement is not safe against
        // concurrent calls for the same user and device: each racing call sees the old
        // devices, deletes them, and adds its own, leaving several live tokens for one
        // logical device. See MintSerializer.
        using var slot = await _mintSerializer.EnterAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (slot is null)
        {
            if (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogWarning("Session provisioning abandoned: the caller disconnected while queued");
                return StatusCode(ClientClosedRequest);
            }

            _logger.LogWarning("Session provisioning rejected: timed out waiting for an in-flight request");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Re-check both kill switches now the wait is over. A request can sit here for
        // the length of the timeout, during which an administrator may have disabled
        // the plugin or removed/rotated the secret hash; the checks above are only as
        // fresh as the moment they ran. Deciding to revoke this capability must not be
        // defeated by a request that queued while it was still permitted.
        if (!IsPluginActive())
        {
            _logger.LogWarning("Session provisioning rejected: the plugin was deactivated while the request queued");
            return NotFound();
        }

        if (!IsSecretValid())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
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
            // MediaBrowser.Controller.Net.SecurityException, NOT System.Security's:
            // device access restrictions or the target user's MaxActiveSessions limit.
            // Catching the wrong type let this escape to Jellyfin's ExceptionMiddleware,
            // which answered 403 -- indistinguishable from a bad provisioning secret.
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

    /// <summary>
    /// Verifies the provisioning secret presented on this request against the hash the
    /// deployment currently supplies.
    /// </summary>
    /// <remarks>
    /// Re-reads the configured hash on every call, so removing or rotating it takes
    /// effect immediately, including for a request already in flight.
    /// </remarks>
    /// <returns><c>true</c> if a usable hash is configured and the header matches it.</returns>
    private bool IsSecretValid()
    {
        var configuredHash = _secretSource.GetConfiguredHash();
        if (!ProvisioningSecretVerifier.IsConfigured(configuredHash))
        {
            _logger.LogWarning("Session provisioning rejected: no provisioning secret is configured");
            return false;
        }

        Request.Headers.TryGetValue(ProvisioningSecretVerifier.HeaderName, out var presentedSecret);
        if (!ProvisioningSecretVerifier.Verify(configuredHash, presentedSecret))
        {
            _logger.LogWarning("Session provisioning rejected: invalid or missing provisioning secret");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether Jellyfin currently considers this plugin fully active.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="PluginStatus.Active"/> exactly, rather than
    /// <c>LocalPlugin.IsEnabledAndSupported</c>. Disabling a running plugin writes
    /// <c>Disabled</c> to its manifest on disk but leaves the in-memory status at
    /// <see cref="PluginStatus.Restart"/> (see <c>PluginManager.ProcessAlternative</c>),
    /// and <c>IsEnabledAndSupported</c> treats <c>Restart</c> as enabled because
    /// <c>Restart</c> sorts above <c>Active</c>. That is reasonable for an ordinary
    /// plugin winding down, but for this endpoint "disable" must stop minting at once.
    /// <para>
    /// Fails closed: if no plugin record matches this assembly's ID, the plugin is in an
    /// unexpected state and minting is refused.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> only if the plugin record is present, supported, and active.</returns>
    private bool IsPluginActive()
    {
        foreach (var plugin in _pluginManager.Plugins)
        {
            if (plugin.Id.Equals(SessionProvisioningPlugin.PluginId))
            {
                return plugin.IsEnabledAndSupported
                    && plugin.Manifest.Status == PluginStatus.Active;
            }
        }

        return false;
    }
}
