using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SessionProvisioning;
using Jellyfin.Plugin.SessionProvisioning.Api;
using Jellyfin.Plugin.SessionProvisioning.Security;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReturnsExtensions;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

/// <summary>
/// Covers gate ordering and the mapping from Jellyfin's behaviour to HTTP results.
/// The live suite proves the same things against a real server; these pin the branches
/// that are awkward to provoke there, such as the fail-closed path when no plugin
/// record matches.
/// </summary>
public sealed class SessionProvisioningControllerTests
{
    private const string Secret = "test-provisioning-secret-value";
    private const string MintedToken = "minted-access-token";

    private static readonly Guid TargetUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly ISessionManager _sessionManager = Substitute.For<ISessionManager>();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly IPluginManager _pluginManager = Substitute.For<IPluginManager>();

    public SessionProvisioningControllerTests()
    {
        var user = new User("bob", "Default", "Default") { Id = TargetUserId };
        _userManager.GetUserById(TargetUserId).Returns(user);

        _sessionManager.AuthenticateDirect(Arg.Any<AuthenticationRequest>())
            .Returns(new AuthenticationResult { AccessToken = MintedToken });

        SetPluginStatus(PluginStatus.Active);
    }

    private static PluginManifest ManifestWith(PluginStatus status) => new()
    {
        Id = Plugin.PluginId,
        Name = "Session Provisioning",
        Version = "1.0.0.0",
        Status = status
    };

    private void SetPluginStatus(PluginStatus status, bool isSupported = true)
        => _pluginManager.Plugins.Returns(new[] { new LocalPlugin("/plugins/sp", isSupported, ManifestWith(status)) });

    private void SetNoPluginRecord()
        => _pluginManager.Plugins.Returns(Array.Empty<LocalPlugin>());

