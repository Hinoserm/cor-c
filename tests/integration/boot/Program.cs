using System.Diagnostics;
using System.Text;

namespace Corsac.Tests.Boot;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string work = Path.GetFullPath(args.Single());
        ProcessStartInfo start = new("qemu-system-i386")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-machine", "isapc", "-cpu", "486", "-m", "32", "-accel", "tcg",
            "-drive", "file=" + Path.Combine(work, "disc.img") + ",format=raw,if=ide,index=0",
            "-nic", "none", "-display", "none", "-serial", "stdio", "-no-reboot",
            "-d", "guest_errors", "-D", Path.Combine(work, "qemu-debug.log") }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("QEMU did not start");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using StreamWriter log = new(Path.Combine(work, "serial.log")) { AutoFlush = true };
        StringBuilder pending = new();
        char[] buffer = new char[4096];
        async Task Until(string marker)
        {
            while (true)
            {
                int found = pending.ToString().IndexOf(marker, StringComparison.Ordinal);
                if (found >= 0) { pending.Remove(0, found + marker.Length); return; }
                int count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token);
                if (count == 0) throw new IOException("Guest exited before marker: " + marker);
                string text = new string(buffer, 0, count).Replace("\r", "");
                await log.WriteAsync(text); pending.Append(text);
                if (pending.Length > 1024 * 1024) throw new IOException("Guest output exceeded diagnostic budget before marker: " + marker);
            }
        }
        async Task Command(string command, string marker)
        {
            await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync();
            await Until(marker); await Until("# ");
        }
        try
        {
            await Until("login: ");
            await process.StandardInput.WriteLineAsync("root"); await process.StandardInput.FlushAsync();
            await Until("# ");
            await Command("cat /proc/mounts", "/dev/hda1 / ext3");
            await Command("tty", "\n/dev/ttyS0\n");
            await Command("ps -eo pid,comm", "init");
            await Command("echo CORC-SPLIT-BOOT-PASS", "\nCORC-SPLIT-BOOT-PASS\n");
            await process.StandardInput.WriteLineAsync("reboot"); await process.StandardInput.FlushAsync();
            await Until("Restarting system.");
            await process.WaitForExitAsync(timeout.Token);
            await log.WriteAsync(await process.StandardOutput.ReadToEndAsync());
            if (process.ExitCode != 0) throw new IOException("QEMU exit " + process.ExitCode);
            Console.WriteLine("PASS stage2 loads kernel, init mounts root/proc, serial login/shell/exec work, reboot completes");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL actual CORSAC boot: " + error.Message + "; diagnostics: " + work);
            return 1;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(work, "qemu-stderr.log"), await errors);
        }
    }
}
