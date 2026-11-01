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
    public static string Digest(IReadOnlyList<string> args, IReadOnlyList<string> objects, IReadOnlyList<ObjectFile> read,
        string? keepPath, KernelExports? kernel, IReadOnlyList<string> sharedLibraries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Say(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text + "\n"); hash.AppendData(bytes); }
        Say("link receipt 1");
        foreach (string file in LinkerFiles()) Say(file);
        foreach (string arg in args) Say(Unplaced(arg));
        foreach (string path in objects) Say(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
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
            List<(string Name, HashSet<string> Exports)> theirs = sharedLibraries.Distinct(StringComparer.Ordinal)
                .Select(path => (Path.GetFileName(path), new HashSet<string>(ElfReader.ExportsOf(File.ReadAllBytes(path)), StringComparer.Ordinal))).ToList();
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

    /// <summary>Which write of the image it is: its length and time, the image changed by anything else is not current.</summary>
    private static string Written(string path)
    {
        FileInfo file = new(path);
        return file.Length + " " + file.LastWriteTimeUtc.Ticks;
    }

    /// <summary>The linker's own files by name, time and length: a linker built again links again.</summary>
    private static IEnumerable<string> LinkerFiles()
    {
        List<string> files = new();
        if (Environment.ProcessPath is string self && File.Exists(self)) files.Add(self);
        string assembly = typeof(LinkReceipt).Assembly.Location;
        if (assembly.Length > 0 && File.Exists(assembly)) files.Add(assembly);
        return files.Distinct(StringComparer.Ordinal).Select(path =>
        {
            FileInfo file = new(path);
            return file.Name + " " + file.Length + " " + file.LastWriteTimeUtc.Ticks;
        });
    }

    /// <summary>An option as the receipt keeps it: a path as `&lt;path&gt;` (ProjectState.Unplaced), the output's own name kept, which a shared object's soname is.</summary>
    private static string Unplaced(string option)
        => option.Length > 0 && option[0] != '-'
           && (Path.IsPathRooted(option) || option.Contains('/') && (File.Exists(option) || Directory.Exists(option))) ? "<path>/" + Path.GetFileName(option) : option;
}
