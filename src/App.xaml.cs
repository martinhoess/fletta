using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Fletta;

public partial class App : Application
{
    static readonly DateTime processStart = Process.GetCurrentProcess().StartTime;
    static readonly List<(string Label, double Ms)> marks = [];

    /// <summary>--measure: misst nur, merkt sich nichts.</summary>
    public static bool IsMeasuring { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Mark("startup");

        var flags = e.Args.Where(a => a.StartsWith("--")).ToHashSet();
        // Ohne Konsole landet stderr nur bei umgeleitetem Handle irgendwo; so auch beim Aufruf aus cmd.
        if (flags.Count > 0) AttachConsole(AttachParentProcess);

        if (flags.Contains("--selftest"))
        {
            Shutdown(SelfTest.Run());
            return;
        }
        if (flags.Contains("--register") || flags.Contains("--unregister"))
        {
            Shutdown(ChangeRegistration(register: flags.Contains("--register")));
            return;
        }

        var path = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        IsMeasuring = flags.Contains("--measure");
        new MainWindow(path, measure: IsMeasuring).Show();
    }

    /// <summary>Abmelden oder Herunterfahren: dann kommt kein Closing, also hier speichern.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        foreach (var window in Windows.OfType<MainWindow>()) window.SaveSettings();
        base.OnSessionEnding(e);
    }

    /// <summary>--register / --unregister: Anmeldung als PDF-Programm beim Benutzer. Exit 0 = geklappt.</summary>
    static int ChangeRegistration(bool register)
    {
        try
        {
            if (register) FileAssociation.Register(Environment.ProcessPath!);
            else FileAssociation.Unregister();
            Console.Error.WriteLine(register ? $"angemeldet: {Environment.ProcessPath}" : "abgemeldet");
            return 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Console.Error.WriteLine($"Registry: {e.Message}");
            return 1;
        }
    }

    /// <summary>Zeitmarke seit Prozessstart. Jede Marke zählt nur beim ersten Mal; aus jedem Thread.</summary>
    public static void Mark(string label)
    {
        lock (marks)
        {
            if (!marks.Exists(m => m.Label == label))
                marks.Add((label, (DateTime.Now - processStart).TotalMilliseconds));
        }
    }

    public static string Report()
    {
        lock (marks) return string.Join(" · ", marks.Select(m => $"{m.Label} {m.Ms:F0} ms"));
    }

    /// <summary>
    /// Meldet das Fenster bei Windows für den Neustart nach einem Update an, samt der offenen Datei — das
    /// Setup (/RESTARTAPPLICATIONS) öffnet es danach wieder. Nicht nach Absturz, Hänger oder Neustart des
    /// Rechners. Ein leeres Fenster braucht ein Argument: eine leere Befehlszeile hebt die Anmeldung auf,
    /// „--restarted“ übergeht OnStartup wie jedes unbekannte Schalter-Argument.
    /// </summary>
    public static void RegisterRestart(string? path) =>
        RegisterApplicationRestart(path is null ? "--restarted" : $"\"{path}\"", RestartNoCrash | RestartNoHang | RestartNoReboot);

    const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, int flags);

    const uint AttachParentProcess = unchecked((uint)-1);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
