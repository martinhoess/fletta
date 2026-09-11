using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fletta;

/// <summary>
/// Meldet Fletta beim angemeldeten Benutzer (HKCU, ohne Adminrechte) als Programm für .pdf an:
/// ProgID, „Öffnen mit“ und ein Eintrag unter „Standard-Apps“. Den Standard selbst setzt Windows
/// seit 10 nur der Benutzer in den Einstellungen — das darf kein Programm.
/// </summary>
static partial class FileAssociation
{
    public const string ProgId = "Fletta.Pdf";
    const string CapabilitiesKey = @"Software\Fletta\Capabilities";
    const int AssociationChanged = 0x08000000; // SHCNE_ASSOCCHANGED

    /// <summary>Alle Einträge als (Schlüssel unter HKCU, Wertname — leer ist der Standardwert, Wert).</summary>
    public static IReadOnlyList<(string Key, string Name, string Value)> Entries(string exe)
    {
        var open = $"\"{exe}\" \"%1\"";
        return
        [
            ($@"Software\Classes\{ProgId}", "", "PDF-Dokument"),
            ($@"Software\Classes\{ProgId}\DefaultIcon", "", $"\"{exe}\",0"),
            ($@"Software\Classes\{ProgId}\shell\open\command", "", open),
            (@"Software\Classes\.pdf\OpenWithProgids", ProgId, ""),
            (@"Software\Classes\Applications\Fletta.exe\shell\open\command", "", open),
            (@"Software\Classes\Applications\Fletta.exe\SupportedTypes", ".pdf", ""),
            (CapabilitiesKey, "ApplicationName", "Fletta"),
            (CapabilitiesKey, "ApplicationDescription", "Schneller PDF-Betrachter"),
            ($@"{CapabilitiesKey}\FileAssociations", ".pdf", ProgId),
            (@"Software\RegisteredApplications", "Fletta", CapabilitiesKey),
        ];
    }

    public static void Register(string exe)
    {
        foreach (var (key, name, value) in Entries(exe))
        {
            using var subKey = Registry.CurrentUser.CreateSubKey(key);
            subKey.SetValue(name, value);
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\Fletta.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Fletta", throwOnMissingSubKey: false);
        using (var progIds = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
            progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue("Fletta", throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>Explorer liest die Zuordnungen neu, sonst zeigt er bis zur nächsten Anmeldung die alten.</summary>
    static void NotifyShell() => SHChangeNotify(AssociationChanged, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
