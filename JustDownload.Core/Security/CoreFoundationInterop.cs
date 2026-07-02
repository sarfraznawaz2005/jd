using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace JustDownload.Core.Security;

/// <summary>
/// Source-generated P/Invoke declarations against Apple's own CoreFoundation.framework, used to build
/// the CFString/CFData/CFDictionary values the Security.framework Keychain calls
/// (<see cref="SecurityFrameworkInterop"/>) expect, and to read a returned CFData back out. No native
/// shim, no bundled binary — this loads the OS-provided framework directly (CLAUDE.md §4).
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class CoreFoundationInterop
{
    internal const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint EncodingUtf8 = 0x0800_0100; // kCFStringEncodingUTF8

    [LibraryImport(Library)]
    private static partial IntPtr CFStringCreateWithBytes(
        IntPtr alloc,
        byte[] bytes,
        nint numBytes,
        uint encoding,
        [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [LibraryImport(Library)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(Library)]
    internal static partial IntPtr CFDictionaryCreate(
        IntPtr allocator,
        IntPtr[] keys,
        IntPtr[] values,
        nint numValues,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks);

    [LibraryImport(Library)]
    internal static partial void CFRelease(IntPtr cf);

    [LibraryImport(Library)]
    private static partial nint CFDataGetLength(IntPtr theData);

    [LibraryImport(Library)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr theData);

    /// <summary>Creates a CFString holding the UTF-8 encoding of <paramref name="value"/>.</summary>
    internal static IntPtr CreateCFString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return CFStringCreateWithBytes(IntPtr.Zero, utf8, utf8.Length, EncodingUtf8, isExternalRepresentation: false);
    }

    /// <summary>Creates a CFData holding the UTF-8 bytes of <paramref name="value"/>.</summary>
    internal static IntPtr CreateCFData(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return CFDataCreate(IntPtr.Zero, utf8, utf8.Length);
    }

    /// <summary>Reads a CFData's raw bytes back out as a UTF-8 string.</summary>
    internal static string ReadCFDataAsUtf8String(IntPtr data)
    {
        nint length = CFDataGetLength(data);
        if (length == 0)
        {
            return string.Empty;
        }

        IntPtr bytePtr = CFDataGetBytePtr(data);
        byte[] buffer = new byte[length];
        Marshal.Copy(bytePtr, buffer, 0, (int)length);
        return Encoding.UTF8.GetString(buffer);
    }
}
