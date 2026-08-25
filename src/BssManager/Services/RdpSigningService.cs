using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BssManager.Models;
using Microsoft.Win32;

namespace BssManager.Services;

/// <summary>
/// Removes the "Unknown remote connection / Unknown publisher" dialog that
/// mstsc shows before every launch.
///
/// That warning is about the .rdp FILE, not the connection: an unsigned file
/// could have been tampered with, so the client asks. It cannot be turned off
/// by policy and an unsigned file gets no "don't ask again" -- the only
/// supported way past it is to sign the file with a certificate the machine
/// trusts.
///
/// Three pieces have to line up, all three established by testing; dropping any
/// one of them brings the dialog back:
///
///   1. A code-signing certificate with its private key in LocalMachine\My.
///      rdpsign.exe finds the signing cert by SHA1 thumbprint, despite the
///      switch being spelled /sha256 and its help text claiming otherwise.
///   2. The public half in LocalMachine\Root, so the signature chains to a
///      trusted anchor. Without this the dialog still appears -- it just
///      upgrades from "Unknown publisher" to naming the publisher and asking
///      you to vouch for it.
///   3. The thumbprint listed in the TrustedCertThumbprints policy, which is
///      what actually suppresses the prompt. Putting the certificate in
///      TrustedPublisher instead does NOT work, though the dialog's own
///      "remember my choices" checkbox implies it should.
///
/// The certificate is generated on the machine it is used on, its private key
/// is never exported, and it is an end-entity certificate rather than a CA, so
/// it cannot vouch for anything but itself. Remove() is the undo.
/// </summary>
public class RdpSigningService
{
    public const string CertSubject = "CN=BSS Alt Manager RDP Signing";

    private const string TerminalServicesPolicyKey =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string ThumbprintValue = "TrustedCertThumbprints";

    /// <summary>Code Signing.</summary>
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private static string RdpSignExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rdpsign.exe");

    // ----------------------------------------------------------------- checks

    public HealthCheck Check()
    {
        var cert = FindCertificate();
        var ready = cert is not null && IsTrusted(cert) && IsListedInPolicy(cert.Thumbprint);

        if (ready)
        {
            return new HealthCheck
            {
                Name = "Launch prompts suppressed",
                State = HealthState.Ok,
                Detail = "session files are signed, so mstsc opens them without asking",
                Consequence = ""
            };
        }

        return new HealthCheck
        {
            Name = "Launch prompts suppressed",
            State = HealthState.Warning,
            Detail = "every launch stops on the \"Unknown remote connection\" warning",
            Consequence = "Windows cannot tell who wrote an unsigned .rdp file, so it asks you to confirm before opening one -- once per alt, every launch, with no option to remember the answer. Signing the files with a certificate this machine trusts skips it.",
            Fix = FixAction.TrustRdpFiles,
            FixLabel = "Sign session files"
        };
    }

    // ------------------------------------------------------------------ setup

    public (bool ok, string message) Apply()
    {
        try
        {
            var cert = FindCertificate() ?? CreateCertificate();

            if (!IsTrusted(cert)) Trust(cert);
            if (!IsListedInPolicy(cert.Thumbprint)) AddToPolicy(cert.Thumbprint);

            // Existing files were written before there was anything to sign
            // them with, so they would still prompt.
            var resigned = SignExisting();

            Log.Write($"rdp signing ready, cert {cert.Thumbprint}, {resigned} file(s) signed");

            return (true, resigned > 0
                ? $"Session files signed. {resigned} existing file(s) updated -- launches no longer prompt."
                : "Session files will be signed from now on -- launches no longer prompt.");
        }
        catch (Exception ex)
        {
            Log.Write($"rdp signing setup failed: {ex}");
            return (false, $"Could not set up signing: {ex.Message}");
        }
    }

    /// <summary>
    /// Undoes <see cref="Apply"/>: drops the certificate from both stores and
    /// removes its thumbprint from the policy. Existing signatures become
    /// meaningless rather than invalid, so the warning simply comes back.
    /// </summary>
    public void Remove()
    {
        foreach (var name in new[] { StoreName.My, StoreName.Root })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);

