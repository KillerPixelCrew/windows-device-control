using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace WindowsDeviceControl;

/// <summary>Which revocation checks Authenticode verification performs.</summary>
public enum AuthenticodeRevocation
{
    /// <summary>
    ///     No revocation check and only cached URL retrieval (<c>WTD_REVOKE_NONE</c>,
    ///     <c>WTD_REVOCATION_CHECK_NONE</c>, <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c>): works offline, for a file
    ///     whose identity is already pinned by other means.
    /// </summary>
    None,

    /// <summary>
    ///     Revocation of every certificate in the chain except the root (<c>WTD_REVOKE_WHOLECHAIN</c>,
    ///     <c>WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT</c>). May reach the network.
    /// </summary>
    ChainExcludingRoot
}

/// <summary>How an Authenticode verification ended.</summary>
public enum AuthenticodeOutcome
{
    /// <summary>The signature and chain are trusted and the signer matched what was expected, if anything.</summary>
    Trusted,

    /// <summary>WinVerifyTrust refused the file; <see cref="AuthenticodeResult.TrustStatus" /> says why.</summary>
    NotTrusted,

    /// <summary>The file is trusted but its signer certificate could not be read to compare.</summary>
    SignerUnreadable,

    /// <summary>The file is trusted but signed by a certificate other than the one expected.</summary>
    SignerMismatch
}

