using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniScan.Net.Core;

/// <summary>
/// Keeps a secret (an access key) in the settings file encrypted for the current Windows user (DPAPI):
/// the file can lie next to a portable program without giving the key away. Copied to another
/// computer or user, the value simply cannot be read and the key has to be entered again.
/// </summary>
public static class SecretText
{
    private const string Prefix = "dpapi:";

    /// <summary>The text encrypted for this user, as a single line.</summary>
    public static string Protect(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        return Prefix + Convert.ToBase64String(Crypt(data, protect: true));
    }

    /// <summary>The original text, or null when the value is not ours or belongs to another user or computer.</summary>
    public static string? Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            byte[] data = Convert.FromBase64String(value[Prefix.Length..]);
            return Encoding.UTF8.GetString(Crypt(data, protect: false));
        }
        catch (Exception e) when (e is FormatException or Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Crypt(byte[] data, bool protect)
    {
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Blob input = new() { Size = data.Length, Data = pin.AddrOfPinnedObject() };
            const int UiForbidden = 0x1;
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out Blob output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
