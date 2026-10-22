namespace Corsac.Lang;

/// <summary>
/// THE NAMESPACE .NET KEEPS EACH OF THE CLASS LIBRARY'S GLOBAL TYPES IN. The
/// library declares List, Console, File and the rest at the top level, and
/// `System.X` names them (Binder.LibraryGlobal); a program sees one written
/// bare only where C# would -- inside that namespace or under a using
/// directive of it (Binder.LibraryNamespaceInScope). A global type of the
/// library's own that .NET has no type of the name for (Os, Gc, Runtime)
/// is in no namespace and is seen everywhere.
///
/// Made from .NET 10's shared framework: the namespace of every public
/// top-level type of the name, for each global type of the library that has
/// one -- none had two. Crc32, the compression code's own table and not
/// System.IO.Hashing's class, is left out. A global type added to the
/// library with a .NET type's name is added here.
/// </summary>
internal static class LibraryNamespaces
{
    public static string? Of(string key) => Table.TryGetValue(key, out string? space) ? space : null;

    private static readonly Dictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        void In(string space, params string[] keys) { foreach (string key in keys) table.Add(key, space); }
        In("System", "Action", "Action`1", "Action`2", "Action`3", "Action`4", "Action`5", "Action`6", "Action`7",
            "Action`8", "Activator", "AppContext", "ApplicationException", "ArgumentException",
            "ArgumentNullException", "ArgumentOutOfRangeException", "ArithmeticException", "Array", "ArraySegment`1",
            "ArrayTypeMismatchException", "Attribute", "AttributeTargets", "AttributeUsageAttribute",
            "Base64FormattingOptions", "BitConverter", "Boolean", "Byte", "Char", "Comparison`1", "Console",
            "ConsoleCancelEventArgs", "ConsoleCancelEventHandler", "ConsoleColor", "ConsoleKey", "ConsoleKeyInfo",
            "ConsoleModifiers", "ConsoleSpecialKey", "Convert", "Converter`2", "DateOnly", "DateTime", "DateTimeKind",
            "DateTimeOffset", "DayOfWeek", "Decimal", "Delegate", "DivideByZeroException", "Double",
            "DuplicateWaitObjectException", "Environment", "Exception", "FormatException", "Func`1", "Func`2",
            "Func`3", "Func`4", "Func`5", "Func`6", "Func`7", "Func`8", "Func`9", "Guid", "Half", "HashCode",
            "ICloneable", "IComparable", "IComparable`1", "ICustomFormatter", "IDisposable", "IEquatable`1",
            "IFormatProvider", "IFormattable", "Index", "IndexOutOfRangeException", "Int128", "Int16", "Int32",
            "Int64", "IntPtr", "InvalidCastException", "InvalidOperationException", "InvalidTimeZoneException",
            "Lazy`1", "Math", "MathF", "MemberAccessException", "MemoryExtensions", "Memory`1", "MidpointRounding",
            "MissingMemberException", "MissingMethodException", "NotImplementedException", "NotSupportedException",
            "NullReferenceException", "ObjectDisposedException", "OperatingSystem", "OperationCanceledException",
            "OutOfMemoryException", "OverflowException", "PlatformNotSupportedException", "Predicate`1", "Random",
            "Range", "ReadOnlyMemory`1", "ReadOnlySpan`1", "SByte", "Single", "Span`1", "String", "StringComparer",
            "StringComparison", "StringSplitOptions", "SystemException", "TimeOnly", "TimeSpan", "TimeZoneInfo",
            "TimeZoneNotFoundException", "TimeoutException", "Tuple", "Tuple`2", "Tuple`3", "Tuple`4", "TypeCode",
            "TypeInitializationException", "UInt128", "UInt16", "UInt32", "UInt64", "UIntPtr",
            "UnauthorizedAccessException", "Uri", "UriFormatException", "UriKind");
        In("System.Buffers.Binary", "BinaryPrimitives");
        In("System.Collections", "BitArray", "IEnumerable", "IEnumerator");
        In("System.Collections.Generic", "Comparer`1", "Dictionary`2", "EqualityComparer`1", "HashSet`1",
            "IAsyncEnumerable`1", "IAsyncEnumerator`1", "ICollection`1", "IComparer`1", "IDictionary`2",
            "IEnumerable`1", "IEnumerator`1", "IEqualityComparer`1", "IList`1", "IReadOnlyCollection`1",
            "IReadOnlyDictionary`2", "IReadOnlyList`1", "IReadOnlySet`1", "ISet`1", "KeyNotFoundException",
            "KeyValuePair`2", "LinkedListNode`1", "LinkedList`1", "List`1", "PriorityQueue`2", "Queue`1",
            "ReferenceEqualityComparer", "SortedDictionary`2", "SortedList`2", "SortedSet`1", "Stack`1");
        In("System.Collections.ObjectModel", "ReadOnlyDictionary`2");
        In("System.Collections.Specialized", "StringCollection");
        In("System.ComponentModel", "Win32Exception");
        In("System.Diagnostics", "Stopwatch");
        In("System.Formats.Tar", "GnuTarEntry", "PaxTarEntry", "PosixTarEntry", "TarEntry", "TarEntryFormat",
            "TarEntryType", "TarFile", "TarReader", "TarWriter", "UstarTarEntry", "V7TarEntry");
        In("System.Globalization", "CultureInfo", "NumberStyles");
        In("System.IO", "BinaryReader", "BinaryWriter", "BufferedStream", "Directory", "DirectoryInfo", "DriveInfo",
            "DriveType", "ErrorEventArgs", "ErrorEventHandler", "File", "FileAccess", "FileAttributes", "FileInfo",
            "FileMode", "FileOptions", "FileShare", "FileStream", "FileSystemEventArgs", "FileSystemEventHandler",
            "FileSystemInfo", "FileSystemWatcher", "MemoryStream", "NotifyFilters", "Path", "RandomAccess",
            "RenamedEventArgs", "RenamedEventHandler", "SearchOption", "SeekOrigin", "Stream", "StreamReader",
            "StreamWriter", "StringReader", "StringWriter", "TextReader", "TextWriter", "UnixFileMode",
            "WaitForChangedResult", "WatcherChangeTypes");
        In("System.IO.Compression", "CompressionLevel", "CompressionMode", "DeflateStream", "GZipStream",
            "ZLibStream");
        In("System.IO.Enumeration", "FileSystemName");
        In("System.Linq", "Enumerable", "IGrouping`2", "ILookup`2", "IOrderedEnumerable`1", "Lookup`2");
        In("System.Net", "Dns", "IPAddress", "IPEndPoint", "IPHostEntry");
        In("System.Net.Sockets", "AddressFamily", "NetworkStream", "ProtocolType", "SelectMode", "Socket",
            "SocketError", "SocketException", "SocketFlags", "SocketShutdown", "SocketType", "TcpClient",
            "TcpListener", "UdpClient");
        In("System.Numerics", "BigInteger", "BitOperations", "Complex");
        In("System.Runtime.CompilerServices", "ConfiguredTaskAwaitable", "ConfiguredTaskAwaitable`1",
            "RuntimeHelpers", "SwitchExpressionException", "TaskAwaiter", "TaskAwaiter`1", "YieldAwaitable");
        In("System.Runtime.InteropServices", "CollectionsMarshal", "ExternalException", "Marshal", "PosixSignal",
            "PosixSignalContext", "PosixSignalRegistration");
        In("System.Security.Cryptography", "CryptoStream", "CryptoStreamMode", "CryptographicException", "HMAC",
            "HMACMD5", "HMACSHA1", "HMACSHA256", "HMACSHA384", "HMACSHA512", "HashAlgorithm", "HashAlgorithmName",
            "ICryptoTransform", "IncrementalHash", "KeyedHashAlgorithm", "MD5", "RandomNumberGenerator", "SHA1",
            "SHA256", "SHA384", "SHA512");
        In("System.Text", "ASCIIEncoding", "Encoding", "Rune", "StringBuilder", "StringRuneEnumerator",
            "UTF32Encoding", "UTF8Encoding", "UnicodeEncoding");
        In("System.Text.RegularExpressions", "Capture", "CaptureCollection", "Group", "GroupCollection", "Match",
            "MatchCollection", "MatchEvaluator", "Regex", "RegexMatchTimeoutException", "RegexOptions",
            "RegexParseError", "RegexParseException");
        In("System.Threading", "CancellationToken", "CancellationTokenRegistration", "CancellationTokenSource",
            "ManualResetEventSlim", "Monitor", "SynchronizationLockException", "ThreadPool", "Timeout");
        In("System.Threading.Tasks", "Task", "TaskCanceledException", "TaskCompletionSource",
            "TaskCompletionSource`1", "TaskStatus", "Task`1");
        return table;
    }
}
