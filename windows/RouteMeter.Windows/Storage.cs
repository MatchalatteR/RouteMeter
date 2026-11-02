using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RouteMeter.Core;

namespace RouteMeter.Windows;

public sealed class Settings
{
    public bool Automatic { get; set; } = true;
    public string Endpoint { get; set; } = "http://127.0.0.1:9090";
    public string ProtectedSecret { get; set; } = "";
    public bool TaskbarVisible { get; set; } = true;
}
public static class Storage
{
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteMeter");
    public static string? Notice { get; private set; }
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(Folder, name);
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("State too large");
            var result = JsonSerializer.Deserialize<T>(File.ReadAllText(path), json) ?? throw new JsonException();
            if (result is MeterState state && (state.Version != 1 || state.Previous is null || state.Previous.Count > 20000)) throw new JsonException();
            if (result is Settings settings && (settings.Endpoint is null || settings.ProtectedSecret is null)) throw new JsonException();
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            var backup = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            try { File.Move(path, backup); } catch (IOException) { }
            Notice = "旧数据无法读取，已保留备份并建立新基准。";
            return new();
        }
    }
    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, name), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, json), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    public static bool Startup
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("RouteMeter") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("RouteMeter", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("RouteMeter", false);
        }
    }
    public static bool RelayStartup
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("RouteMeterRelay") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("RouteMeterRelay", "\"" + Environment.ProcessPath + "\" --relay");
            else key.DeleteValue("RouteMeterRelay", false);
        }
    }
}

// Windows DPAPI, scoped to the current user. No third-party package or plaintext secret file.
public static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "RouteMeter", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    public static string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
}
