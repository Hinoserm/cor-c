using System.Text;

namespace Corsac.Build;

/// <summary>Keep raw subprocess logs while forwarding bounded progress records live.</summary>
public static class ProgressOutput
{
    public const string Prefix = "build-progress: ";
    public static async Task Copy(Stream source, Stream log, Action<string> report)
    {
        byte[] buffer = new byte[8192], line = new byte[8192];
        int length = 0;
        bool oversized = false;
        void Publish()
        {
            if (!oversized && length >= Prefix.Length)
            {
                bool match = true;
                for (int i = 0; i < Prefix.Length; i++) if (line[i] != Prefix[i]) match = false;
                if (match) report(Encoding.UTF8.GetString(line, Prefix.Length, length - Prefix.Length).TrimEnd('\r'));
            }
            length = 0; oversized = false;
        }
        int count;
        while ((count = await source.ReadAsync(buffer)) != 0)
        {
            await log.WriteAsync(buffer.AsMemory(0, count));
            for (int i = 0; i < count; i++)
            {
                byte value = buffer[i];
                if (value == 10) Publish();
                else if (length < line.Length) line[length++] = value;
                else oversized = true;
            }
        }
        if (length != 0) Publish();
    }
}
