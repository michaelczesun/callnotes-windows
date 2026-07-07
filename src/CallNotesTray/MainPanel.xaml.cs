using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using static CallNotesTray.L10n;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;
using FontFamily = System.Windows.Media.FontFamily;

namespace CallNotesTray;

/// <summary>
/// Das Panel-Fenster (Klick aufs Tray-Icon). An die macOS-Menueleisten-App
/// (SettingsApp.swift) angelehnt: Header mit Status, laufender Anruf mit Wellenform-
/// Pegeln + Teilnehmer-Zeilen, Sprecher-Zuordnungen, letzte Notizen, fehlgeschlagene
/// Aufnahmen und eine vollstaendige Einstellungs-Sektion (alle config.json-Optionen).
///
/// Alle Datei-I/O ist bewusst defensiv (try/catch) — ein fehlendes/kaputtes State-File
/// darf das Panel nie zum Absturz bringen.
/// </summary>
public partial class MainPanel : Window
{
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _waveTimer;
    private string? _activeCallDir;
    private DateTime? _activeCallStartUtc;
    private string? _micAddName; // Prozessname der aktuell mikro-aktiven, nicht-gelisteten App
    private bool _addingMicActiveApp; // waehrend "Diese App immer aufnehmen" speichert
    private string? _participantsBuiltForDir;
    private bool _settingsExpanded;
    private bool _loading;

    // Einstellungs-Zustand, der nicht direkt in einem Control lebt
    private string _notesDir = "";
    private string _audioDir = "";
    private string _mirrorDir = "";
    private bool _mirrorEnabled;

    // Wellenform-Pegel (scrollende Historie)
    private const int WaveBars = 40;
    private Rectangle[] _micBars = System.Array.Empty<Rectangle>();
    private Rectangle[] _sysBars = System.Array.Empty<Rectangle>();
    private readonly double[] _micHist = new double[WaveBars];
    private readonly double[] _sysHist = new double[WaveBars];
    private double _lastMic, _lastSys;
    private readonly System.Random _rand = new();

    public MainPanel()
    {
        InitializeComponent();
        BuildWaveBars();
        _loading = true;
        LoadSettingsIntoUi();
        _loading = false;
        ApplyLocalization();
        PositionNearTray();
        RefreshAll();

        // Wie ein macOS-Menueleisten-Popover: bei Fokusverlust schliessen, Esc schliesst.
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => RefreshAll();
        _pollTimer.Start();

        _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _waveTimer.Tick += (_, _) => WaveTick();
        _waveTimer.Start();

        Closed += (_, _) => { _pollTimer.Stop(); _waveTimer.Stop(); };
    }

