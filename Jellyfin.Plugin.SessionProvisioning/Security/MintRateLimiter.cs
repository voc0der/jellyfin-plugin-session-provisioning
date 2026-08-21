using System;
using System.Threading.RateLimiting;

namespace Jellyfin.Plugin.SessionProvisioning.Security;

/// <summary>
/// Bounds how often the mint endpoint will do work, however well-credentialed the
/// caller is.
/// </summary>
/// <remarks>
/// Both authorization gates still apply; this only caps the rate. It is applied before
/// the secret is checked, so a caller holding an elevated Jellyfin credential cannot
/// use the endpoint to guess the provisioning secret at speed, and cannot spin
/// Jellyfin's session machinery in a loop.
/// <para>
/// The limit is deliberately generous: provisioning is a rare administrative
/// operation, and a batch enrolling a set of managed installations must not trip it.
/// The window is process-local and resets when Jellyfin restarts, which is fine —
/// this is an abuse bound, not a quota.
/// </para>
/// <para>
/// Uses <see cref="System.Threading.RateLimiting"/> from the ASP.NET Core shared
/// framework rather than the MVC rate-limiting middleware, because a plugin cannot add
/// middleware to Jellyfin's request pipeline.
/// </para>
/// </remarks>
public sealed class MintRateLimiter : IDisposable
{
    /// <summary>
    /// Requests permitted per window.
    /// </summary>
    public const int DefaultPermitLimit = 120;

    private readonly FixedWindowRateLimiter _limiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MintRateLimiter"/> class with the
    /// default limit of <see cref="DefaultPermitLimit"/> requests per minute.
    /// </summary>
    public MintRateLimiter()
        : this(DefaultPermitLimit, TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MintRateLimiter"/> class.
    /// </summary>
    /// <param name="permitLimit">Requests permitted per window.</param>
    /// <param name="window">Length of the fixed window.</param>
    public MintRateLimiter(int permitLimit, TimeSpan window)
    {
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    /// <summary>
    /// Attempts to take a permit for one mint request.
    /// </summary>
    /// <param name="retryAfter">How long the caller should wait, when refused.</param>
    /// <returns><c>true</c> if the request may proceed.</returns>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire();
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadata)
            ? metadata
            : TimeSpan.Zero;
        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
