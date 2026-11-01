#nullable enable
using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac;

/// <summary>
/// WHAT A LINK MADE ITS IMAGE FROM, beside the image (`&lt;output&gt;.linked`), so
/// that a link whose inputs say nothing new writes nothing: the linker's own
/// files, the options (a path among them as `&lt;path&gt;`, a tree copied elsewhere
/// links as it did), each object by its bytes, a file the link reads whole
/// (the keep list) by its bytes, and -- for an image linked against a kernel
/// or shared objects -- the kernel's machine contract and, for every name the
/// objects leave undefined, whether the kernel and each of the others define
/// it now. A MODULE IS HELD TO ITS KERNEL BY THE NAMES IT IMPORTS ALONE
/// (Linker.LinkModule: no address of the kernel's is in it), so a kernel
/// linked again leaves a module's image as it was unless one of the names it
/// imports came or went; every module of the kernel was linked again on
/// every kernel link.
///
/// What the link checks against the kernel beside its exports -- the wait
/// and float facts its handlers walk into -- is checked whether or not the
/// image is current (ObjectLinkCommand), so a check that would fail now
/// fails, and the image is left only when it would come out the same.
/// </summary>
public static class LinkReceipt
{
    public const string Suffix = ".linked";

    /// <summary>
    /// The digest of everything the image is made from, as one hex string;
    /// null where something it reads cannot be read (the link then goes on,
    /// and says so where it fails).
    /// </summary>
    /// <param name="objectHashes">Each object's bytes hashed as the link read them (ObjectLinkCommand): what the image is made of, not a later read.</param>
    public static string Digest(IReadOnlyList<string> args, IReadOnlyList<string> objectHashes, IReadOnlyList<ObjectFile> read,
        string? keepPath, KernelExports? kernel, IReadOnlyList<string> sharedLibraries, string? backendPath)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Say(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text + "\n"); hash.AppendData(bytes); }
        Say("link receipt 2");
        foreach (string file in LinkerFiles()) Say(file);
        if (backendPath is not null && File.Exists(backendPath)) Say("backend " + Written(backendPath));
        // THE SWITCHES THIS PROCESS WAS GIVEN, taken off the command line before
        // the link saw it (Switches.Take) and handed to every backend it starts:
        // --no-rta, --region-engine and --skip-passes change the image.
        foreach (string flag in Switches.ChildFlags) Say("switch " + flag);
        for (int i = 0; i < args.Count; i++)
        {
            // A path the options name, by its name and never by whether it
            // exists yet (the output's does not before its first link): what
            // the link reads is hashed below, by its bytes or its answers. The
            // run path is the image's own text, and is kept as written.
            if (args[i] is "-o" or "--kernel" or "--keep" or "--link-shared" or "--map" or "--lto-backend" or "--region-report" or "--unused-report"
                && i + 1 < args.Count)
            {
                Say(args[i] + " <path>/" + Path.GetFileName(args[i + 1]));
                i++;
            }
            else if (args[i].Length > 0 && args[i][0] != '-' && (i == 0 || !Valued(args[i - 1]))) Say("<object>/" + Path.GetFileName(args[i]));
            else Say(args[i]);
        }
        foreach (string objectHash in objectHashes) Say(objectHash);
        if (keepPath is not null) Say("keep " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keepPath))));
        if (kernel is not null)
        {
            Say("kernel " + Convert.ToHexString(kernel.Abi ?? Array.Empty<byte>()) + " " + Convert.ToHexString(kernel.Cpu ?? Array.Empty<byte>())
                + " " + (kernel.LongMode ? "64" : "32"));
        }
        if (kernel is not null || sharedLibraries.Count > 0)
        {
            // THE NAMES THE OBJECTS IMPORT, each with who defines it now: the
            // kernel, each shared object by its file's name (its soname is what
            // the image records). A call to another image's $inside copy asks
            // for the method itself too (Linker.InsideOrItself).
            HashSet<string> defined = new(read.SelectMany(obj => obj.Symbols).Where(s => s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
            SortedSet<string> asked = new(StringComparer.Ordinal);
            foreach (string name in read.SelectMany(obj => obj.Symbols).Where(s => !s.IsDefined && s.Name.Length > 0).Select(s => s.Name))
            {
                if (defined.Contains(name)) continue;
                asked.Add(name);
                if (name.EndsWith(Linker.InsideSuffix, StringComparison.Ordinal)) asked.Add(name[..^Linker.InsideSuffix.Length]);
            }
            // A MODULE'S DEPENDENCY by its soname (its DT_NEEDED) and the names
            // asked of it, as the kernel; ANY OTHER IMAGE'S by its bytes as well:
            // a program's link prebinds the library's addresses into it.
            List<(string Name, HashSet<string> Exports)> theirs = new();
            foreach (string path in sharedLibraries.Distinct(StringComparer.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(path);
                theirs.Add((Path.GetFileName(path), new HashSet<string>(ElfReader.ExportsOf(bytes), StringComparer.Ordinal)));
                Say("library " + Path.GetFileName(path) + " " + (kernel is null ? Convert.ToHexString(SHA256.HashData(bytes)) : (ElfReader.SoNameOf(bytes) ?? Path.GetFileName(path))));
            }
            foreach (string name in asked)
            {
                StringBuilder answer = new(name);
                if (kernel is not null) answer.Append(kernel.Names.Contains(name) ? " kernel" : " -");
                foreach ((string library, HashSet<string> exports) in theirs) answer.Append(exports.Contains(name) ? " " + library : " -");
                Say(answer.ToString());
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Whether the image beside its receipt is what these inputs make: the digest the same, the image as the link left it.</summary>
    public static bool Current(string output, string digest)
    {
        string receipt = output + Suffix;
        if (!File.Exists(output) || !File.Exists(receipt)) return false;
        string[] said = File.ReadAllLines(receipt);
        return said.Length == 2 && said[0] == digest && said[1] == Written(output);
    }

    /// <summary>The receipt of an image just written; a link that fails leaves none (Forget).</summary>
    public static void Record(string output, string digest)
    {
        string temporary = output + Suffix + "." + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temporary, new[] { digest, Written(output) });
        File.Move(temporary, output + Suffix, overwrite: true);
    }

    /// <summary>No receipt: the image is being made again, and one cut short is no image.</summary>
    public static void Forget(string output)
    {
        try { File.Delete(output + Suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether a link option takes a value after it, which is then no object.</summary>
    private static bool Valued(string option)
        => option is "-o" or "--kernel" or "--keep" or "--link-shared" or "--map" or "--lto-backend" or "--region-report" or "--unused-report"
            or "--runpath" or "--entry" or "--base" or "--paddr" or "--lto-import-bytes" or "--cpu" or "--fpu"
            or "--irq-waits" or "--wait-free-roots" or "--wait-free-stop" or "--irq-roots" or "--irq-stop";

    /// <summary>Which write of the image it is: its length and time, the image changed by anything else is not current.</summary>
    private static string Written(string path)
    {
        FileInfo file = new(path);
        return file.Length + " " + file.LastWriteTimeUtc.Ticks;
    }

    /// <summary>
    /// The linker's own files by name, time and length: a linker built again
    /// links again. Every assembly of the toolchain loaded here -- the
    /// compiler's too, whose passes the link's backends run -- or the one
    /// native image that holds them all.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty location is a native image's, which the process path stands for.")]
    private static IEnumerable<string> LinkerFiles()
    {
        List<string> files = new();
        if (Environment.ProcessPath is string self && File.Exists(self)) files.Add(self);
        string home = AppContext.BaseDirectory;
        foreach (System.Reflection.Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            string location = loaded.Location;
            if (location.Length > 0 && home.Length > 0 && location.StartsWith(home, StringComparison.Ordinal) && File.Exists(location)) files.Add(location);
        }
        return files.Distinct(StringComparer.Ordinal).Select(path =>
        {
            FileInfo file = new(path);
            return file.Name + " " + file.Length + " " + file.LastWriteTimeUtc.Ticks;
        });
    }

}
