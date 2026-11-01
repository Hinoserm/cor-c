using System.Text;

namespace Corsac.Build;

/// <summary>
/// A subprocess's output, KEPT AND SHOWN WHOLE: every byte to its log, and
/// every line said live as it comes. Only `build-progress: ` lines were said
/// once, and a compiler's "unit N/M ... rebuilt: why" and a tool's own
/// messages were in the logs alone (Hino: a build shows the full compiler
/// output and every message). The prefix is still taken off where a tool
/// writes it, so a tool that marked its lines to get them through reads the
/// same now that everything gets through.
/// </summary>
public static class ProgressOutput
{
    public const string Prefix = "build-progress: ";

    /// <summary>The longest line said whole; a longer one is said this far, and how much was left.</summary>
    private const int Longest = 64 * 1024;

    public static async Task Copy(Stream source, Stream log, Action<string> report)
    {
        byte[] buffer = new byte[8192];
        MemoryStream line = new();
        long cut = 0;
        void Publish()
        {
            byte[] bytes = line.GetBuffer();
            int length = (int)line.Length;
            int from = length >= Prefix.Length && Encoding.ASCII.GetString(bytes, 0, Prefix.Length) == Prefix ? Prefix.Length : 0;
            string said = Encoding.UTF8.GetString(bytes, from, length - from).TrimEnd('\r');
            report(cut == 0 ? said : said + " ... (" + cut + " more bytes in the log)");
            line.SetLength(0);
            cut = 0;
        }
        int count;
        while ((count = await source.ReadAsync(buffer)) != 0)
        {
            await log.WriteAsync(buffer.AsMemory(0, count));
            for (int i = 0; i < count; i++)
            {
                byte value = buffer[i];
                if (value == 10) Publish();
                else if (line.Length < Longest) line.WriteByte(value);
                else cut++;
            }
        }
        if (line.Length != 0 || cut != 0) Publish();
    }
}
