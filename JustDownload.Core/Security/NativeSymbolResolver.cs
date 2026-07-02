using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JustDownload.Core.Security;

/// <summary>
/// Loads a macOS framework binary and reads its exported global-data symbols. Apple's
/// Security.framework/CoreFoundation.framework export constants like <c>kSecClass</c> as data, not
/// functions, so <c>[LibraryImport]</c> can't bind them — they must be resolved by name via
/// <see cref="NativeLibrary.GetExport"/> instead.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class NativeSymbolResolver
{
    /// <summary>Loads the framework binary at <paramref name="path"/> and returns its module handle.</summary>
    internal static IntPtr LoadFramework(string path) => NativeLibrary.Load(path);

    /// <summary>
    /// Reads a <b>pointer-typed</b> exported constant (C declaration <c>extern const CFTypeRef kX;</c>):
    /// the symbol's address holds the CFTypeRef <i>value</i>, so it is dereferenced once.
    /// </summary>
    internal static IntPtr GetPointerSymbol(IntPtr library, string symbolName) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbolName));

    /// <summary>
    /// Reads a <b>struct-typed</b> exported constant (e.g. <c>kCFTypeDictionaryKeyCallBacks</c>):
    /// the symbol's own address already <i>is</i> the pointer CoreFoundation APIs expect, so — unlike
    /// <see cref="GetPointerSymbol"/> — no dereference happens.
    /// </summary>
    internal static IntPtr GetStructSymbol(IntPtr library, string symbolName) =>
        NativeLibrary.GetExport(library, symbolName);
}