/// <summary>The signer a verification requires: a pinned thumbprint, or one of several subject names.</summary>
/// <param name="Thumbprint">The SHA-1 thumbprint of the signing certificate, compared case-insensitively.</param>
/// <param name="Names">Accepted simple subject names, compared case-insensitively.</param>
public sealed record AuthenticodeSigner(string? Thumbprint, IReadOnlyList<string> Names)
{
    /// <summary>Requires the signing certificate with one exact thumbprint.</summary>
    /// <param name="thumbprint">Its SHA-1 thumbprint.</param>
    /// <returns>The expectation.</returns>
    public static AuthenticodeSigner WithThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        return new AuthenticodeSigner(thumbprint, []);
    }

    /// <summary>Requires a signing certificate whose simple subject name is one of these.</summary>
    /// <param name="names">The accepted names.</param>
    /// <returns>The expectation.</returns>
    public static AuthenticodeSigner WithName(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Length == 0 ? throw new ArgumentException("A signer name is required.", nameof(names))
            : new AuthenticodeSigner(null, names);
    }

    internal bool Matches(X509Certificate2 signer)
    {
        if (Thumbprint is { } thumbprint)
        {
            return string.Equals(signer.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);
        }

        var name = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        foreach (var accepted in Names)
        {
            if (string.Equals(name, accepted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The result of one Authenticode verification. Dispose it to release the signer certificate.</summary>
public sealed class AuthenticodeResult : IDisposable
{
    internal AuthenticodeResult(AuthenticodeOutcome outcome, int trustStatus, X509Certificate2? signer,
        string? signerError)
    {
        Outcome = outcome;
        TrustStatus = trustStatus;
        Signer = signer;
        SignerError = signerError;
    }

    /// <summary>How the verification ended.</summary>
    public AuthenticodeOutcome Outcome { get; }

    /// <summary>The status WinVerifyTrust returned: zero when trusted, otherwise its <c>TRUST_E_*</c> HRESULT.</summary>
    public int TrustStatus { get; }

    /// <summary>The leaf signing certificate, read after the file was trusted; null when it could not be read.</summary>
    public X509Certificate2? Signer { get; }

    /// <summary>Why the signer could not be read, or null.</summary>
    public string? SignerError { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Signer?.Dispose();
    }
}

/// <summary>Embedded Authenticode signature verification through WinVerifyTrust.</summary>
/// <remarks>
///     Verification uses the generic Authenticode policy (<c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>) with no
///     UI, without <c>WTD_LIFETIME_SIGNING_FLAG</c>: a properly timestamped signature stays valid after its
///     certificate expires, as Windows itself reports it. The caller chooses the revocation policy and
///     the expected signer. A self-signed file is not trusted here; it needs its own pinned check.
/// </remarks>
public static unsafe partial class Authenticode
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    private const uint CacheOnlyUrlRetrieval = 0x1000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>Verifies a file's embedded signature and, when asked, who signed it.</summary>
    /// <param name="path">The file to verify.</param>
    /// <param name="handle">
    ///     An open handle to the same file that denies writers, so trust describes the bytes the caller
    ///     holds; null to verify by path alone. It is borrowed, not closed. It must be synchronous:
    ///     WinVerifyTrust reads through it without an OVERLAPPED structure, and an overlapped handle yields a
    ///     wrong digest (<c>TRUST_E_BAD_DIGEST</c>) for a correctly signed file.
    /// </param>
    /// <param name="revocation">The revocation checks to perform.</param>
    /// <param name="expectedSigner">The signer the file must carry, or null to accept any trusted signer.</param>
    /// <returns>The outcome, the WinVerifyTrust status and the leaf certificate; dispose it.</returns>
    /// <exception cref="ArgumentException">The handle was opened for overlapped I/O.</exception>
    /// <remarks>
    ///     The signer is read from the verification's own provider state, the signature WinVerifyTrust just
    ///     trusted, never by opening the path again; a reopen would fail against a held writer handle and
    ///     could describe other bytes.
    /// </remarks>
    public static AuthenticodeResult Verify(string path, SafeFileHandle? handle, AuthenticodeRevocation revocation,
        AuthenticodeSigner? expectedSigner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (handle is { IsAsync: true })
        {
            throw new ArgumentException("Authenticode verification needs a synchronous file handle.", nameof(handle));
        }

        var (status, signer) = VerifyTrust(path, handle, revocation);
        if (status != 0)
        {
            return new AuthenticodeResult(AuthenticodeOutcome.NotTrusted, status, null, null);
        }

        if (signer is null)
        {
            return new AuthenticodeResult(
                expectedSigner is null ? AuthenticodeOutcome.Trusted : AuthenticodeOutcome.SignerUnreadable, status,
                null, "The trusted signature carries no signer certificate.");
        }

        var outcome = expectedSigner is null || expectedSigner.Matches(signer)
            ? AuthenticodeOutcome.Trusted
            : AuthenticodeOutcome.SignerMismatch;
        return new AuthenticodeResult(outcome, status, signer, null);
    }

    /// <summary>The WinTrust flags for one revocation policy.</summary>
    /// <param name="revocation">The policy.</param>
    /// <returns>The <c>fdwRevocationChecks</c> and <c>dwProvFlags</c> values.</returns>
    internal static (uint RevocationChecks, uint ProviderFlags) Flags(AuthenticodeRevocation revocation)
    {
        return revocation switch
        {
            AuthenticodeRevocation.None => (RevokeNone, RevocationCheckNone | CacheOnlyUrlRetrieval),
            AuthenticodeRevocation.ChainExcludingRoot => (RevokeWholeChain, RevocationCheckChainExcludeRoot),
            _ => throw new ArgumentOutOfRangeException(nameof(revocation))
        };
    }

    private static (int Status, X509Certificate2? Signer) VerifyTrust(string path, SafeFileHandle? handle,
        AuthenticodeRevocation revocation)
    {
        var (revocationChecks, providerFlags) = Flags(revocation);
        var added = false;
        try
        {
            handle?.DangerousAddRef(ref added);
            fixed (char* pathPointer = path)
            {
                TrustFileInfo file = new()
                {
                    Size = (uint)sizeof(TrustFileInfo),
                    FilePath = pathPointer,
                    File = added ? handle!.DangerousGetHandle() : 0
                };
                TrustData data = new()
                {
                    Size = (uint)sizeof(TrustData),
                    UiChoice = UiNone,
                    RevocationChecks = revocationChecks,
                    UnionChoice = ChoiceFile,
                    File = &file,
                    StateAction = StateActionVerify,
                    ProviderFlags = providerFlags
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(0, ref action, ref data);
                try
                {
                    return (result, result == 0 ? Signer(data.StateData) : null);
                }
                finally
                {
                    data.StateAction = StateActionClose;
                    WinVerifyTrust(0, ref action, ref data);
                }
            }
        }
        finally
        {
            if (added)
            {
                handle!.DangerousRelease();
            }
        }
    }

    /// <summary>Copies the leaf certificate of the first signer WinVerifyTrust evaluated.</summary>
    /// <param name="state">The open verification state.</param>
    /// <returns>The certificate, or null when the provider state carries none.</returns>
    private static X509Certificate2? Signer(nint state)
    {
        var provider = WTHelperProvDataFromStateData(state);
        var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        var certificate = signer == 0 ? null : WTHelperGetProvCertFromChain(signer, 0);
        // The constructor duplicates the context, so the copy outlives the state closed after this.
        return certificate is null || certificate->Certificate == 0
            ? null
            : new X509Certificate2(certificate->Certificate);
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    private static partial nint WTHelperProvDataFromStateData(nint state);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvCertFromChain")]
    private static partial ProviderCertificate* WTHelperGetProvCertFromChain(nint signer, uint certificate);

    /// <summary>The leading fields of <c>CRYPT_PROVIDER_CERT</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public nint Certificate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFileInfo
    {
        public uint Size;
        public char* FilePath;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public TrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
}
