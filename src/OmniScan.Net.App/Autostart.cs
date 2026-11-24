using System;
using Microsoft.Win32;

namespace OmniScan.Net.App;

/// <summary>Start with Windows (current user only) in server mode: HKCU\...\Run\OmniScan = "exe" --server.</summary>
public static class Autostart
{
    public const string ServerArgument = "--server";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OmniScan";

    public static void Set(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\" {ServerArgument}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
