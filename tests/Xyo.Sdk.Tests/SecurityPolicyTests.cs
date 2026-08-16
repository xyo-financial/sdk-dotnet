using System;
using System.Net;
using Xunit;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Security;

namespace Xyo.Sdk.Tests;

public class SecurityPolicyTests
{
    [Theory]
    [InlineData("https://api.xyo.financial/batches/file.tar.gz")]
    [InlineData("https://download.xyo.financial/batches/file.tar.gz")]
    [InlineData("https://xyo-financial.s3.amazonaws.com/batches/file.tar.gz")]
    [InlineData("https://xyo-financial.s3.us-east-1.amazonaws.com/batches/file.tar.gz")]
    public void ValidateDownloadUrl_DefaultTrustedHosts_Passes(string url)
    {
        var policy = new DownloadSecurityPolicy("https://api.xyo.financial");
        var uri = policy.ValidateDownloadUrl(url);

        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("http://api.xyo.financial/file.tar.gz")]
    [InlineData("ftp://api.xyo.financial/file.tar.gz")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://api.xyo.financial")]
    public void ValidateDownloadUrl_InsecureOrNonHttpsScheme_ThrowsXyoClientException(string url)
    {
        var policy = new DownloadSecurityPolicy("https://api.xyo.financial");

        var ex = Assert.Throws<XyoClientException>(() => policy.ValidateDownloadUrl(url));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Theory]
    [InlineData("https://evil-attacker.com/file.tar.gz")]
    [InlineData("https://malicious.s3.amazonaws.com/file.tar.gz")]
    [InlineData("https://xyo.financial.attacker.com/file.tar.gz")]
    public void ValidateDownloadUrl_UntrustedHost_ThrowsXyoClientException(string url)
    {
        var policy = new DownloadSecurityPolicy("https://api.xyo.financial");

        var ex = Assert.Throws<XyoClientException>(() => policy.ValidateDownloadUrl(url));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("not in the trusted domain allowlist", ex.Message);
    }

    [Fact]
    public void ValidateDownloadUrl_CustomTrustedHost_Passes()
    {
        var policy = new DownloadSecurityPolicy("https://api.xyo.financial", new[] { "storage.internal.bank.corp" });
        var uri = policy.ValidateDownloadUrl("https://storage.internal.bank.corp/archives/batch.tar.gz");

        Assert.NotNull(uri);
        Assert.Equal("storage.internal.bank.corp", uri.Host);
    }

    [Fact]
    public void IsExternalStorageHost_ApiVsS3_IdentifiesCorrectly()
    {
        var policy = new DownloadSecurityPolicy("https://api.xyo.financial");

        Assert.False(policy.IsExternalStorageHost("api.xyo.financial"));
        Assert.True(policy.IsExternalStorageHost("download.xyo.financial"));
        Assert.True(policy.IsExternalStorageHost("xyo-financial.s3.amazonaws.com"));
    }
}
