using System;
using System.IO;
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
    public void ASignedFileVerifiesThroughAHeldHandleAtItsEnd()
    {
        // The .NET runtime libraries carry an embedded Microsoft Authenticode signature.
        var path = typeof(object).Assembly.Location;
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held.Seek(0, SeekOrigin.End);

        using var result = Authenticode.Verify(path, held.SafeFileHandle, AuthenticodeRevocation.None);

        Assert.Equal(AuthenticodeOutcome.Trusted, result.Outcome);
        Assert.Equal(0, result.TrustStatus);
    }

    [Fact]
    public void ASignerIsReadFromTheVerifiedBytesWhileAWriterHandleIsHeld()
    {
        // A download keeps its read-write handle and denies other writers, so the path cannot be reopened.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(typeof(object).Assembly.Location, path);
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            held.Seek(0, SeekOrigin.End);

            using var result = Authenticode.Verify(path, held.SafeFileHandle, AuthenticodeRevocation.None,
                AuthenticodeSigner.WithName(".NET", "Microsoft Corporation"));

            Assert.Equal(AuthenticodeOutcome.Trusted, result.Outcome);
            Assert.Contains("O=Microsoft Corporation", result.Signer!.Subject, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOverlappedHandleIsRefusedBeforeItCanYieldABadDigest()
    {
        var path = typeof(object).Assembly.Location;
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous);

        Assert.Throws<ArgumentException>(() =>
            Authenticode.Verify(path, held.SafeFileHandle, AuthenticodeRevocation.None));
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
