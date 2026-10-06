using System.Security.Cryptography;
using System.Text;

namespace Corsac.Projects;

public static class ProjectState
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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
        => File.Exists(state) && File.Exists(output) && File.ReadAllText(state) == signature + "\n" + FileIdentity(output);
    public static void Record(string state, string signature, string output)
    {
        string temporary = state + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, signature + "\n" + FileIdentity(output));
        File.Move(temporary, state, overwrite: true);
    }
}
