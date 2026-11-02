using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CapyServer.Core;

namespace CapyServer.Desktop;

public static class SecretStore
{
    public static string PathFor(string state, ServerProfile profile)
    { _ = ProfileStore.Root(state, profile); return Path.Combine(state, "credentials", "duckdns-" + profile.Id + ".bin"); }
    public static void Save(string state, ServerProfile profile, string token)
    {
        token = token.Trim(); if (token.Length is < 1 or > 256 || token.Any(char.IsWhiteSpace)) throw new InvalidDataException("Paste the DuckDNS account token without spaces.");
        var data = Encoding.UTF8.GetBytes(token);
        try { var protectedData = Transform(data, profile.Id, true); var path = PathFor(state, profile); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path + ".new", protectedData); File.Move(path + ".new", path, true); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    public static string? Read(string state, ServerProfile profile)
    {
        var path = PathFor(state, profile); if (!File.Exists(path)) return null;
        var file = new FileInfo(path); if (file.Length > 8192 || file.LinkTarget != null) throw new InvalidDataException("Invalid credential storage file.");
        try { var data = Transform(File.ReadAllBytes(path), profile.Id, false); try { return Encoding.UTF8.GetString(data); } finally { CryptographicOperations.ZeroMemory(data); } }
        catch (Win32Exception) { throw new IOException("The saved DuckDNS token belongs to another Windows account or is unavailable. Save it again in automatic setup."); }
    }
    private static byte[] Transform(byte[] bytes, string profileId, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Credential storage requires Windows.");
        var entropy = Encoding.UTF8.GetBytes("CapyBaraServerManager/DuckDNS/" + profileId);
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; var salt = new Blob { Size = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) }; var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(entropy, 0, salt.Data, entropy.Length);
            var ok = protect ? CryptProtectData(ref input, null, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(salt.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
