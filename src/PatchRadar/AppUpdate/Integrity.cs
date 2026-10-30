using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PatchRadar.Infra;

namespace PatchRadar.AppUpdate;

// ---- download ---------------------------------------------------------------------------------

public interface IFileDownloader
{
    /// <summary>Download to <paramref name="destPath"/>. Throws <see cref="UpdateCheckException"/> on any refusal or failure.</summary>
    Task DownloadAsync(string url, string destPath, CancellationToken ct);
    Task<string> DownloadTextAsync(string url, CancellationToken ct);
}

/// <summary>
/// https only, allowlisted hosts only, and every redirect hop is re-checked
/// (GitHub redirects release assets to a CDN host). Redirects are followed by hand for that reason.
/// </summary>
public sealed class AllowlistedDownloader : IFileDownloader
{
    private const int MaxHops = 5;
    private const long MaxBytes = 300L * 1024 * 1024;
    private readonly Func<HttpClient> _http;
    private readonly string _userAgent;

    /// <param name="http">Must not follow redirects on its own (see <see cref="CreateClient"/>).</param>
    public AllowlistedDownloader(HttpClient http, string userAgentVersion) : this(() => http, userAgentVersion) { }

    public AllowlistedDownloader(Func<HttpClient> http, string userAgentVersion)
    {
        _http = http;
        _userAgent = "PatchRadar/" + userAgentVersion;
    }

    public static HttpClient CreateClient(string proxy, TimeSpan timeout)
    {
        var h = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None };
        if (!string.IsNullOrWhiteSpace(proxy)) { h.Proxy = new System.Net.WebProxy(proxy); h.UseProxy = true; }
        return new HttpClient(h) { Timeout = timeout };
    }

    public async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength is > MaxBytes)
            throw new UpdateCheckException("Download refused: file is larger than expected.");
        var tmp = destPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using (var fs = File.Create(tmp))
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) throw new UpdateCheckException("Download refused: file is larger than expected.");
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            File.Move(tmp, destPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public async Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength is > 64 * 1024) throw new UpdateCheckException("Checksum file is unexpectedly large.");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop <= MaxHops; hop++)
        {
            if (!UpdateRepo.IsAllowedAssetUrl(current))
                throw new UpdateCheckException($"Download refused: {SafeHost(current)} is not an allowed update host.");

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.UserAgent.ParseAdd(_userAgent);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            HttpResponseMessage resp;
            try { resp = await _http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new UpdateCheckException("Download failed: " + ex.Message, ex);
            }

            var code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var loc = resp.Headers.Location;
                resp.Dispose();
                if (loc is null) throw new UpdateCheckException("Download failed: redirect without a location.");
                current = (loc.IsAbsoluteUri ? loc : new Uri(new Uri(current), loc)).ToString();
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                resp.Dispose();
                throw new UpdateCheckException($"Download failed: HTTP {code}.");
            }
            return resp;
        }
        throw new UpdateCheckException("Download failed: too many redirects.");
    }

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(invalid URL)";
}

// ---- checksum ---------------------------------------------------------------------------------

public static class Sha256Check
{
    public static string ComputeFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Reads a <c>&lt;hash&gt;  &lt;filename&gt;</c> line. The filename must match the asset, so a checksum
    /// file for a different asset cannot be swapped in.
    /// </summary>
    public static string? ParseExpected(string text, string assetName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) continue;
            if (parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase)) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public static bool Matches(string path, string expectedHex) =>
        string.Equals(ComputeFile(path), expectedHex, StringComparison.OrdinalIgnoreCase);
}

// ---- Authenticode -----------------------------------------------------------------------------

public enum SignatureStatus { Valid, Unsigned, WrongSigner, Tampered, Invalid }

public sealed record SignatureResult(SignatureStatus Status, string? Signer = null, string? Detail = null)
{
    public bool IsValid => Status == SignatureStatus.Valid;
}

public interface IAuthenticodeVerifier
{
    SignatureResult Verify(string path);
}

public static class SignatureClassifier
{
    public const string RequiredSignerOrg = "O=SignPath Foundation";

    // WinVerifyTrust HRESULTs
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);

    /// <summary>Turns the WinVerifyTrust result and the signer subject into one verdict. Pure, so it is table-tested.</summary>
    public static SignatureResult Classify(int trustResult, string? signerSubject)
    {
        switch (trustResult)
        {
            case 0:
                if (signerSubject is not null && signerSubject.Contains(RequiredSignerOrg, StringComparison.OrdinalIgnoreCase))
                    return new SignatureResult(SignatureStatus.Valid, signerSubject);
                return new SignatureResult(SignatureStatus.WrongSigner, signerSubject,
                    $"Signature is valid but the signer is not {RequiredSignerOrg.Replace("O=", "")}.");
            case TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown:
                return new SignatureResult(SignatureStatus.Unsigned, null, "The file has no Authenticode signature.");
            case TrustEBadDigest:
                return new SignatureResult(SignatureStatus.Tampered, signerSubject, "The file was modified after it was signed.");
            case CertEUntrustedRoot or CertEChaining or CertEExpired or TrustESubjectNotTrusted:
                return new SignatureResult(SignatureStatus.Invalid, signerSubject, $"The certificate chain is not trusted (0x{trustResult:X8}).");
            default:
                return new SignatureResult(SignatureStatus.Invalid, signerSubject, $"Signature check failed (0x{trustResult:X8}).");
        }
    }
}

/// <summary>
/// WinVerifyTrust with WINTRUST_ACTION_GENERIC_VERIFY_V2, then a signer-subject check. No thumbprint pinning.
/// Revocation lookups are off: servers that cannot reach CRL endpoints would otherwise reject good files.
/// </summary>
public sealed class WinTrustVerifier : IAuthenticodeVerifier
{
    public SignatureResult Verify(string path)
    {
        var hr = WinVerifyTrustFile(path);
        string? subject = null;
        if (hr is 0 or unchecked((int)0x80096010))
        {
            try
            {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile has no replacement that reads the Authenticode signer
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                subject = cert.Subject;
            }
            catch (CryptographicException) { /* fall through: Classify decides */ }
        }
        return SignatureClassifier.Classify(hr, subject);
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
        };
        var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,                 // WTD_UI_NONE
                fdwRevocationChecks = 0,        // WTD_REVOKE_NONE
                dwUnionChoice = 1,              // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,              // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10,             // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;             // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(pFile);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
