using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;

namespace AriaUI.Helpers;

public static class WindowsCertificateBundle
{
    // MSYS2 libaria2 uses OpenSSL. Give it Windows' trusted roots explicitly.
    public static string ExportTrustedRoots(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var disallowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.Disallowed, location);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80092004) || ex.HResult == unchecked((int)0x80070002)) { continue; }
            foreach (var certificate in store.Certificates)
                using (certificate) disallowed.Add(certificate.Thumbprint);
        }
        var pem = new StringBuilder();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                    if (!disallowed.Contains(certificate.Thumbprint) && seen.Add(certificate.Thumbprint))
                        pem.AppendLine(certificate.ExportCertificatePem());
            }
        }
        if (seen.Count == 0) throw new InvalidOperationException("No Windows trusted root certificates are available.");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "windows-ca-bundle.pem");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, pem.ToString(), Encoding.ASCII);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }
}
