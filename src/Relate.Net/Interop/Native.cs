using System.Runtime.InteropServices;
using System.Text;

namespace Relate.Net.Interop;

/// <summary>Helpers shared by the interop layer: library checks, strings and error handling.</summary>
internal static unsafe class Native
{
    /// <summary>The C ABI major version this wrapper is written against.</summary>
    internal const int SupportedAbiVersion = 2;

    private static readonly Lazy<int> s_abiVersion = new(LoadAndCheck, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static int AbiVersion => s_abiVersion.Value;

    /// <summary>Verifies once per process that the native library loads and speaks ABI v2.</summary>
    internal static void EnsureLoaded() => _ = s_abiVersion.Value;

    private static int LoadAndCheck()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"Relate.Net ships the relate native library for Windows x64 only; this process is {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.");
        }

        int version;
        try
        {
            version = NativeMethods.relate_abi_version();
        }
        catch (DllNotFoundException ex)
        {
            throw new DllNotFoundException(
                $"The relate native library '{NativeMethods.LibraryName}.dll' could not be loaded. " +
                "It must be next to Relate.Net.dll or in runtimes/win-x64/native.", ex);
        }

        if (version != SupportedAbiVersion)
        {
            throw new NotSupportedException(
                $"The loaded relate native library implements C ABI version {version}, but Relate.Net requires version {SupportedAbiVersion}.");
        }

        return version;
    }

    /// <summary>Reads a NUL-terminated UTF-8 string owned by relate (do not free).</summary>
    internal static string? PtrToString(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>Reads a UTF-8 string of a known byte length owned by relate (do not free).</summary>
    internal static string PtrToString(nint p, nuint len) =>
        p == 0 || len == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)p, checked((int)len));

    /// <summary>Reads and frees a caller-owned string (<c>relate_free_string</c>).</summary>
    internal static string? TakeString(nint p)
    {
        if (p == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            NativeMethods.relate_free_string(p);
        }
    }

    /// <summary>
    /// Throws a <see cref="RelateException"/> when <paramref name="status"/> is an error; always
    /// frees the caller-owned <paramref name="errorJson"/>.
    /// </summary>
    internal static void Check(RelateStatus status, nint errorJson, string operation)
    {
        string? json = TakeString(errorJson);
        if (status != RelateStatus.Ok)
        {
            throw RelateException.FromErrorJson(json, operation);
        }
    }
}
