using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SessionProvisioning.Security;

/// <summary>
/// Ensures only one session is provisioned at a time.
/// </summary>
/// <remarks>
/// Jellyfin's <c>SessionManager.GetAuthorizationToken</c> reads the devices matching
/// user+deviceId, logs each of them out, and then creates a new device — with no lock
/// around the sequence. Concurrent mints for the same user and device therefore each
/// see the pre-existing set, delete it, and add their own row: the result is several
/// live tokens for one logical device, and Jellyfin can throw while they collide.
/// Observed directly: eight simultaneous requests produced four device rows, four
/// valid tokens, and one 500.
/// <para>
/// That breaks the guarantee this plugin depends on — that re-minting an existing
/// user+deviceId rotates the credential rather than accumulating credentials — so a
/// caller that retries or enrols in parallel could leave extra tokens behind that an
/// administrator revoking the device would not clear.
/// </para>
/// <para>
/// Serializing mint requests removes the race for everything that goes through this
/// endpoint. It is deliberately a single global gate rather than a per-key one:
/// provisioning is rare and already rate limited, so the simplest auditable rule —
/// one mint at a time — costs nothing here. It cannot protect against a normal client
/// login racing a mint for the same device; that is Jellyfin's own behaviour.
/// </para>
/// </remarks>
public sealed class MintSerializer : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="MintSerializer"/> class.
    /// </summary>
    public MintSerializer()
        : this(TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MintSerializer"/> class.
    /// </summary>
    /// <param name="timeout">How long to wait for the gate before giving up.</param>
    public MintSerializer(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Waits for exclusive access to the mint path.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait, e.g. when the caller disconnects.</param>
    /// <returns>
    /// A handle to dispose when finished, or <c>null</c> if the wait timed out or was
    /// cancelled, in which case the caller must not proceed. Inspect the token to tell
    /// the two apart.
    /// </returns>
    public async Task<IDisposable?> EnterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _semaphore.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            // A queued request whose caller has gone away must not go on to rotate a
            // working token into a replacement nobody will receive.
            return null;
        }

        return new Slot(_semaphore);
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _released;

        public Slot(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _semaphore.Release();
        }
    }
}