                foreach (var cert in store.Certificates.Where(Mine).ToList())
                {
                    store.Remove(cert);
                    RemoveFromPolicy(cert.Thumbprint);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"could not clean the {name} store: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- signing

    /// <summary>
    /// Signs one .rdp file in place. Returns false when signing is not set up
    /// -- the normal state before the fix has been applied, not an error, so
    /// callers can ignore it.
    /// </summary>
    public bool TrySign(string rdpPath)
    {
        var cert = FindCertificate();
        if (cert is null || !File.Exists(rdpPath)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RdpSignExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // The switch says /sha256 but wants the SHA1 thumbprint.
            psi.ArgumentList.Add("/q");
            psi.ArgumentList.Add("/sha256");
            psi.ArgumentList.Add(cert.Thumbprint);
            psi.ArgumentList.Add(rdpPath);

            using var process = Process.Start(psi);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);

            if (process.ExitCode == 0) return true;

            Log.Write($"rdpsign failed ({process.ExitCode}) for {rdpPath}: {output}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"rdpsign could not run: {ex.Message}");
            return false;
        }
    }

    private static int SignExisting()
    {
        if (!Directory.Exists(AppPaths.RdpFolder)) return 0;

        var signer = new RdpSigningService();
        return Directory.GetFiles(AppPaths.RdpFolder, "*.rdp").Count(signer.TrySign);
    }

    // ------------------------------------------------------------ certificate

    private static bool Mine(X509Certificate2 cert) =>
        string.Equals(cert.Subject, CertSubject, StringComparison.OrdinalIgnoreCase);

    /// <summary>The signing certificate, or null if there is not a usable one.</summary>
    public X509Certificate2? FindCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            return store.Certificates
                .Where(c => Mine(c) && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Write($"could not read the certificate store: {ex.Message}");
            return null;
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            CertSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Not a CA: this certificate can vouch for nothing except itself, which
        // is what makes it safe to put in the root store.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(CodeSigningOid)], true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Long-dated on purpose: when it expires the signatures stop being
        // accepted and the dialog silently comes back.
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        // CreateSelfSigned hands back an ephemeral key. Round-tripping through
        // PKCS#12 is what makes the private key persist in the machine store,
        // which is where rdpsign.exe looks for it.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pkcs12 = generated.Export(X509ContentType.Pkcs12, password);

        var persistent = X509CertificateLoader.LoadPkcs12(
            pkcs12, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(persistent);
        }

        Log.Write($"created rdp signing certificate {persistent.Thumbprint}");
        return persistent;
    }

    private static bool IsTrusted(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            return store.Certificates.Any(c =>
                string.Equals(c.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void Trust(X509Certificate2 cert)
    {
        // Public half only -- the private key stays in LocalMachine\My.
        var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(publicOnly);
    }

    // ----------------------------------------------------------------- policy

    private static string[] PolicyThumbprints()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TerminalServicesPolicyKey);
        var raw = key?.GetValue(ThumbprintValue) as string;

        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsListedInPolicy(string thumbprint) =>
        PolicyThumbprints().Any(t => string.Equals(t, thumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a thumbprint without disturbing any already there -- the value is
    /// shared with anything else on the machine that trusts an .rdp publisher.
    /// </summary>
    private static void AddToPolicy(string thumbprint)
    {
        var list = PolicyThumbprints().ToList();
        list.Add(thumbprint);

        using var key = Registry.LocalMachine.CreateSubKey(TerminalServicesPolicyKey);
        key.SetValue(ThumbprintValue, string.Join(',', list), RegistryValueKind.String);
    }

    private static void RemoveFromPolicy(string thumbprint)
    {
        var remaining = PolicyThumbprints()
            .Where(t => !string.Equals(t, thumbprint, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        using var key = Registry.LocalMachine.OpenSubKey(TerminalServicesPolicyKey, writable: true);
        if (key is null) return;

        if (remaining.Length == 0) key.DeleteValue(ThumbprintValue, throwOnMissingValue: false);
        else key.SetValue(ThumbprintValue, string.Join(',', remaining), RegistryValueKind.String);
    }
}
