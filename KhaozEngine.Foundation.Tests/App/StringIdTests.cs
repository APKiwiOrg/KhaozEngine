using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class StringIdTests
    {
        [Fact]
        public void Key_RoundTrips()
        {
            var id = new StringId("Menu.Play");
            Assert.Equal("Menu.Play", id.Key);
            Assert.Equal("Menu.Play", id.ToString());
        }

        [Fact]
        public void Of_IsEquivalentToCtor()
        {
            Assert.Equal(new StringId("A.B"), StringId.Of("A.B"));
        }

        [Fact]
        public void Equality_IsOrdinalOnKey()
        {
            Assert.Equal(new StringId("x"), new StringId("x"));
            Assert.NotEqual(new StringId("x"), new StringId("X"));
            Assert.True(new StringId("x").GetHashCode() == new StringId("x").GetHashCode());
        }

        [Fact]
        public void NullOrEmptyKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => new StringId(""));
            Assert.Throws<ArgumentNullException>(() => new StringId(null!));
        }
    }
}
