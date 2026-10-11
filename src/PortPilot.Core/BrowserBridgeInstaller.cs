using System.Text.Json;
using Microsoft.Win32;

namespace PortPilot.Core;

public sealed class BrowserBridgeInstaller(string directory)
{
    private static readonly string[] Browsers = [@"Software\Google\Chrome", @"Software\Microsoft\Edge", @"Software\BraveSoftware\Brave-Browser"];
    public string ManifestPath => Path.Combine(directory, "browser-bridge", "native-host.json");
    public string CreateManifest(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kopplingen behöver en befintlig DevGeek.exe med absolut sökväg.");
        return JsonSerializer.Serialize(new { name = BalanceNativeHost.HostName, description = "DevGeek local OpenAI balance bridge", path = executable, type = "stdio", allowed_origins = new[] { BalanceNativeHost.ExtensionOrigin } }, new JsonSerializerOptions { WriteIndented = true });
    }
    public void Register(string executable)
    {
        var manifest = CreateManifest(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(ManifestPath, manifest);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            foreach (var browser in Browsers)
            {
                using var key = user.CreateSubKey(browser + @"\NativeMessagingHosts\" + BalanceNativeHost.HostName);
                key.SetValue("", ManifestPath, RegistryValueKind.String);
            }
        }
    }
    public void Unregister()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            foreach (var browser in Browsers)
            {
                using var parent = user.OpenSubKey(browser + @"\NativeMessagingHosts", writable: true);
                if (parent is null) continue;
                bool ours;
                using (var key = parent.OpenSubKey(BalanceNativeHost.HostName)) ours = string.Equals(key?.GetValue("") as string, ManifestPath, StringComparison.OrdinalIgnoreCase);
                if (ours) parent.DeleteSubKey(BalanceNativeHost.HostName, throwOnMissingSubKey: false);
            }
        }
    }
}
