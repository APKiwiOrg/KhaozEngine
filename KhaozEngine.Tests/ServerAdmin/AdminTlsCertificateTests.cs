using KhaozEngine.Server.Admin;
using Xunit;

namespace KhaozEngine.Tests.ServerAdmin;

public class AdminTlsCertificateTests
{
    [Fact]
    public void CreateSelfSigned_ProducesCertWithPrivateKey()
    {
        var tls = AdminTlsCertificate.CreateSelfSigned("khaoz-admin");
        Assert.True(tls.Certificate.HasPrivateKey);
        Assert.Contains("khaoz-admin", tls.Certificate.Subject);
    }
}
