#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// C LIBRARIES BY THE NAME A [DllImport] GAVE. The loader wants the name a
/// library calls itself (DT_SONAME) in DT_NEEDED; a declaration may give that
/// ("libc.so.6"), a file ("/opt/x/libfoo.so"), or a bare name as .NET allows
/// ("c", "libm", "foo"), which is looked up the way .NET's own probing does
/// on Linux: lib{name}.so and {name}.so, then their versioned files, in the
/// system's library directories for this image's class -- the 32-bit ones
/// for an i386 image, the 64-bit ones for long mode.
/// </summary>
public static partial class Linker
{
    private static readonly string[] LibraryDirectories64 =
    {
        "/lib64", "/usr/lib64", "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu", "/usr/local/lib64", "/usr/local/lib",
    };

    private static readonly string[] LibraryDirectories32 =
    {
        "/lib", "/usr/lib", "/lib/i386-linux-gnu", "/usr/lib/i386-linux-gnu", "/lib32", "/usr/lib32", "/usr/local/lib",
    };

    /// <summary>The DT_NEEDED names of every C library the inputs' [DllImport]s name; the sections go.</summary>
    private static List<string> NativeNeeded(List<Input> inputs, bool longMode, List<string> errors)
    {
        List<string> needed = new();
        foreach (Input input in inputs)
        {
            foreach (string name in NativeLibraries.Read(input.Object))
            {
                string? soname = ResolveNative(name, longMode);
                if (soname is null)
                {
                    errors.Add($"{input.Name}: the C library '{name}' a [DllImport] names is not in {string.Join(", ", longMode ? LibraryDirectories64 : LibraryDirectories32)}");
                    continue;
                }
                if (!needed.Contains(soname))
                {
                    needed.Add(soname);
                }
            }
            input.Object.Sections.RemoveAll(s => s.Name == NativeLibraries.SectionName);
        }
        return needed;
    }

    private static string? ResolveNative(string name, bool longMode)
    {
        if (name.Contains('/'))
        {
            return File.Exists(name) ? SoNameOfLibrary(name, longMode) ?? Path.GetFileName(name) : null;
        }
        if (name.Contains(".so.", StringComparison.Ordinal))
        {
            // Versioned: the name the loader will be given is this one.
            return name;
        }
        string stem = name.EndsWith(".so", StringComparison.Ordinal) ? name[..^3] : name;
        List<string> bases = new() { stem };
        if (!stem.StartsWith("lib", StringComparison.Ordinal))
        {
            bases.Insert(0, "lib" + stem);
        }
        foreach (string directory in longMode ? LibraryDirectories64 : LibraryDirectories32)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }
            foreach (string b in bases)
            {
                // The development link first (it is what a C compiler would
                // use), then the versioned files, newest name last in order.
                string plain = Path.Combine(directory, b + ".so");
                if (SoNameOfLibrary(plain, longMode) is string found)
                {
                    return found;
                }
                foreach (string versioned in Directory.GetFiles(directory, b + ".so.*").Order(StringComparer.Ordinal))
                {
                    if (SoNameOfLibrary(versioned, longMode) is string named)
                    {
                        return named;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>A shared object's SONAME (or its file name), when it is an ELF of this image's class; null otherwise.</summary>
    private static string? SoNameOfLibrary(string path, bool longMode)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        // A GNU linker script (libc.so on glibc systems) is text, not ELF.
        if (bytes.Length < 20 || bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
        {
            return null;
        }
        if (bytes[4] != (longMode ? Elf.Class64 : Elf.Class32))
        {
            return null;
        }
        try
        {
            return ElfReader.SoNameOf(bytes) ?? Path.GetFileName(path);
        }
        catch (ElfFormatException)
        {
            return null;
        }
    }
}
