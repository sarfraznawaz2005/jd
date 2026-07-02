using System.Runtime.Versioning;

namespace JustDownload.Core.Security;

/// <summary>
/// Real macOS Keychain backend for <see cref="IMacKeychainInterop"/>. Builds the CFDictionary
/// query/attribute sets <c>SecItemAdd</c>/<c>SecItemCopyMatching</c>/<c>SecItemDelete</c> expect via
/// <see cref="CoreFoundationInterop"/>, and releases every CoreFoundation object it creates
/// (<c>CFDictionaryCreate</c> retains each key/value passed to it, so once the dictionary is built the
/// per-call references created just to populate it can be released immediately).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainInterop : IMacKeychainInterop
{
    public int Add(string service, string account, string secret)
    {
        IntPtr serviceRef = CoreFoundationInterop.CreateCFString(service);
        IntPtr accountRef = CoreFoundationInterop.CreateCFString(account);
        IntPtr secretData = CoreFoundationInterop.CreateCFData(secret);
        IntPtr attributes = CreateDictionary(
            (SecurityFrameworkConstants.KSecClass, SecurityFrameworkConstants.KSecClassGenericPassword),
            (SecurityFrameworkConstants.KSecAttrService, serviceRef),
            (SecurityFrameworkConstants.KSecAttrAccount, accountRef),
            (SecurityFrameworkConstants.KSecValueData, secretData));

        try
        {
            return SecurityFrameworkInterop.SecItemAdd(attributes, IntPtr.Zero);
        }
        finally
        {
            Release(serviceRef, accountRef, secretData, attributes);
        }
    }

    public (int Status, string? Secret) CopyMatching(string service, string account)
    {
        IntPtr serviceRef = CoreFoundationInterop.CreateCFString(service);
        IntPtr accountRef = CoreFoundationInterop.CreateCFString(account);
        IntPtr query = CreateDictionary(
            (SecurityFrameworkConstants.KSecClass, SecurityFrameworkConstants.KSecClassGenericPassword),
            (SecurityFrameworkConstants.KSecAttrService, serviceRef),
            (SecurityFrameworkConstants.KSecAttrAccount, accountRef),
            (SecurityFrameworkConstants.KSecReturnData, SecurityFrameworkConstants.KCFBooleanTrue),
            (SecurityFrameworkConstants.KSecMatchLimit, SecurityFrameworkConstants.KSecMatchLimitOne));

        int status = SecurityFrameworkInterop.SecItemCopyMatching(query, out IntPtr result);
        try
        {
            // Apple's docs leave 'result' undefined when the call fails, so it's only read/released
            // once status confirms success.
            return status == 0 ? (status, CoreFoundationInterop.ReadCFDataAsUtf8String(result)) : (status, null);
        }
        finally
        {
            Release(serviceRef, accountRef, query);
            if (status == 0)
            {
                Release(result);
            }
        }
    }

    public int Delete(string service, string account)
    {
        IntPtr serviceRef = CoreFoundationInterop.CreateCFString(service);
        IntPtr accountRef = CoreFoundationInterop.CreateCFString(account);
        IntPtr query = CreateDictionary(
            (SecurityFrameworkConstants.KSecClass, SecurityFrameworkConstants.KSecClassGenericPassword),
            (SecurityFrameworkConstants.KSecAttrService, serviceRef),
            (SecurityFrameworkConstants.KSecAttrAccount, accountRef));

        try
        {
            return SecurityFrameworkInterop.SecItemDelete(query);
        }
        finally
        {
            Release(serviceRef, accountRef, query);
        }
    }

    private static IntPtr CreateDictionary(params (IntPtr Key, IntPtr Value)[] entries)
    {
        var keys = new IntPtr[entries.Length];
        var values = new IntPtr[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }

        return CoreFoundationInterop.CFDictionaryCreate(
            IntPtr.Zero,
            keys,
            values,
            entries.Length,
            SecurityFrameworkConstants.KCFTypeDictionaryKeyCallBacks,
            SecurityFrameworkConstants.KCFTypeDictionaryValueCallBacks);
    }

    private static void Release(params IntPtr[] refs)
    {
        foreach (IntPtr cfRef in refs)
        {
            if (cfRef != IntPtr.Zero)
            {
                CoreFoundationInterop.CFRelease(cfRef);
            }
        }
    }
}
