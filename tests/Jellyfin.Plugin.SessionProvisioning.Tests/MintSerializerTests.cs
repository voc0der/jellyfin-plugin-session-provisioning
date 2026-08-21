using Jellyfin.Plugin.SessionProvisioning.Security;

namespace Jellyfin.Plugin.SessionProvisioning.Tests;

public static class MintSerializerTests
{
    [Fact]
    public static async Task EnterAsync_WhenFree_Succeeds()
    {
        using var serializer = new MintSerializer(TimeSpan.FromSeconds(1));

        using var slot = await serializer.EnterAsync();

        Assert.NotNull(slot);
    }

    [Fact]
    public static async Task EnterAsync_WhileHeld_TimesOut()
    {
        using var serializer = new MintSerializer(TimeSpan.FromMilliseconds(50));
        using var held = await serializer.EnterAsync();

        Assert.Null(await serializer.EnterAsync());
    }

    [Fact]
    public static async Task EnterAsync_AfterRelease_SucceedsAgain()
    {
        using var serializer = new MintSerializer(TimeSpan.FromMilliseconds(200));

        var first = await serializer.EnterAsync();
        Assert.NotNull(first);
        first!.Dispose();

        using var second = await serializer.EnterAsync();
        Assert.NotNull(second);
    }

    [Fact]
    public static async Task Slot_DisposedTwice_DoesNotOverRelease()
    {
        using var serializer = new MintSerializer(TimeSpan.FromMilliseconds(200));

        var slot = await serializer.EnterAsync();
        slot!.Dispose();
        slot.Dispose();

        // A double release would let two callers in at once.
        using var held = await serializer.EnterAsync();
        Assert.NotNull(held);
        Assert.Null(await serializer.EnterAsync());
    }

    [Fact]
    public static async Task EnterAsync_UnderContention_AdmitsOneAtATime()
    {
        using var serializer = new MintSerializer(TimeSpan.FromSeconds(5));
        var concurrent = 0;
        var maxObserved = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using var slot = await serializer.EnterAsync();
            Assert.NotNull(slot);
            var now = Interlocked.Increment(ref concurrent);
            Interlocked.CompareExchange(ref maxObserved, now, now - 1);
            await Task.Delay(5);
            Interlocked.Decrement(ref concurrent);
        }));

        Assert.Equal(1, maxObserved);
    }
}
