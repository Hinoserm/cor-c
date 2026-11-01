using System.Security.Cryptography;
using System.Text;

namespace Corsac.Projects;

public static class ProjectState
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>
    /// WHICH WRITE OF A FILE, WHEREVER IT IS: its name, when it was last
    /// written and its length -- FileIdentity without the directory. What a
    /// stamp kept in a build tree records of a file, so that the tree copied
    /// elsewhere (with its times, as rsync -a and cp -a keep them) finds its
    /// stamps current: with the whole path in them, a copy of a kernel's
    /// build compiled all 245 units again.
    /// </summary>
    public static string WriteIdentity(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists) throw new FileNotFoundException("No such file: " + path, path);
        return file.Name + "\n" + file.LastWriteTimeUtc.Ticks + "\n" + file.Length;
    }

    /// <summary>
    /// A path as a stamp keeps it: relative to <paramref name="root"/> where
    /// it is under it, so that a tree copied elsewhere names its files as it
    /// did; as it is anywhere else.
    /// </summary>
    public static string Placed(string path, string root)
    {
        string full = Path.GetFullPath(path), under = Path.GetRelativePath(Path.GetFullPath(root), full);
        return under.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(under) ? full : under.Replace('\\', '/');
    }

    /// <summary>
    /// An option as a stamp hashes it: a path -- one written from the root,
    /// or one with a directory in it that names a file or directory here --
    /// as `<path>`, anything else as it is. What a file holds is a stamp's
    /// to say by other means (a hash, a receipt); where it is says nothing
    /// a copied tree should rebuild for. A bare word stays a word:
    /// `--target x86` beside a directory called x86 is still the target.
    /// </summary>
    public static string Unplaced(string option)
        => option.Length > 0 && option[0] != '-'
           && (Path.IsPathRooted(option) || option.Contains('/') && (File.Exists(option) || Directory.Exists(option))) ? "<path>" : option;

    /// <summary>
    /// WHICH FILE, AND WHICH WRITE OF IT: its path, when it was last written
    /// and its length. What a build compares between runs and across a build,
    /// as make does. It also hashed every byte with SHA-256, and it is asked
    /// of every library source in every unit compiler process (ProjectCompile's
    /// references), of every object and the image at the link, of the native
    /// compiler itself (ToolIdentity) -- tens of megabytes of SHA-256 a build
    /// step, a fifth of a native unit compile's time, to learn what the
    /// write time already says. A file written again with the same bytes is
    /// taken as changed, which costs a rebuild and never a stale object.
    /// </summary>
    public static string FileIdentity(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists) throw new FileNotFoundException("No such file: " + path, path);
        return path + "\n" + file.LastWriteTimeUtc.Ticks + "\n" + file.Length;
    }
    public static bool Current(string state, string signature, string output)
        => File.Exists(state) && File.Exists(output) && File.ReadAllText(state) == signature + "\n" + WriteIdentity(output);
    public static void Record(string state, string signature, string output)
    {
        string temporary = state + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, signature + "\n" + WriteIdentity(output));
        File.Move(temporary, state, overwrite: true);
    }
}
