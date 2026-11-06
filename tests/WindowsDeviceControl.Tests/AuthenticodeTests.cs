using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace WindowsDeviceControl.Tests;

public sealed class AuthenticodeTests
{
    // wintrust.h: WTD_REVOKE_NONE 0, WTD_REVOKE_WHOLECHAIN 1, WTD_REVOCATION_CHECK_NONE 0x10,
    // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT 0x80, WTD_CACHE_ONLY_URL_RETRIEVAL 0x1000.
    [Fact]
    public void NoRevocationChecksNothingAndStaysOffline()
    {
        Assert.Equal((0u, 0x1010u), Authenticode.Flags(AuthenticodeRevocation.None));
    }

    [Fact]
    public void ChainRevocationChecksEveryCertificateButTheRoot()
    {
        Assert.Equal((1u, 0x80u), Authenticode.Flags(AuthenticodeRevocation.ChainExcludingRoot));
    }

    [Fact]
    public void AnUndefinedRevocationPolicyIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Authenticode.Flags((AuthenticodeRevocation)7));
    }

    [Fact]
    public void AThumbprintMatchesOnlyThatCertificateInAnyCase()
    {
        using var signer = Certificate("Intel Corporation");
        using var other = Certificate("Intel Corporation");

        Assert.True(AuthenticodeSigner.WithThumbprint(signer.Thumbprint.ToLowerInvariant()).Matches(signer));
        Assert.False(AuthenticodeSigner.WithThumbprint(signer.Thumbprint).Matches(other));
    }

    [Fact]
    public void ANameMatchesAnyAcceptedSubjectNameInAnyCase()
    {
        using var signer = Certificate("Advanced Micro Devices, Inc.");
        var expected = AuthenticodeSigner.WithName("Advanced Micro Devices", "advanced micro devices, inc.");
        var unrelated = AuthenticodeSigner.WithName("Advanced Micro Devices");

        Assert.True(expected.Matches(signer));
        Assert.False(unrelated.Matches(signer));
        Assert.False(AuthenticodeSigner.WithName("NVIDIA Corporation").Matches(signer));
    }

    [Fact]
    public void ANameExpectationNeedsAName()
    {
        Assert.Throws<ArgumentException>(() => AuthenticodeSigner.WithName());
    }

    private static X509Certificate2 Certificate(string commonName)
    {
        using var key = RSA.Create(2048);
        X500DistinguishedNameBuilder subject = new();
        subject.AddCommonName(commonName);
        CertificateRequest request = new(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
