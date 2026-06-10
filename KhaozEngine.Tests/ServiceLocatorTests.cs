using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class ServiceLocatorTests
{
    private interface IFoo { }
    private sealed class Foo : IFoo { }
    private interface IBar { }
    private sealed class Bar : IBar { }

    [Fact]
    public void Register_ThenGet_ReturnsSameInstance()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();

        locator.Register<IFoo>(foo);

        Assert.Same(foo, locator.Get<IFoo>());
    }

    [Fact]
    public void GetService_ByRuntimeType_ReturnsInstance()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();
        locator.Register<IFoo>(foo);

        // Directly and via the IServiceProvider contract.
        Assert.Same(foo, locator.GetService(typeof(IFoo)));
        IServiceProvider provider = locator;
        Assert.Same(foo, provider.GetService(typeof(IFoo)));
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var locator = new ServiceLocator();
        locator.Register<IFoo>(new Foo());

        Assert.Throws<InvalidOperationException>(() => locator.Register<IFoo>(new Foo()));
    }

    [Fact]
    public void Get_Unregistered_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<InvalidOperationException>(() => locator.Get<IFoo>());
    }

    [Fact]
    public void TryGet_ReturnsInstanceOrNull()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();

        Assert.Null(locator.TryGet<IFoo>());
        locator.Register<IFoo>(foo);
        Assert.Same(foo, locator.TryGet<IFoo>());
    }

    [Fact]
    public void Has_ReflectsRegistration()
    {
        var locator = new ServiceLocator();

        Assert.False(locator.Has<IFoo>());
        locator.Register<IFoo>(new Foo());
        Assert.True(locator.Has<IFoo>());
    }

    [Fact]
    public void Replace_OverwritesExisting()
    {
        var locator = new ServiceLocator();
        var first = new Foo();
        var second = new Foo();
        locator.Register<IFoo>(first);

        locator.Replace<IFoo>(second);

        Assert.Same(second, locator.Get<IFoo>());
    }

    [Fact]
    public void Replace_AddsWhenAbsent()
    {
        var locator = new ServiceLocator();
        var bar = new Bar();

        locator.Replace<IBar>(bar);

        Assert.Same(bar, locator.Get<IBar>());
    }

    [Fact]
    public void GetService_Unregistered_ReturnsNull()
    {
        var locator = new ServiceLocator();

        Assert.Null(locator.GetService(typeof(IFoo)));
    }

    [Fact]
    public void Register_Null_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<ArgumentNullException>(() => locator.Register<IFoo>(null!));
    }

    [Fact]
    public void Replace_Null_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<ArgumentNullException>(() => locator.Replace<IFoo>(null!));
    }
}
