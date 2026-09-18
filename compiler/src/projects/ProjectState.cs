using System.Security.Cryptography;
using System.Text;

namespace Corsac.Projects;

public static class ProjectState
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string FileIdentity(string path)
    {
        FileInfo file = new(path);
        using FileStream stream = file.OpenRead();
        using SHA256 hash = SHA256.Create();
        return path + "\n" + file.LastWriteTimeUtc.Ticks + "\n" + file.Length + "\n" + Convert.ToHexString(hash.ComputeHash(stream));
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
