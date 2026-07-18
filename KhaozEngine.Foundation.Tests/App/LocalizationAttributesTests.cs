using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    public class LocalizationAttributesTests
    {
        [Fact]
        public void Exempt_TargetsAssemblyTypeMember()
        {
            var u = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(LocalizationExemptAttribute), typeof(AttributeUsageAttribute))!;
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Assembly));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Class));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Method));
        }

        [Fact]
        public void StringSink_TargetsMethodAndCtor()
        {
            var u = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(LocalizationStringSinkAttribute), typeof(AttributeUsageAttribute))!;
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Method));
            Assert.True(u.ValidOn.HasFlag(AttributeTargets.Constructor));
        }
    }
}
