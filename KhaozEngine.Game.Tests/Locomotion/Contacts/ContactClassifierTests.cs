using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ContactClassifierTests
{
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);

    static Vector3 NormalWithY(float y) => new(MathF.Sqrt(1f - y * y), y, 0f);

    [Fact]
    public void ShellClassesByNormal()
    {
        Assert.Equal(ContactClass.RisingSupport, ContactClassifier.ClassifyShell(NormalWithY(0.8f), CosMaxSlope));
        Assert.Equal(ContactClass.Wall, ContactClassifier.ClassifyShell(NormalWithY(0.5f), CosMaxSlope));
        Assert.Equal(ContactClass.Wall, ContactClassifier.ClassifyShell(NormalWithY(0f), CosMaxSlope));
        Assert.Equal(ContactClass.Ceiling, ContactClassifier.ClassifyShell(NormalWithY(-0.1f), CosMaxSlope));
    }

    [Fact]
    public void SupportClassesByStatus()
    {
        Assert.Equal(ContactClass.Support, ContactClassifier.ClassifySupport(SupportStatus.Walkable));
        Assert.Equal(ContactClass.SteepSupport, ContactClassifier.ClassifySupport(SupportStatus.Steep));
        Assert.Null(ContactClassifier.ClassifySupport(SupportStatus.None));
        Assert.Null(ContactClassifier.ClassifySupport(SupportStatus.Refused));
    }
}
