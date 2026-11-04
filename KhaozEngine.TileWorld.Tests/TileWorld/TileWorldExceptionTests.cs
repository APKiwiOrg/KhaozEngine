using System;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_constructors_leave_the_reason_unset(bool withInner)
    {
        var inner = new InvalidOperationException();
        var ex = withInner
            ? new TileWorldException("external refusal", inner)
            : new TileWorldException("external refusal");

        Assert.Null(ex.Reason);
        Assert.Same(withInner ? inner : null, ex.InnerException);
    }

    [Fact]
    public void Reason_constructor_keeps_the_original_cause()
    {
        var inner = new InvalidOperationException();
        var ex = new TileWorldException(TileWorldRefusal.RegionHashMismatch, "refused region", inner);

        Assert.Equal(TileWorldRefusal.RegionHashMismatch, ex.Reason);
        Assert.Same(inner, ex.InnerException);
    }
}