    private void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 6;
        Top = workArea.Bottom - 60;
        SizeChanged += (_, _) =>
        {
            Top = Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - 6);
            Left = workArea.Right - Width - 6;
        };
    }

    // ============================================================
    // Lokalisierung
    // ============================================================

    private void ApplyLocalization()
    {
        Title = "CallNotes";
        MicLabel.Text = L("Du", "You");
        SysLabel.Text = L("Gegenseite", "Other side");
        AddParticipantText.Text = L("weiterer Teilnehmer", "another participant");
        SaveParticipantsButton.Content = L("Speichern", "Save");
        DiscardCallText.Text = L("Diesen Anruf nicht aufnehmen", "Don't record this call");
        RecentNotesTitle.Text = L("LETZTE ANRUFE", "RECENT CALLS");
        RecentNotesEmpty.Text = L("Noch keine Notizen.", "No notes yet.");
        FailedTitle.Text = L("FEHLGESCHLAGEN", "FAILED");
        SettingsTitle.Text = L("Einstellungen", "Settings");

        LangHeader.Text = L("SPRACHE", "LANGUAGE");
        StorageHeader.Text = L("SPEICHERORTE", "STORAGE");
        NotesDirLabel.Text = L("Notizen", "Notes");
        AudioDirLabel.Text = L("Audio-Archiv", "Audio archive");
        MirrorDirLabel.Text = L("Kopie (extern)", "Copy (external)");
        TranscriptionHeader.Text = L("TRANSKRIPTION", "TRANSCRIPTION");
        GroqKeyLabel.Text = L("Groq API-Key", "Groq API key");
        SummaryHeader.Text = L("KI-ZUSAMMENFASSUNG", "AI SUMMARY");
        NoteContentHeader.Text = L("NOTIZ-INHALTE", "NOTE CONTENTS");
        ChkKurzfassung.Content = L("Kurzfassung", "Summary");
        ChkBesprochen.Content = L("Besprochen", "Discussed");
        ChkTodos.Content = L("To-dos", "To-dos");
        ChkFollowup.Content = L("Follow-up-Mail", "Follow-up email");
        DestHeader.Text = L("ABLAGE ZUSÄTZLICH IN", "ALSO STORE IN");
        PushHeader.Text = L("PUSH", "PUSH");
        SyncNowButton.Content = L("Jetzt syncen", "Sync now");
        SaveSettingsButton.Content = L("Speichern & Neustart", "Save & restart");
        QuitButton.Content = L("Beenden", "Quit");

        foreach (var seg in new[] { LangSystem, LangDe, LangEn })
            seg.Content = (string)seg.Tag switch { "de" => L("Deutsch", "German"), "en" => "English", _ => L("System", "System") };
        TransLocal.Content = L("Lokal (Whisper)", "Local (Whisper)");
        TransGroq.Content = L("Groq (Cloud)", "Groq (cloud)");
        SumClaude.Content = L("Claude Code", "Claude Code");
        SumOpenAI.Content = L("Eigene KI", "Own AI");
        SumOff.Content = L("Aus", "Off");
    }

    // ============================================================
    // Zentraler Poll-Tick
    // ============================================================

    private void RefreshAll()
    {
        RefreshHeaderStatus();
        RefreshMicMonitor();
        RefreshActiveCall();
        RefreshPending();
        RefreshRecentNotes();
        RefreshFailed();
    }

    // Live-Mikro-Monitor: zeigt, welche App gerade das Mikrofon nutzt (auch nicht
    // gelistete) — und bietet an, sie dauerhaft aufzunehmen (Browser-Calls!).
    private void RefreshMicMonitor()
    {
        var m = TryReadJsonObject(Paths.MicActiveFile);
        bool recording = File.Exists(Path.Combine(Paths.StateDir, "current-call.json"));
        if (m == null || recording)
        {
            MicMonitorCard.Visibility = Visibility.Collapsed;
            _micAddName = null;
            return;
        }
        string name = m["name"]?.GetValue<string>() ?? m["bundle"]?.GetValue<string>() ?? "?";
        bool listed = m["listed"]?.GetValue<bool>() ?? false;
        _micAddName = m["base"]?.GetValue<string>() ?? m["bundle"]?.GetValue<string>();

        MicMonitorCard.Visibility = Visibility.Visible;
        IdleHintCard.Visibility = Visibility.Collapsed; // nicht doppelt erklaeren
        MicMonitorText.Text = L($"„{name}“ nutzt gerade dein Mikrofon", $"“{name}” is using your microphone");
        if (listed)
        {
            MicMonitorSub.Text = L("Aufnahme startet …", "Recording starting …");
            MicMonitorButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MicMonitorSub.Text = L("Wird nicht aufgenommen — nicht in deiner Anruf-Liste.",
                                   "Not being recorded — not in your call list.");
            MicMonitorButton.Content = L("Diese App immer aufnehmen", "Always record this app");
            MicMonitorButton.Visibility = string.IsNullOrWhiteSpace(_micAddName)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void MicMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = _micAddName;
        if (string.IsNullOrWhiteSpace(name) || _addingMicActiveApp) return;
        _addingMicActiveApp = true;
        MicMonitorButton.IsEnabled = false;
        try
        {
            var c = TryReadJsonObject(Paths.ConfigFile) ?? new JsonObject();
            var apps = c["apps"] as JsonArray;
            if (apps == null) { apps = new JsonArray(); c["apps"] = apps; }
            bool exists = apps.Any(n => string.Equals(n?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (!exists) apps.Add(JsonValue.Create(name));
            WriteJsonAtomic(Paths.ConfigFile, c);
            // Config-Aenderung sofort wirksam machen — auch fuer den gerade laufenden
            // "Anruf" (Browser mit aktivem Mikrofon), analog zu alwaysRecord() im
            // Mac-Original (dort per launchctl kickstart -k).
            TryRestartDaemon();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Konnte App nicht hinzufügen: {ex.Message}", $"Could not add app: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _addingMicActiveApp = false;
            MicMonitorButton.IsEnabled = true;
        }
        RefreshAll();
    }

    private void RefreshHeaderStatus()
    {
        bool running = IsWatchDaemonRunning();
        bool processing = File.Exists(Path.Combine(Paths.StateDir, "processing.json"));
        bool recording = File.Exists(Path.Combine(Paths.StateDir, "current-call.json"));

        SolidColorBrush dot;
        string subtitle;
        if (processing) { dot = Brush("BrushPurple"); subtitle = L("verarbeitet Anruf…", "processing call…"); }
        else if (recording) { dot = Brush("BrushAccent"); subtitle = L("Aufnahme läuft", "recording"); }
        else if (running) { dot = Brush("BrushSuccess"); subtitle = L("Anruf-Autopilot bereit", "Call autopilot ready"); }
        else { dot = Brush("BrushTextTertiary"); subtitle = L("Daemon gestoppt", "Daemon stopped"); }

        DaemonStatusDot.Fill = dot;
        HeaderSubtitle.Text = subtitle;

        // Leerlauf: erklaeren, wann Aufnahme/Popup kommen (erster Mac-Tester
        // erwartete das Popup schon beim Oeffnen von Teams)
        IdleHintCard.Visibility = (!recording && !processing) ? Visibility.Visible : Visibility.Collapsed;
        IdleHintText.Text = running
            ? L("Bereit. Sobald in einer Call-App wirklich ein Anruf läuft (Mikrofon aktiv), startet die Aufnahme von selbst und ein Popup erscheint. Die App nur zu öffnen reicht nicht.",
                "Ready. As soon as a call is actually running in a call app (microphone active), recording starts by itself and a popup appears. Just opening the app is not enough.")
            : L("Der Aufnahme-Dienst läuft nicht. installer\\install.ps1 erneut ausführen oder den PC neu starten.",
                "The recording service is not running. Re-run installer\\install.ps1 or restart your PC.");
        HeaderSubtitle.Foreground = recording ? Brush("BrushAccent") : (processing ? Brush("BrushPurple") : Brush("BrushPurple"));
    }

    private static bool IsWatchDaemonRunning()
    {
        try { return Process.GetProcessesByName("calltap").Length > 0; }
        catch { return false; }
    }

    // ============================================================
    // Aktiver Anruf
    // ============================================================

    private void RefreshActiveCall()
    {
        string currentCallFile = Path.Combine(Paths.StateDir, "current-call.json");
        JsonObject? call = TryReadJsonObject(currentCallFile);

        if (call == null)
        {
            _activeCallDir = null;
            _activeCallStartUtc = null;
            _participantsBuiltForDir = null;
            _lastMic = _lastSys = 0;
            ActiveCallCard.Visibility = Visibility.Collapsed;
            return;
        }

        ActiveCallCard.Visibility = Visibility.Visible;
        _activeCallDir = call["dir"]?.GetValue<string>();
        ActiveCallApp.Text = call["appName"]?.GetValue<string>() ?? call["app"]?.GetValue<string>() ?? "?";

        if (DateTime.TryParse(call["start"]?.GetValue<string>(), out var start))
            _activeCallStartUtc = start.ToUniversalTime();

        if (_activeCallStartUtc.HasValue)
        {
            var elapsed = DateTime.UtcNow - _activeCallStartUtc.Value;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            ActiveCallDuration.Text = elapsed.Hours > 0
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        // Teilnehmer-Zeilen nur bei neuem Anruf (neu-)aufbauen, um Tipperei nicht zu ueberschreiben.
        if (_activeCallDir != _participantsBuiltForDir)
        {
            _participantsBuiltForDir = _activeCallDir;
            RebuildParticipantRows();
        }

        // aktuelle Pegel fuer die Wellenform
        if (!string.IsNullOrEmpty(_activeCallDir))
        {
            var levels = TryReadJsonObject(Path.Combine(_activeCallDir, "levels.json"));
            _lastMic = ClampLevel(levels?["mic"]?.GetValue<double>() ?? 0);
            _lastSys = ClampLevel(levels?["sys"]?.GetValue<double>() ?? 0);
        }
    }

    private static double ClampLevel(double v) => Math.Max(0, Math.Min(1, v));

    // ---- Wellenform ----

    private void BuildWaveBars()
    {
        _micBars = new Rectangle[WaveBars];
        _sysBars = new Rectangle[WaveBars];
        for (int i = 0; i < WaveBars; i++)
        {
            _micBars[i] = MakeBar(Brush("BrushLevelMic"));
            _sysBars[i] = MakeBar(Brush("BrushLevelSys"));
            MicWavePanel.Children.Add(_micBars[i]);
            SysWavePanel.Children.Add(_sysBars[i]);
        }
    }

    private static Rectangle MakeBar(Brush fill) => new()
    {
        Width = 3.5,
        Height = 2,
        RadiusX = 1.75,
        RadiusY = 1.75,
        Fill = fill,
        Margin = new Thickness(1.25, 0, 1.25, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void WaveTick()
    {
        if (ActiveCallCard.Visibility != Visibility.Visible) { _lastMic = _lastSys = 0; }

        System.Array.Copy(_micHist, 1, _micHist, 0, WaveBars - 1);
        System.Array.Copy(_sysHist, 1, _sysHist, 0, WaveBars - 1);
        _micHist[WaveBars - 1] = _lastMic > 0.02 ? Math.Min(1, _lastMic * (0.7 + _rand.NextDouble() * 0.6)) : 0;
        _sysHist[WaveBars - 1] = _lastSys > 0.02 ? Math.Min(1, _lastSys * (0.7 + _rand.NextDouble() * 0.6)) : 0;

        const double maxH = 26;
        for (int i = 0; i < WaveBars; i++)
        {
            _micBars[i].Height = Math.Max(2, _micHist[i] * maxH);
            _sysBars[i].Height = Math.Max(2, _sysHist[i] * maxH);
        }
    }

    // ---- Teilnehmer ----

    private void RebuildParticipantRows()
    {
        ParticipantsPanel.Children.Clear();
        var names = new List<string>();
        if (!string.IsNullOrEmpty(_activeCallDir))
        {
            var arr = TryReadJsonArray(Path.Combine(_activeCallDir, "participants.json"));
            if (arr != null)
                names = arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        if (names.Count == 0) names.Add("");
        foreach (var n in names) AddParticipantRow(n);
    }

    private void AddParticipantRow(string name)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush("BrushTextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(glyph, 0);

        var box = new TextBox { Text = name, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(box, 1);

        var remove = new Button
        {
            Style = (Style)FindResource("RoundIconButton"),
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            ParticipantsPanel.Children.Remove(row);
            if (ParticipantsPanel.Children.Count == 0) AddParticipantRow("");
        };
        Grid.SetColumn(remove, 2);

        row.Children.Add(glyph);
        row.Children.Add(box);
        row.Children.Add(remove);
        ParticipantsPanel.Children.Add(row);
    }

    private void AddParticipantButton_Click(object sender, RoutedEventArgs e) => AddParticipantRow("");

    private void SaveParticipantsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeCallDir)) return;

        var array = new JsonArray();
        foreach (var child in ParticipantsPanel.Children)
        {
            if (child is Grid g)
            {
                var box = g.Children.OfType<TextBox>().FirstOrDefault();
                var name = (box?.Text ?? "").Trim();
                if (name.Length > 0) array.Add(JsonValue.Create(name));
            }
        }
        WriteJsonAtomic(Path.Combine(_activeCallDir, "participants.json"), array);
        FlashHint(L("Teilnehmer gespeichert.", "Participants saved."));
    }

    private void DiscardCallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeCallDir)) return;
        var result = System.Windows.MessageBox.Show(
            L("Diesen Anruf wirklich verwerfen? Es wird nichts gespeichert.",
              "Really discard this call? Nothing will be saved."),
            L("Anruf verwerfen", "Discard call"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            File.WriteAllText(Path.Combine(_activeCallDir, "abort"), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            System.Windows.MessageBox.Show(L("Konnte Abbruch nicht auslösen.", "Could not trigger abort."),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ============================================================
    // Sprecher-Zuordnungen (state/pending/*.json)
    // ============================================================

    private void RefreshPending()
    {
        PendingList.Children.Clear();
        List<string> files;
        try
        {
            if (!Directory.Exists(Paths.PendingDir)) { PendingSection.Visibility = Visibility.Collapsed; return; }
            files = Directory.GetFiles(Paths.PendingDir, "*.json").OrderByDescending(f => f).ToList();
        }
        catch { PendingSection.Visibility = Visibility.Collapsed; return; }

        if (files.Count == 0) { PendingSection.Visibility = Visibility.Collapsed; return; }
        PendingSection.Visibility = Visibility.Visible;

        foreach (var file in files)
        {
            var obj = TryReadJsonObject(file);
            if (obj == null) continue;
            var card = BuildPendingCard(file, obj);
            if (card != null) PendingList.Children.Add(card);
        }
    }

    private FrameworkElement? BuildPendingCard(string file, JsonObject obj)
    {
        string stamp = obj["stamp"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
        string app = obj["app"]?.GetValue<string>() ?? "";
        var speakersNode = obj["speakers"] as JsonArray;
        var participantOptions = (obj["participants"] as JsonArray)?
            .Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();
        if (speakersNode == null || speakersNode.Count == 0) return null;

        var outer = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Brush("BrushTextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock
        {
            Text = $"{speakersNode.Count} {L("Stimmen", "voices")} · {(string.IsNullOrEmpty(app) ? "" : app + " · ")}{stamp}",
            Foreground = Brush("BrushTextSecondary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        });
        outer.Children.Add(head);

        var speakerCombos = new List<(string label, ComboBox combo)>();
        foreach (var speakerNode in speakersNode)
        {
            if (speakerNode is not JsonObject speaker) continue;
            string label = speaker["label"]?.GetValue<string>() ?? "?";
            string clip = speaker["clip"]?.GetValue<string>() ?? "";
            string suggestion = speaker["suggestion"]?.GetValue<string>() ?? "";

            var r = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var play = new Button { Style = (Style)FindResource("CirclePlayButton"), Tag = clip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            play.Click += (_, _) => PlayClip(clip);
            Grid.SetColumn(play, 0);

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("BrushTextPrimary"), FontSize = 12.5 };
            Grid.SetColumn(lbl, 1);

            var combo = new ComboBox { IsEditable = true, Text = suggestion, VerticalAlignment = VerticalAlignment.Center };
            foreach (var name in participantOptions) combo.Items.Add(name);
            Grid.SetColumn(combo, 2);

            r.Children.Add(play); r.Children.Add(lbl); r.Children.Add(combo);
            outer.Children.Add(r);
            speakerCombos.Add((label, combo));
        }

        var actions = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var viewNote = new Button { Content = L("Notiz ansehen", "View note"), Style = (Style)FindResource("LinkButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        string notePath = obj["note"]?.GetValue<string>() ?? "";
        viewNote.Click += (_, _) => OpenNote(notePath);
        DockPanel.SetDock(viewNote, Dock.Left);
        var apply = new Button { Content = L("Zuordnung übernehmen", "Apply assignment"), Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        apply.Click += (_, _) => ApplyPendingSpeakers(file, stamp, speakerCombos);
        actions.Children.Add(viewNote); actions.Children.Add(apply);
        outer.Children.Add(actions);

        return new Border { Child = outer };
    }

    private void PlayClip(string clipPath)
    {
        if (string.IsNullOrEmpty(clipPath) || !File.Exists(clipPath)) return;
        try { Process.Start(new ProcessStartInfo(clipPath) { UseShellExecute = true }); } catch { }
    }

    private void ApplyPendingSpeakers(string pendingFile, string stamp, List<(string label, ComboBox combo)> combos)
    {
        var assignment = new JsonObject();
        foreach (var (label, combo) in combos)
            assignment[label] = JsonValue.Create((combo.Text ?? "").Trim());

        string tempAssignmentFile = Path.Combine(Path.GetTempPath(), $"callnotes-speakers-{stamp}.json");
        try
        {
            File.WriteAllText(tempAssignmentFile, assignment.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var psi = new ProcessStartInfo
            {
                FileName = ResolveVenvPython(),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add(ResolvePipelineScript("apply_speakers.py"));
            psi.ArgumentList.Add(pendingFile);
            psi.ArgumentList.Add(tempAssignmentFile);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            if (proc != null && proc.ExitCode == 0) TryDeleteFile(pendingFile);
            else
            {
                string stderr = proc?.StandardError.ReadToEnd() ?? "";
                System.Windows.MessageBox.Show(L($"Sprecher-Zuordnung fehlgeschlagen:\n{stderr}", $"Speaker assignment failed:\n{stderr}"),
                    "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Konnte apply_speakers.py nicht starten: {ex.Message}", $"Could not start apply_speakers.py: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { TryDeleteFile(tempAssignmentFile); RefreshPending(); }
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // ============================================================
    // Letzte Notizen
    // ============================================================

    private void RefreshRecentNotes()
    {
        RecentNotesList.Children.Clear();
        List<string> notes;
        try
        {
            if (!Directory.Exists(Paths.NotesDir)) { RecentNotesEmpty.Visibility = Visibility.Visible; return; }
            notes = Directory.GetFiles(Paths.NotesDir, "*.md").OrderByDescending(File.GetLastWriteTimeUtc).Take(5).ToList();
        }
        catch { RecentNotesEmpty.Visibility = Visibility.Visible; return; }

        if (notes.Count == 0) { RecentNotesEmpty.Visibility = Visibility.Visible; return; }
        RecentNotesEmpty.Visibility = Visibility.Collapsed;

        foreach (var note in notes)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Cursor = System.Windows.Input.Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Brush("BrushAccent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(icon, 0);
            var name = new TextBlock { Text = Path.GetFileNameWithoutExtension(note), Foreground = Brush("BrushTextPrimary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var arrow = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Brush("BrushTextTertiary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(arrow, 2);

            row.Children.Add(icon); row.Children.Add(name); row.Children.Add(arrow);
            row.MouseLeftButtonUp += (_, _) => OpenNote(note);
            RecentNotesList.Children.Add(row);
        }
    }

    private static void OpenNote(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ============================================================
    // Fehlgeschlagene Aufnahmen
    // ============================================================

    private void RefreshFailed()
    {
        FailedList.Children.Clear();
        List<string> dirs;
        try
        {
            if (!Directory.Exists(Paths.FailedDir)) { FailedSection.Visibility = Visibility.Collapsed; return; }
            dirs = Directory.GetDirectories(Paths.FailedDir).OrderByDescending(d => d).ToList();
        }
        catch { FailedSection.Visibility = Visibility.Collapsed; return; }

        if (dirs.Count == 0) { FailedSection.Visibility = Visibility.Collapsed; return; }
        FailedSection.Visibility = Visibility.Visible;

        foreach (var dir in dirs)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Fehlergrund (z. B. „Whisper-Modell fehlt") sichtbar machen, falls vorhanden
            string reason = "";
            try { var rf = Path.Combine(dir, "fail-reason.txt"); if (File.Exists(rf)) reason = File.ReadAllText(rf).Trim(); } catch { }
            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            namePanel.Children.Add(new TextBlock { Text = Path.GetFileName(dir), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(reason))
                namePanel.Children.Add(new TextBlock { Text = reason, FontSize = 11, Foreground = Brush("BrushTextSecondary"), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(namePanel, 0);
            var retry = new Button { Content = L("Erneut", "Retry"), Style = (Style)FindResource("ChooseButton"), Margin = new Thickness(0, 0, 6, 0) };
            retry.Click += (_, _) => RetryFailed(dir);
            Grid.SetColumn(retry, 1);
            var discard = new Button { Content = L("Verwerfen", "Discard"), Style = (Style)FindResource("ChooseButton") };
            discard.Click += (_, _) => DiscardFailed(dir);
            Grid.SetColumn(discard, 2);

            row.Children.Add(namePanel); row.Children.Add(retry); row.Children.Add(discard);
            FailedList.Children.Add(row);
        }
    }

    private void RetryFailed(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ResolveVenvPython(), UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(ResolvePipelineScript("process_call.py"));
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Konnte Verarbeitung nicht neu starten: {ex.Message}", $"Could not restart processing: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { RefreshFailed(); }
    }

    private void DiscardFailed(string dir)
    {
        var result = System.Windows.MessageBox.Show(
            L("Diese fehlgeschlagene Aufnahme endgültig löschen?", "Permanently delete this failed recording?"),
            "CallNotes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Löschen fehlgeschlagen: {ex.Message}", $"Deletion failed: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { RefreshFailed(); }
    }

    // ============================================================
    // Einstellungen: Laden / Speichern
    // ============================================================

    private void SettingsToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _settingsExpanded = !_settingsExpanded;
        SettingsBody.Visibility = _settingsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsChevron.Text = _settingsExpanded ? "" : ""; // ChevronDown / ChevronRight
    }

    private void LoadSettingsIntoUi()
    {
        var c = TryReadJsonObject(Paths.ConfigFile) ?? new JsonObject();

        _notesDir = c["notesDir"]?.GetValue<string>() ?? Paths.NotesDir;
        _audioDir = c["audioDir"]?.GetValue<string>() ?? Path.Combine(Paths.DataRoot, "audio");
        _mirrorDir = c["mirrorDir"]?.GetValue<string>() ?? "";
        _mirrorEnabled = !string.IsNullOrWhiteSpace(_mirrorDir);
        NotesDirValue.Text = _notesDir;
        AudioDirValue.Text = _audioDir;
        UpdateMirrorUi();

        SetSegment("lang", c["uiLanguage"]?.GetValue<string>() ?? "system");
        SetSegment("trans", c["transcriber"]?.GetValue<string>() ?? "local");
        SetSegment("sum", c["summarizer"]?.GetValue<string>() ?? "claude");

        GroqKeyInput.Text = c["groqApiKey"]?.GetValue<string>() ?? "";
        SummarizerUrlInput.Text = c["summarizerUrl"]?.GetValue<string>() ?? "";
        SummarizerModelInput.Text = c["summarizerModel"]?.GetValue<string>() ?? "";
        SummarizerApiKeyInput.Text = c["summarizerApiKey"]?.GetValue<string>() ?? "";
        NtfyInput.Text = c["ntfyUrl"]?.GetValue<string>() ?? "";

        var sections = (c["noteSections"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToHashSet()
                       ?? new HashSet<string> { "kurzfassung", "besprochen", "todos" };
        ChkKurzfassung.IsChecked = sections.Contains("kurzfassung");
        ChkBesprochen.IsChecked = sections.Contains("besprochen");
        ChkTodos.IsChecked = sections.Contains("todos");
        ChkFollowup.IsChecked = sections.Contains("followup");

        var dest = c["destinations"] as JsonObject;
        ChkNextcloud.IsChecked = dest?["nextcloud"]?.GetValue<bool>() ?? false;
        ChkNotion.IsChecked = dest?["notion"]?.GetValue<bool>() ?? false;
        NextcloudUrlInput.Text = c["nextcloudUrl"]?.GetValue<string>() ?? "";
        NextcloudUserInput.Text = c["nextcloudUser"]?.GetValue<string>() ?? "";
        NextcloudPassInput.Text = c["nextcloudAppPass"]?.GetValue<string>() ?? "";
        NotionTokenInput.Text = c["notionToken"]?.GetValue<string>() ?? "";
        NotionParentInput.Text = c["notionParent"]?.GetValue<string>() ?? "";

        UpdateTransRows();
        UpdateSumRows();
        UpdateDestRows();

        SettingsBody.Visibility = Visibility.Collapsed;
        SettingsChevron.Text = "";
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var c = TryReadJsonObject(Paths.ConfigFile) ?? new JsonObject();

            c["notesDir"] = JsonValue.Create(_notesDir);
            c["audioDir"] = JsonValue.Create(_audioDir);
            c["mirrorDir"] = JsonValue.Create(_mirrorEnabled ? _mirrorDir : "");
            c["uiLanguage"] = JsonValue.Create(GetSegment("lang"));
            c["transcriber"] = JsonValue.Create(GetSegment("trans"));
            c["summarizer"] = JsonValue.Create(GetSegment("sum"));
            c["groqApiKey"] = JsonValue.Create(GroqKeyInput.Text ?? "");
            c["summarizerUrl"] = JsonValue.Create(SummarizerUrlInput.Text ?? "");
            c["summarizerModel"] = JsonValue.Create(SummarizerModelInput.Text ?? "");
            c["summarizerApiKey"] = JsonValue.Create(SummarizerApiKeyInput.Text ?? "");
            c["ntfyUrl"] = JsonValue.Create(NtfyInput.Text ?? "");

            var sections = new JsonArray();
            if (ChkKurzfassung.IsChecked == true) sections.Add(JsonValue.Create("kurzfassung"));
            if (ChkBesprochen.IsChecked == true) sections.Add(JsonValue.Create("besprochen"));
            if (ChkTodos.IsChecked == true) sections.Add(JsonValue.Create("todos"));
            if (ChkFollowup.IsChecked == true) sections.Add(JsonValue.Create("followup"));
            c["noteSections"] = sections;

            var dest = c["destinations"] as JsonObject ?? new JsonObject();
            dest["appleNotes"] = JsonValue.Create((c["destinations"] as JsonObject)?["appleNotes"]?.GetValue<bool>() ?? false);
            dest["nextcloud"] = JsonValue.Create(ChkNextcloud.IsChecked == true);
            dest["notion"] = JsonValue.Create(ChkNotion.IsChecked == true);
            c["destinations"] = dest;
            c["nextcloudUrl"] = JsonValue.Create(NextcloudUrlInput.Text ?? "");
            c["nextcloudUser"] = JsonValue.Create(NextcloudUserInput.Text ?? "");
            c["nextcloudAppPass"] = JsonValue.Create(NextcloudPassInput.Text ?? "");
            c["notionToken"] = JsonValue.Create(NotionTokenInput.Text ?? "");
            c["notionParent"] = JsonValue.Create(NotionParentInput.Text ?? "");

            WriteJsonAtomic(Paths.ConfigFile, c);
            L10n.Refresh();
            ApplyLocalization();

            string restart = TryRestartDaemon();
            FlashHint(L("Gespeichert.", "Saved.") + (restart.Length > 0 ? " " + restart : ""));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Speichern fehlgeschlagen: {ex.Message}", $"Save failed: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// „Neustart“: laeuft ein eigenstaendiger calltap-watch-Daemon UND wird gerade nicht
    /// aufgenommen, wird er mit der neuen Config neu gestartet. Waehrend einer Aufnahme
    /// bewusst NICHT (wuerde den laufenden Anruf abschneiden).
    /// </summary>
    private string TryRestartDaemon()
    {
        if (File.Exists(Path.Combine(Paths.StateDir, "current-call.json")))
            return L("Neustart nach Anrufende.", "Restart after call ends.");
        try
        {
            var procs = Process.GetProcessesByName("calltap");
            if (procs.Length == 0) return "";
            string? exe = null;
            try { exe = procs[0].MainModule?.FileName; } catch { }
            foreach (var p in procs) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe, "watch") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                return L("Daemon neu gestartet.", "Daemon restarted.");
            }
            return L("Daemon gestoppt (Pfad unbekannt).", "Daemon stopped (path unknown).");
        }
        catch { return ""; }
    }

    // ---- Segmented-Control-Helfer ----

    private RadioButton[] SegGroup(string group) => group switch
    {
        "lang" => new[] { LangSystem, LangDe, LangEn },
        "trans" => new[] { TransLocal, TransGroq, TransParakeet },
        "sum" => new[] { SumClaude, SumOpenAI, SumOff },
        _ => System.Array.Empty<RadioButton>(),
    };

    private void SetSegment(string group, string tag)
    {
        var segs = SegGroup(group);
        foreach (var s in segs) if ((string)s.Tag == tag) { s.IsChecked = true; return; }
        if (segs.Length > 0) segs[0].IsChecked = true;
    }

    private string GetSegment(string group)
    {
        foreach (var s in SegGroup(group)) if (s.IsChecked == true) return (string)s.Tag;
        return (string)(SegGroup(group).FirstOrDefault()?.Tag ?? "");
    }

    private void TransSegment_Changed(object sender, RoutedEventArgs e) { if (!_loading) UpdateTransRows(); }
    private void SumSegment_Changed(object sender, RoutedEventArgs e) { if (!_loading) UpdateSumRows(); }
    private void DestCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading) UpdateDestRows(); }

    private void UpdateTransRows()
    {
        bool groq = GetSegment("trans") == "groq";
        GroqKeyRow.Visibility = groq ? Visibility.Visible : Visibility.Collapsed;
        TransSpacer.Visibility = groq ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSumRows()
    {
        bool openai = GetSegment("sum") == "openai";
        OpenAiRow.Visibility = openai ? Visibility.Visible : Visibility.Collapsed;
        SumSpacer.Visibility = openai ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDestRows()
    {
        NextcloudRow.Visibility = ChkNextcloud.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NotionRow.Visibility = ChkNotion.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Speicherorte / Kopie-Toggle ----

    private void BrowseNotesButton_Click(object sender, RoutedEventArgs e)
    { var p = PickFolder(_notesDir); if (p != null) { _notesDir = p; NotesDirValue.Text = p; } }

    private void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    { var p = PickFolder(_audioDir); if (p != null) { _audioDir = p; AudioDirValue.Text = p; } }

    private void BrowseMirrorButton_Click(object sender, RoutedEventArgs e)
    { var p = PickFolder(_mirrorDir); if (p != null) { _mirrorDir = p; _mirrorEnabled = true; UpdateMirrorUi(); } }

    private void MirrorToggleButton_Click(object sender, RoutedEventArgs e)
    { _mirrorEnabled = !_mirrorEnabled; UpdateMirrorUi(); }

    private void UpdateMirrorUi()
    {
        MirrorToggleButton.Content = _mirrorEnabled ? L("an", "on") : L("aus", "off");
        MirrorDirValue.Text = string.IsNullOrWhiteSpace(_mirrorDir) ? L("— nicht gesetzt", "— not set") : _mirrorDir;
        MirrorDirValue.Foreground = _mirrorEnabled ? Brush("BrushTextPrimary") : Brush("BrushTextTertiary");
    }

    private void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ResolveVenvPython(), UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(ResolvePipelineScript("callnotes_sync.py"));
            psi.EnvironmentVariables["CALLNOTES_CONFIG"] = Paths.ConfigFile;
            Process.Start(psi);
            FlashHint(L("Sync gestartet.", "Sync started."));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(L($"Sync fehlgeschlagen: {ex.Message}", $"Sync failed: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    // ============================================================
    // Hilfsfunktionen
    // ============================================================

    private SolidColorBrush Brush(string key) => (SolidColorBrush)FindResource(key);

    private void FlashHint(string text)
    {
        SettingsSavedHint.Text = text;
        SettingsSavedHint.Visibility = Visibility.Visible;
    }

    private static string? PickFolder(string initial)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static JsonObject? TryReadJsonObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return null; }
    }

    private static JsonArray? TryReadJsonArray(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray : null; }
        catch { return null; }
    }

    private static void WriteJsonAtomic(string path, JsonNode node)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ResolveVenvPython()
    {
        var config = TryReadJsonObject(Paths.ConfigFile);
        string? venvPython = config?["venvPython"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(venvPython) && File.Exists(venvPython)) return venvPython!;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultVenv = Path.Combine(localAppData, "callnotes", "venv", "Scripts", "python.exe");
        return File.Exists(defaultVenv) ? defaultVenv : "python";
    }

    private static string ResolvePipelineScript(string fileName)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "pipeline", fileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "pipeline", fileName),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return candidates[0];
    }
}
