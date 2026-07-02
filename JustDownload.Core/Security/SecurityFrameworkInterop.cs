using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JustDownload.Core.Security;

/// <summary>
/// Source-generated P/Invoke declarations against Apple's own Security.framework Keychain Item API.
/// Called only through <see cref="MacKeychainInterop"/>, which builds the CFDictionary arguments via
/// <see cref="CoreFoundationInterop"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class SecurityFrameworkInterop
{
    internal const string Library = "/System/Library/Frameworks/Security.framework/Security";

    [LibraryImport(Library)]
    internal static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(Library)]
    internal static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(Library)]
    internal static partial int SecItemDelete(IntPtr query);
}

/// <summary>
/// Resolves the CFTypeRef/struct constants Security.framework and CoreFoundation.framework export as
/// global data symbols (<c>kSecClass</c>, <c>kSecClassGenericPassword</c>, ...). These are not
/// functions and can't be P/Invoked; each is looked up by name via <see cref="NativeSymbolResolver"/>.
/// Hardcoding their values would be wrong — they are opaque addresses assigned at image-load time,
/// not stable literals.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class SecurityFrameworkConstants
{
    private static readonly IntPtr SecurityLibrary = NativeSymbolResolver.LoadFramework(SecurityFrameworkInterop.Library);
    private static readonly IntPtr CoreFoundationLibrary = NativeSymbolResolver.LoadFramework(CoreFoundationInterop.Library);

    internal static readonly IntPtr KSecClass = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecClass");
    internal static readonly IntPtr KSecClassGenericPassword = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecClassGenericPassword");
    internal static readonly IntPtr KSecAttrService = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecAttrService");
    internal static readonly IntPtr KSecAttrAccount = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecAttrAccount");
    internal static readonly IntPtr KSecValueData = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecValueData");
    internal static readonly IntPtr KSecReturnData = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecReturnData");
    internal static readonly IntPtr KSecMatchLimit = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecMatchLimit");
    internal static readonly IntPtr KSecMatchLimitOne = NativeSymbolResolver.GetPointerSymbol(SecurityLibrary, "kSecMatchLimitOne");
    internal static readonly IntPtr KCFBooleanTrue = NativeSymbolResolver.GetPointerSymbol(CoreFoundationLibrary, "kCFBooleanTrue");

    internal static readonly IntPtr KCFTypeDictionaryKeyCallBacks =
        NativeSymbolResolver.GetStructSymbol(CoreFoundationLibrary, "kCFTypeDictionaryKeyCallBacks");
    internal static readonly IntPtr KCFTypeDictionaryValueCallBacks =
        NativeSymbolResolver.GetStructSymbol(CoreFoundationLibrary, "kCFTypeDictionaryValueCallBacks");
}
