using System.IO;
using System.Windows;
using System.Windows.Threading;
using DrawingIcon = System.Drawing.Icon;
using WinFormsApp = System.Windows.Forms.Application;

namespace CallNotesTray;

/// <summary>
/// App-Einstieg der CallNotes-Tray-App. Haelt NICHT den Watch-Daemon selbst
/// (der laeuft laut Contract als eigener Task/Thread, gestartet von hier aus
/// via CallTap.Core — Anbindung an den Kern folgt sobald CallTap.Core existiert;
/// diese Datei beschraenkt sich auf die ihr zugewiesene Tray-/Panel-Schicht),
/// sondern nur das Tray-Icon + das Panel-Fenster (MainPanel).
///
/// Icon-Verhalten (Windows-Aequivalent des macOS-Menueleisten-Icons):
///   - Klick links  -> Panel oeffnen/nach vorne holen (Singleton, kein Doppel-Fenster)
///   - Klick rechts -> Kontextmenue (Panel oeffnen / Beenden)
///   - Icon-Zustand wird alle 2s aus state/current-call.json + state/processing.json
///     abgeleitet (idle / recording / processing), gleiche Kadenz wie der Poll-Loop
///     des Watchers laut Contract Abschnitt 6.3.
/// </summary>
public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private MainPanel? _panel;
    private DispatcherTimer? _iconStateTimer;

    private DrawingIcon? _iconIdle;
    private DrawingIcon? _iconRecording;
    private DrawingIcon? _iconProcessing;

    private TrayIconState _lastState = TrayIconState.Unknown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sprache einmalig aus config.json auflösen (System/DE/EN), bevor
        // irgendein UI-Text erzeugt wird.
        L10n.Refresh();

        LoadIcons();
        CreateNotifyIcon();

        _iconStateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _iconStateTimer.Tick += (_, _) => RefreshTrayState();
        _iconStateTimer.Start();

        // Sofort einmal auswerten statt 2s auf den ersten Tick zu warten.
        RefreshTrayState();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _iconStateTimer?.Stop();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _iconIdle?.Dispose();
        _iconRecording?.Dispose();
        _iconProcessing?.Dispose();
        base.OnExit(e);
    }

    private void LoadIcons()
    {
        // Alle drei Icon-Dateien liegen unter Assets/ (siehe Contract 2, CallTap.Tray/Assets/
        // als Vorbild) und werden als Content mit "Copy to Output Directory" ausgeliefert.
        _iconIdle = LoadIconOrFallback("Assets/tray.ico");
        _iconRecording = LoadIconOrFallback("Assets/tray-recording.ico") ?? _iconIdle;
        _iconProcessing = LoadIconOrFallback("Assets/tray-processing.ico") ?? _iconIdle;
    }

    private static DrawingIcon? LoadIconOrFallback(string relativePath)
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string full = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                return new DrawingIcon(full);
            }
        }
        catch
        {
            // Fehlendes/kaputtes Icon darf die App nie zum Absturz bringen —
            // gleiche "warn, don't crash"-Haltung wie im restlichen Projekt.
        }
        return null;
    }

    private void CreateNotifyIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _iconIdle,
            Visible = true,
            Text = "CallNotes",
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ShowPanel();
            }
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = menu.Items.Add(L10n.L("Öffnen", "Open"));
        openItem.Click += (_, _) => ShowPanel();

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var quitItem = menu.Items.Add(L10n.L("Beenden", "Quit"));
        quitItem.Click += (_, _) => Shutdown();

        _notifyIcon.ContextMenuStrip = menu;
    }

    /// <summary>Oeffnet das Panel-Fenster oder holt es nach vorne, falls schon offen (Singleton).</summary>
    public void ShowPanel()
    {
        if (_panel == null || !_panel.IsLoaded)
        {
            _panel = new MainPanel();
            _panel.Closed += (_, _) => _panel = null;
        }

        if (_panel.WindowState == WindowState.Minimized)
        {
            _panel.WindowState = WindowState.Normal;
        }

        _panel.Show();
        _panel.Activate();
        _panel.Topmost = true;
        _panel.Topmost = false;
        _panel.Focus();
    }

    /// <summary>
    /// Liest state/current-call.json und state/processing.json und waehlt das
    /// passende Icon. Reine Anzeige-Logik — der eigentliche Poll-Loop, der diese
    /// Dateien schreibt, laeuft im Watcher (CallTap.Core), nicht hier.
    /// </summary>
    private void RefreshTrayState()
    {
        if (_notifyIcon == null) return;

        TrayIconState state = TrayIconState.Idle;
        string tooltip = "CallNotes";

        try
        {
            string currentCallPath = Path.Combine(Paths.StateDir, "current-call.json");
            string processingPath = Path.Combine(Paths.StateDir, "processing.json");

            if (File.Exists(processingPath))
            {
                state = TrayIconState.Processing;
                tooltip = L10n.L("CallNotes – verarbeitet Anruf…", "CallNotes – processing call…");
            }
            else if (File.Exists(currentCallPath))
            {
                state = TrayIconState.Recording;
                tooltip = L10n.L("CallNotes – Aufnahme läuft…", "CallNotes – recording…");
            }
        }
        catch
        {
            // Zustandsdateien nicht lesbar -> als Idle behandeln statt abzustuerzen.
        }

        if (state != _lastState)
        {
            _notifyIcon.Icon = state switch
            {
                TrayIconState.Recording => _iconRecording,
                TrayIconState.Processing => _iconProcessing,
                _ => _iconIdle,
            };
            _lastState = state;
        }

        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; // NotifyIcon.Text-Limit (Win32)
    }

    private enum TrayIconState
    {
        Unknown,
        Idle,
        Recording,
        Processing,
    }
}

/// <summary>
/// Minimaler Pfad-Helfer, lokal zu CallNotesTray (dupliziert bewusst nur die paar
/// Konstanten aus Contract Abschnitt 3, statt eine Abhaengigkeit auf CallTap.Core
/// vorauszusetzen, solange dessen Paths.cs noch nicht existiert). Sobald
/// CallTap.Core.Config.Paths verfuegbar ist, sollte dieser Typ durch eine
/// direkte Projektreferenz ersetzt werden.
/// </summary>
internal static class Paths
{
    /// <summary>%APPDATA%\callnotes\config.json, ueberschreibbar per CALLNOTES_CONFIG.</summary>
    public static string ConfigFile
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("CALLNOTES_CONFIG");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "callnotes", "config.json");
        }
    }

    /// <summary>%USERPROFILE%\CallNotes — Datenwurzel (outDir-Default).</summary>
    public static string DataRoot
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, "CallNotes");
        }
    }

    public static string StateDir => Path.Combine(DataRoot, "state");
    public static string MicActiveFile => Path.Combine(StateDir, "mic-active.json");
    public static string PendingDir => Path.Combine(StateDir, "pending");
    public static string NotesDir => Path.Combine(DataRoot, "notes");
    public static string ReviewDir => Path.Combine(DataRoot, "review");
    public static string FailedDir => Path.Combine(DataRoot, "failed");
    public static string LogDir => Path.Combine(DataRoot, "log");
}
