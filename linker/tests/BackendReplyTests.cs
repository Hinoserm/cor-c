using System.Text;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// A backend's error reply: whatever it failed on is said whole or cut,
/// never a second failure in the saying (BackendProtocol.Fitted) -- a text
/// over the protocol's length, a NUL, a lone surrogate the strict encoder
/// cannot write.
/// </summary>
public static class BackendReplyTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }

        string Said(string error)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, BackendProtocol.Utf8, leaveOpen: true))
                BackendProtocol.WriteResponse(writer, error);
            stream.Position = 0;
            using BinaryReader reader = new(stream, BackendProtocol.Utf8);
            try { BackendProtocol.ReadResponse(reader); }
            catch (InvalidDataException failed) when (failed.Message.StartsWith("Compiler backend: ", StringComparison.Ordinal))
            {
                return failed.Message["Compiler backend: ".Length..];
            }
            throw new Exception("an error reply was not read as the backend's error");
        }

        Check(Said("plain error") == "plain error", "a short error is not said as it was");
        Check(Said("a\0b") == "a\\0b", "a NUL is not spelled out");
        Check(Said("x\uD800y") == "x\\uD800y", "a lone surrogate is not spelled out");
        Check(Said("pair 😀 kept") == "pair 😀 kept", "a surrogate pair is not kept");

        string huge = new string('e', 70000) + "😀";
        string cut = Said(huge);
        Check(Encoding.UTF8.GetByteCount(cut) <= BackendProtocol.MaxText, "a cut error is still over the protocol's length");
        Check(cut.StartsWith(new string('e', 60000), StringComparison.Ordinal), "a cut error lost its start");
        Check(cut.Contains("more characters cut", StringComparison.Ordinal), "a cut error does not say it was cut");

        // Cut exactly where a pair starts: never half of it.
        string atPair = new string('e', BackendProtocol.MaxText - 100) + string.Concat(Enumerable.Repeat("😀", 100));
        string pairCut = Said(atPair);
        Check(!pairCut.Any(char.IsSurrogate) || pairCut.Count(char.IsHighSurrogate) == pairCut.Count(char.IsLowSurrogate), "a cut split a surrogate pair");

        Check(BackendProtocol.Fitted(new string('n', BackendProtocol.MaxText)) is { } full
              && Encoding.UTF8.GetByteCount(full) <= BackendProtocol.MaxText, "an error of exactly the length was not fitted");
    }
}
