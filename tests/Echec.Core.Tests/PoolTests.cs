using Echec.Core.Common;
using Xunit;

namespace Echec.Core.Tests;

public class PoolTests
{
    private sealed class Box { public int Value; }

    [Fact]
    public void Get_CreatesViaFactory_WhenEmpty()
    {
        var created = 0;
        var pool = new Pool<Box>(() => { created++; return new Box(); });

        var a = pool.Get();
        var b = pool.Get();

        Assert.NotSame(a, b);
        Assert.Equal(2, created);
        Assert.Equal(0, pool.FreeCount);
    }

    [Fact]
    public void Return_ThenGet_RecyclesSameInstance_WithoutNewAllocation()
    {
        var created = 0;
        var pool = new Pool<Box>(() => { created++; return new Box(); });

        var a = pool.Get();
        pool.Return(a);
        var b = pool.Get();

        Assert.Same(a, b);            // recyclée
        Assert.Equal(1, created);     // pas de nouvelle allocation
    }

    [Fact]
    public void Return_AppliesReset()
    {
        var pool = new Pool<Box>(() => new Box(), reset: box => box.Value = 0);

        var a = pool.Get();
        a.Value = 42;
        pool.Return(a);

        Assert.Equal(0, pool.Get().Value);
    }

    [Fact]
    public void Prewarm_AllocatesInstancesUpFront()
    {
        var pool = new Pool<Box>(() => new Box(), prewarm: 3);
        Assert.Equal(3, pool.FreeCount);
    }
}
