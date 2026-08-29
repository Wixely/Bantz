using Bantz;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class SingleInstanceLockTests
{
    [Fact]
    public void TryAcquireReturnsNullWhileAnotherThreadOwnsNamedLock()
    {
        var name = $"Bantz.Tests.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? ownerException = null;

        var owner = new Thread(() =>
        {
            try
            {
                using var first = SingleInstanceLock.TryAcquire(name);
                if (first is null)
                {
                    throw new InvalidOperationException("The first lock acquisition failed.");
                }

                acquired.Set();
                release.Wait();
            }
            catch (Exception exception)
            {
                ownerException = exception;
                acquired.Set();
            }
        });

        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        using var second = SingleInstanceLock.TryAcquire(name);
        Assert.Null(second);

        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerException);
    }

    [Fact]
    public void TryAcquireAllowsLockToBeAcquiredAgainAfterDispose()
    {
        var name = $"Bantz.Tests.{Guid.NewGuid():N}";

        using (var first = SingleInstanceLock.TryAcquire(name))
        {
            Assert.NotNull(first);
        }

        using var second = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(second);
    }
}