    private SessionProvisioningController CreateController(
        string? configuredHash = null,
        MintRateLimiter? rateLimiter = null,
        string? presentedSecret = Secret,
        MintSerializer? mintSerializer = null)
    {
        var secretSource = new ProvisioningSecretSource(
            name => name == ProvisioningSecretSource.HashVariable
                ? configuredHash ?? ProvisioningSecretVerifier.ComputeHashHex(Secret)
                : null,
            _ => throw new FileNotFoundException());

        var controller = new SessionProvisioningController(
            _sessionManager,
            _userManager,
            _pluginManager,
            secretSource,
            rateLimiter ?? new MintRateLimiter(100, TimeSpan.FromMinutes(1)),
            mintSerializer ?? new MintSerializer(TimeSpan.FromSeconds(5)),
            NullLogger<SessionProvisioningController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (presentedSecret is not null)
        {
            httpContext.Request.Headers[ProvisioningSecretVerifier.HeaderName] = presentedSecret;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static MintSessionRequest Request(Guid? userId = null) => new()
    {
        UserId = userId ?? TargetUserId,
        DeviceId = "living-room-mpv-shim-0f2a",
        DeviceName = "Living Room MPV Shim",
        AppVersion = "3.0.0"
    };

    private static int StatusOf(ActionResult<MintSessionResponse> result) => result.Result switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 0,
        _ => 0
    };

    private async Task AssertNoSessionCreated()
        => await _sessionManager.DidNotReceive().AuthenticateDirect(Arg.Any<AuthenticationRequest>());

    [Fact]
    public async Task MintSession_ValidRequest_ReturnsTokenOnce()
    {
        var result = await CreateController().MintSession(Request());

        var response = Assert.IsType<MintSessionResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(MintedToken, response.AccessToken);
        Assert.Equal(TargetUserId, response.UserId);
        Assert.Equal("living-room-mpv-shim-0f2a", response.DeviceId);
    }

    [Fact]
    public async Task MintSession_ValidRequest_UsesPluginControlledAppIdentity()
    {
        await CreateController().MintSession(Request());

        await _sessionManager.Received(1).AuthenticateDirect(Arg.Is<AuthenticationRequest>(r =>
            r.App == "Jellyfin MPV Shim"
            && r.UserId == TargetUserId
            && r.DeviceId == "living-room-mpv-shim-0f2a"
            && r.DeviceName == "Living Room MPV Shim"
            && r.AppVersion == "3.0.0"
            && string.IsNullOrEmpty(r.Password)
            && string.IsNullOrEmpty(r.Username)));
    }

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Malfunctioned)]
    [InlineData(PluginStatus.NotSupported)]
    [InlineData(PluginStatus.Superseded)]
    [InlineData(PluginStatus.Deleted)]
    [InlineData(PluginStatus.Restart)] // queued for disable: still "enabled and supported"
    public async Task MintSession_PluginNotActive_IsRefused(PluginStatus status)
    {
        SetPluginStatus(status);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_PluginUnsupported_IsRefused()
    {
        SetPluginStatus(PluginStatus.Active, isSupported: false);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request())));
        await AssertNoSessionCreated();
    }

    // Fail closed: an assembly serving requests with no matching plugin record is in an
    // unexpected state, so it must refuse rather than assume it is fine.
    [Fact]
    public async Task MintSession_NoPluginRecord_IsRefused()
    {
        SetNoPluginRecord();

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("deadbeef")]
    public async Task MintSession_NoUsableHashConfigured_IsRefused(string? configuredHash)
    {
        var controller = CreateController(configuredHash: configuredHash ?? string.Empty);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Theory]
    [InlineData(null)]      // header absent entirely
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData("test-provisioning-secret-valu")] // one character short
    public async Task MintSession_BadSecret_IsRefused(string? presented)
    {
        var controller = CreateController(presentedSecret: presented);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_EmptyUserId_IsRejected()
    {
        var result = await CreateController().MintSession(Request(Guid.Empty));

        Assert.IsType<BadRequestResult>(result.Result);
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_UnknownUser_IsNotFound()
    {
        var unknown = Guid.NewGuid();
        _userManager.GetUserById(unknown).ReturnsNull();

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request(unknown))));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_UserVanishesMidFlight_IsNotFound()
    {
        _sessionManager.AuthenticateDirect(Arg.Any<AuthenticationRequest>())
            .Throws(new AuthenticationException("Invalid username or password entered."));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request())));
    }

    // MediaBrowser.Controller.Net.SecurityException, not System.Security's: device
    // restrictions and MaxActiveSessions both arrive this way.
    [Fact]
    public async Task MintSession_JellyfinRefusesSession_IsConflict()
    {
        _sessionManager.AuthenticateDirect(Arg.Any<AuthenticationRequest>())
            .Throws(new SecurityException("User is at their maximum number of sessions."));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await CreateController().MintSession(Request())));
    }

    [Fact]
    public async Task MintSession_OverRateLimit_Returns429WithRetryAfter()
    {
        using var limiter = new MintRateLimiter(1, TimeSpan.FromMinutes(1));
        var controller = CreateController(rateLimiter: limiter);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.MintSession(Request())));

        var second = CreateController(rateLimiter: limiter);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(await second.MintSession(Request())));
        Assert.False(string.IsNullOrEmpty(second.Response.Headers.RetryAfter));
    }

    [Fact]
    public async Task MintSession_RateLimitIsCheckedBeforeTheSecret()
    {
        using var limiter = new MintRateLimiter(1, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(out _)); // exhaust it

        var controller = CreateController(rateLimiter: limiter, presentedSecret: "wrong-secret");

        // 429 rather than 403 proves a flood is bounded before any secret comparison.
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(await controller.MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_PluginStateIsCheckedBeforeTheRateLimit()
    {
        SetPluginStatus(PluginStatus.Disabled);
        using var limiter = new MintRateLimiter(1, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(out _));

        var controller = CreateController(rateLimiter: limiter);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.MintSession(Request())));
    }

    // Provisioning is serialized: Jellyfin's device replacement races with itself, so
    // a second request must wait rather than interleave.
    [Fact]
    public async Task MintSession_WhileAnotherIsInFlight_WaitsRatherThanInterleaving()
    {
        using var serializer = new MintSerializer(TimeSpan.FromSeconds(5));
        var inFlight = new TaskCompletionSource<AuthenticationResult>();
        _sessionManager.AuthenticateDirect(Arg.Any<AuthenticationRequest>()).Returns(inFlight.Task);

        var first = CreateController(mintSerializer: serializer).MintSession(Request());
        await Task.Delay(50);

        var second = CreateController(mintSerializer: serializer).MintSession(Request());
        await Task.Delay(50);

        // The load-bearing assertion. Checking only that `second` is incomplete proves
        // nothing: both calls await the same unfinished task, so it would be incomplete
        // even if the gate let them in together. Counting the calls is what shows the
        // second is still queued.
        await _sessionManager.Received(1).AuthenticateDirect(Arg.Any<AuthenticationRequest>());
        Assert.False(second.IsCompleted);

        inFlight.SetResult(new AuthenticationResult { AccessToken = MintedToken });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await first));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await second));
        await _sessionManager.Received(2).AuthenticateDirect(Arg.Any<AuthenticationRequest>());
    }

    [Fact]
    public async Task MintSession_GateHeldTooLong_IsServiceUnavailable()
    {
        using var serializer = new MintSerializer(TimeSpan.FromMilliseconds(50));
        var held = await serializer.EnterAsync();
        Assert.NotNull(held);

        try
        {
            var controller = CreateController(mintSerializer: serializer);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(await controller.MintSession(Request())));
            await AssertNoSessionCreated();
        }
        finally
        {
            held!.Dispose();
        }
    }

    /// <summary>
    /// Builds a source whose answer changes after the first read, standing in for an
    /// administrator removing or rotating the hash while a request is queued.
    /// </summary>
    private static ProvisioningSecretSource ChangingSecretSource(string? firstAnswer, string? laterAnswer)
    {
        var reads = 0;
        return new ProvisioningSecretSource(
            name =>
            {
                if (name != ProvisioningSecretSource.HashVariable)
                {
                    return null;
                }

                return reads++ == 0 ? firstAnswer : laterAnswer;
            },
            _ => throw new FileNotFoundException());
    }

    // The gates run before the serializer wait, so their answers can be up to the
    // timeout old by the time the request actually mints.
    [Fact]
    public async Task MintSession_SecretRemovedWhileQueued_IsRefused()
    {
        var controller = new SessionProvisioningController(
            _sessionManager,
            _userManager,
            _pluginManager,
            ChangingSecretSource(ProvisioningSecretVerifier.ComputeHashHex(Secret), null),
            new MintRateLimiter(100, TimeSpan.FromMinutes(1)),
            new MintSerializer(TimeSpan.FromSeconds(5)),
            NullLogger<SessionProvisioningController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[ProvisioningSecretVerifier.HeaderName] = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_SecretRotatedWhileQueued_IsRefused()
    {
        var controller = new SessionProvisioningController(
            _sessionManager,
            _userManager,
            _pluginManager,
            ChangingSecretSource(
                ProvisioningSecretVerifier.ComputeHashHex(Secret),
                ProvisioningSecretVerifier.ComputeHashHex("a-different-secret")),
            new MintRateLimiter(100, TimeSpan.FromMinutes(1)),
            new MintSerializer(TimeSpan.FromSeconds(5)),
            NullLogger<SessionProvisioningController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[ProvisioningSecretVerifier.HeaderName] = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.MintSession(Request())));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_PluginDisabledWhileQueued_IsRefused()
    {
        var active = new LocalPlugin("/plugins/sp", true, ManifestWith(PluginStatus.Active));
        var disabled = new LocalPlugin("/plugins/sp", true, ManifestWith(PluginStatus.Disabled));
        _pluginManager.Plugins.Returns(_ => new[] { active }, _ => new[] { disabled });

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().MintSession(Request())));
        await AssertNoSessionCreated();
    }

    // A caller that has hung up must not have a working token rotated out from under it.
    [Fact]
    public async Task MintSession_CallerDisconnectsWhileQueued_DoesNotMint()
    {
        using var serializer = new MintSerializer(TimeSpan.FromSeconds(30));
        using var held = await serializer.EnterAsync();
        Assert.NotNull(held);

        var controller = CreateController(mintSerializer: serializer);
        using var aborted = new CancellationTokenSource();
        controller.ControllerContext.HttpContext.RequestAborted = aborted.Token;

        var pending = controller.MintSession(Request());
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        await aborted.CancelAsync();

        Assert.Equal(499, StatusOf(await pending));
        await AssertNoSessionCreated();
    }

    [Fact]
    public async Task MintSession_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateController().MintSession(null!));
    }
}
