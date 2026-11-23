#nullable enable
using Corsac.Lang;

namespace Corsac;

/// <summary>
/// THE CLASS LIBRARY'S SPLIT INTO SHARED LIBRARIES: which image owns each
/// hosted library source, in build order. A library may use only the ones
/// before it in this table. Every hosted source of
/// <see cref="Driver.DefaultLibraries"/> is owned by exactly one of them, and
/// <see cref="Check"/> says so before anything reads the table.
///
/// It lives here, beside the list of sources, because every build that lays
/// the libraries out asked for it separately: the integration script kept a
/// copy, the kernel repository's image build kept another, and the second
/// fell behind (normalization.cor missing from corsacrt). Both now ask:
/// `corc library-split` prints it, and `corc build` hands a manifest the same
/// text as a file through $(ClassLibraries).
///
/// Drawing, the GUI seat and Forms call each other, so they are one image. A
/// class whose parts lie in two libraries keeps its static state in the
/// first one's part: SystemType's statics are started by the runtime's
/// StaticInit$, which imported the COM part's Preinit$Missing and did not
/// link, so Type.Missing is System.Reflection.Missing.Value in Core.cor.
/// </summary>
internal static class ClassLibraries
{
    internal sealed record Library(string Soname, string[] Sources);

    private static readonly Library[] Table =
    [
        new("libcorsacrt.so", [
            "stdlib/src/System/Core.cor",
            "stdlib/src/System/Runtime/ExceptionServices/ExceptionDispatchInfo.cor",
            "runtime/src/core/runtime.cor",
            "runtime/src/core/unicode.cor",
            "runtime/src/core/gc.cor",
            "stdlib/src/System/GC.cor",
            "runtime/src/core/threading.cor",
            "runtime/src/platforms/threads.cor",
            "runtime/src/platforms/linux/threading.cor",
            "runtime/src/platforms/linux/system.cor",
            "runtime/src/platforms/linux/abi.cor",
            "runtime/src/platforms/linux/files.cor",
            "stdlib/src/System/signals.cor",
            "stdlib/src/System/Threading/interlocked.cor",
            "stdlib/src/System/Text/normalization.cor",
        ]),
        new("libSystem.Runtime.InteropServices.so", [
            "stdlib/src/System/interop.cor",
        ]),
        new("libSystem.Runtime.Extensions.so", [
            "stdlib/src/System/time.cor",
            "stdlib/src/System/values.cor",
            "stdlib/src/System/environment.cor",
            "stdlib/src/System/Reflection/Assembly.cor",
            "stdlib/src/System/AppDomain.cor",
        ]),
        new("libSystem.Runtime.Numerics.so", [
            "stdlib/src/System/numerics.cor",
        ]),
        new("libSystem.Collections.so", [
            "stdlib/src/System/Collections/Collections.cor",
        ]),
        new("libSystem.Text.RegularExpressions.so", [
            "stdlib/src/System/Text/RegularExpressions.cor",
        ]),
        new("libSystem.IO.so", [
            "stdlib/src/System/IO/io.cor",
            "stdlib/src/System/IO/io-streams.cor",
            "stdlib/src/System/IO/timezone.cor",
            "stdlib/src/System/IO/watcher.cor",
        ]),
        new("libSystem.IO.Compression.so", [
            "stdlib/src/System/IO/compression.cor",
        ]),
        new("libSystem.Formats.Tar.so", [
            "stdlib/src/System/IO/tar.cor",
        ]),
        new("libSystem.Console.so", [
            "stdlib/src/System/console.cor",
        ]),
        new("libSystem.Net.so", [
            "stdlib/src/System/Net/Net.cor",
        ]),
        new("libSystem.Security.Cryptography.so", [
            "stdlib/src/System/Security/Cryptography/Cryptography.cor",
            "stdlib/src/System/Security/Cryptography/Hashing.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_base.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_der.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_random.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_sha512.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_chacha.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_poly1305.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_chachapoly.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_aes.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_gcm.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_field25519.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_x25519.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_ed25519.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_p256_field.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_p256.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_p384.cor",
            "stdlib/src/Corsac/Security/Cryptography/crypto_rsa.cor",
            "stdlib/src/System/Security/Cryptography/Oid.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509Der.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509CertificateView.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509PathChecks.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509Certificate.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509Store.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509Chain.cor",
            "stdlib/src/System/Security/Cryptography/X509Certificates/X509KeyExtensions.cor",
            "stdlib/src/System/Security/Cryptography/HKDF.cor",
            "stdlib/src/System/Security/Cryptography/Aead.cor",
            "stdlib/src/System/Security/Cryptography/Asymmetric.cor",
        ]),
        new("libSystem.Net.Security.so", [
            "stdlib/src/System/Net/SslPolicyErrors.cor",
            "stdlib/src/System/Security/Authentication/SslProtocols.cor",
            "stdlib/src/System/Security/Authentication/AuthenticationException.cor",
            "stdlib/src/System/Net/Security/tls.cor",
            "stdlib/src/System/Net/Security/tls13.cor",
            "stdlib/src/System/Net/Security/tls_aesgcm.cor",
            "stdlib/src/System/Net/Security/tls_stream.cor",
            "stdlib/src/System/Net/Security/tls_handshake.cor",
            "stdlib/src/System/Net/Security/tls_hello.cor",
            "stdlib/src/System/Net/Security/tls_policy.cor",
            "stdlib/src/System/Net/Security/tls_client.cor",
            "stdlib/src/System/Net/Security/SslStream.cor",
        ]),
        new("libMono.Posix.so", [
            "stdlib/src/System/unix.cor",
            "stdlib/src/System/power.cor",
        ]),
        new("libSystem.Diagnostics.Process.so", [
            "stdlib/src/System/process.cor",
        ]),
        new("libSystem.ServiceProcess.so", [
            "stdlib/src/System/ServiceProcess/ServiceWire.cor",
            "stdlib/src/System/ServiceProcess/ServiceProcess.cor",
        ]),
        new("libSystem.Security.Cryptography.ProtectedData.so", [
            "stdlib/src/System/Security/Cryptography/ProtectedData.cor",
        ]),
        new("libSystem.Xml.so", [
            "stdlib/src/System/Xml/Xml.cor",
        ]),
        new("libSystem.Text.Json.so", [
            "stdlib/src/System/Text/Json/Json.cor",
        ]),
        new("libMicrosoft.Win32.Registry.so", [
            "stdlib/src/Microsoft/Win32/Registry.cor",
        ]),
        new("libSystem.Runtime.InteropServices.Com.so", [
            "stdlib/src/System/Runtime/InteropServices/ComInterop.cor",
            "stdlib/src/System/Runtime/InteropServices/ComNdr.cor",
            "stdlib/src/System/Runtime/InteropServices/ComRemote.cor",
            "stdlib/src/System/Runtime/InteropServices/ComOle.cor",
        ]),
        new("libMicrosoft.CSharp.so", [
            "stdlib/src/System/Dynamic/Dynamic.cor",
        ]),
        new("libSystem.Windows.Forms.so", [
            "stdlib/src/System/Drawing/Drawing.cor",
            "stdlib/src/System/Drawing/Drawing2D.cor",
            "stdlib/src/System/Drawing/FontEngine.cor",
            "stdlib/src/System/Drawing/TrueType.cor",
            "stdlib/src/System/Drawing/BitmapFont.cor",
            "stdlib/src/System/Drawing/Text.cor",
            "stdlib/src/System/Drawing/Imaging.cor",
            "stdlib/src/Corsac/GUI/Gui.cor",
            "stdlib/src/System/Windows/Forms/Forms.cor",
            "stdlib/src/Microsoft/Win32/SystemEvents.cor",
            "stdlib/src/System/Drawing/Printing/SpoolWire.cor",
            "stdlib/src/System/Drawing/Printing/Printing.cor",
            "stdlib/src/System/Windows/Forms/PrintDialogs.cor",
            "stdlib/src/System/Windows/Forms/PrintPreview.cor",
        ]),
    ];

    /// <summary>
    /// What is wrong with the table against the library root's hosted
    /// sources: a source no library owns, one owned twice, one that does not
    /// exist. Empty when it is right.
    /// </summary>
    internal static List<string> Check(string root)
    {
        List<string> problems = new();
        Dictionary<string, string> owner = new(StringComparer.Ordinal);
        foreach (Library library in Table)
        {
            foreach (string source in library.Sources)
            {
                if (!owner.TryAdd(source, library.Soname))
                    problems.Add($"{source} is owned by both {owner[source]} and {library.Soname}");
                if (!File.Exists(Path.Combine(root, source)))
                    problems.Add($"{library.Soname} owns {source}, which does not exist");
            }
        }
        foreach (string path in Driver.DefaultLibraries(Target.X86))
        {
            string source = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!owner.ContainsKey(source)) problems.Add($"no shared library owns {source}");
        }
        return problems;
    }

    /// <summary>
    /// One line per library, in build order: `soname|source source ...`, the
    /// sources relative to the library root.
    /// </summary>
    internal static string Text() =>
        string.Concat(Table.Select(library => library.Soname + "|" + string.Join(' ', library.Sources) + "\n"));
}
