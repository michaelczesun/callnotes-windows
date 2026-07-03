using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using static CallNotesTray.L10n;

namespace CallNotesTray;

/// <summary>
/// Das Panel-Fenster, das sich beim Klick auf das Tray-Icon oeffnet.
/// Pollt alle 1s state/current-call.json + levels.json + state/pending/*.json,
/// zeigt laufenden Anruf, Pegel, Teilnehmer-Eingabe, Sprecher-Zuordnungen,
/// letzte Notizen, fehlgeschlagene Aufnahmen und ein minimales Settings-Panel.
///
/// Alle Datei-I/O ist bewusst defensiv (try/catch, nie eine UnhandledException
/// aus dem Poll-Tick heraus) — ein fehlendes/kaputtes State-File darf das Panel
/// nie zum Absturz bringen, gleiche Haltung wie im Rest des Projekts.
/// </summary>
public partial class MainPanel : Window
{
    private readonly DispatcherTimer _pollTimer;
    private string? _activeCallDir;
    private DateTime? _activeCallStartUtc;
    private bool _settingsExpanded = false;

    public MainPanel()
    {
        InitializeComponent();
        ApplyLocalization();
        PositionNearTray();

        LoadSettingsIntoUi();
        RefreshAll();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => RefreshAll();
        _pollTimer.Start();

        Closed += (_, _) => _pollTimer.Stop();
    }

    private void PositionNearTray()
    {
        // Unten rechts andocken, analog zur macOS-Menueleisten-Popover-Position.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - 40; // vorlaeufig, wird nach erstem Layout-Pass unten korrigiert
        SizeChanged += (_, _) =>
        {
            Top = Math.Max(workArea.Top + 12, workArea.Bottom - ActualHeight - 12);
        };
    }

    // ---------------------------------------------------------------
    // Lokalisierung
    // ---------------------------------------------------------------

    private void ApplyLocalization()
    {
        Title = "CallNotes";
        ActiveCallTitle.Text = L("Aktiver Anruf", "Active call");
        ParticipantsLabel.Text = L("Teilnehmer (Komma-getrennt)", "Participants (comma-separated)");
        SaveParticipantsButton.Content = L("Speichern", "Save");
        DiscardCallButton.Content = L("Diesen Anruf nicht aufnehmen", "Don't record this call");
        NoActiveCallText.Text = L("Aktuell läuft kein Anruf.", "No call is currently being recorded.");
        MicLabel.Text = L("Mikro", "Mic");
        SysLabel.Text = L("System", "System");
        PendingTitle.Text = L("Sprecher zuordnen", "Assign speakers");
        RecentNotesTitle.Text = L("Letzte Notizen", "Recent notes");
        RecentNotesEmpty.Text = L("Noch keine Notizen.", "No notes yet.");
        FailedTitle.Text = L("Fehlgeschlagene Aufnahmen", "Failed recordings");
        SettingsTitle.Text = L("Einstellungen", "Settings");
        OutDirLabel.Text = L("Datenordner", "Data folder");
        NotesDirLabel.Text = L("Notizen-Ordner", "Notes folder");
        LanguageLabel.Text = L("Sprache", "Language");
        TranscriberLabel.Text = L("Transkription", "Transcription");
        GroqKeyLabel.Text = L("Groq API-Key", "Groq API key");
        SummarizerLabel.Text = L("Zusammenfassung", "Summarization");
        SummarizerUrlLabel.Text = L("API-URL", "API URL");
        SummarizerModelLabel.Text = L("Modell", "Model");
        SummarizerApiKeyLabel.Text = L("API-Key", "API key");
        SaveSettingsButton.Content = L("Einstellungen speichern", "Save settings");

        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            item.Content = (string)item.Tag switch
            {
                "de" => "Deutsch",
                "en" => "English",
                _ => "System",
            };
        }
        foreach (ComboBoxItem item in TranscriberCombo.Items)
        {
            item.Content = (string)item.Tag switch
            {
                "groq" => L("Groq (Cloud)", "Groq (cloud)"),
                "parakeet" => "Parakeet",
                _ => L("Lokal (whisper.cpp)", "Local (whisper.cpp)"),
            };
        }
        foreach (ComboBoxItem item in SummarizerCombo.Items)
        {
            item.Content = (string)item.Tag switch
            {
                "openai" => L("OpenAI-kompatibel", "OpenAI-compatible"),
                "off" => L("Aus", "Off"),
                _ => "Claude",
            };
        }
    }

    // ---------------------------------------------------------------
    // Zentraler Poll-Tick
    // ---------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshDaemonStatus();
        RefreshActiveCall();
        RefreshPending();
        RefreshRecentNotes();
        RefreshFailed();
    }

    private void RefreshDaemonStatus()
    {
        bool running = IsWatchDaemonRunning();
        DaemonStatusDot.Fill = running
            ? (SolidColorBrush)FindResource("BrushSuccess")
            : (SolidColorBrush)FindResource("BrushDanger");
        DaemonStatusText.Text = running
            ? L("Läuft", "Running")
            : L("Gestoppt", "Stopped");
    }

    /// <summary>
    /// Der Watch-Daemon ist entweder der eingebettete In-Process-Task (CallTap.Core,
    /// sobald angebunden) oder ein separat laufender "calltap.exe watch"-Prozess
    /// (headless/Scheduled-Task-Betrieb, siehe Contract Abschnitt 5). Fuer die reine
    /// Statusanzeige reicht ein Prozess-Check nach Namen.
    /// </summary>
    private static bool IsWatchDaemonRunning()
    {
        try
        {
            return Process.GetProcessesByName("calltap").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Aktiver Anruf: current-call.json + levels.json
    // ---------------------------------------------------------------

    private void RefreshActiveCall()
    {
        string currentCallFile = Path.Combine(Paths.StateDir, "current-call.json");
        JsonObject? call = TryReadJsonObject(currentCallFile);

        if (call == null)
        {
            _activeCallDir = null;
            _activeCallStartUtc = null;
            ActiveCallCard.Visibility = Visibility.Collapsed;
            NoActiveCallCard.Visibility = Visibility.Visible;
            return;
        }

        ActiveCallCard.Visibility = Visibility.Visible;
        NoActiveCallCard.Visibility = Visibility.Collapsed;

        _activeCallDir = call["dir"]?.GetValue<string>();
        string appName = call["appName"]?.GetValue<string>() ?? call["app"]?.GetValue<string>() ?? "?";
        ActiveCallApp.Text = appName;

        if (DateTime.TryParse(call["start"]?.GetValue<string>(), out var start))
        {
            _activeCallStartUtc = start.ToUniversalTime();
        }

        if (_activeCallStartUtc.HasValue)
        {
            var elapsed = DateTime.UtcNow - _activeCallStartUtc.Value;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            ActiveCallDuration.Text = elapsed.Hours > 0
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        // Aktuelle Teilnehmer aus participants.json vorbefuellen, falls schon
        // welche fuer diesen Anruf hinterlegt sind (z.B. nach Neuoeffnen des Panels).
        if (!string.IsNullOrEmpty(_activeCallDir) && !ParticipantsInput.IsFocused)
        {
            string participantsFile = Path.Combine(_activeCallDir, "participants.json");
            var participants = TryReadJsonArray(participantsFile);
            if (participants != null)
            {
                var names = participants.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0);
                ParticipantsInput.Text = string.Join(", ", names);
            }
        }

        // Pegel-Balken aus levels.json (siehe Contract 3.5: {"mic":0.42,"sys":0.18,"t":...}).
        if (!string.IsNullOrEmpty(_activeCallDir))
        {
            string levelsFile = Path.Combine(_activeCallDir, "levels.json");
            var levels = TryReadJsonObject(levelsFile);
            double mic = ClampLevel(levels?["mic"]?.GetValue<double>() ?? 0);
            double sys = ClampLevel(levels?["sys"]?.GetValue<double>() ?? 0);
            UpdateLevelBar(MicLevelBar, mic);
            UpdateLevelBar(SysLevelBar, sys);
        }
        else
        {
            UpdateLevelBar(MicLevelBar, 0);
            UpdateLevelBar(SysLevelBar, 0);
        }
    }

    private static double ClampLevel(double v) => Math.Max(0, Math.Min(1, v));

    private void UpdateLevelBar(FrameworkElement bar, double level)
    {
        // Track-Breite = Elternspalte; Pegel-Balken wird proportional skaliert.
        if (bar.Parent is Border track)
        {
            double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : 260;
            bar.Width = trackWidth * level;
        }
    }

    private void SaveParticipantsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeCallDir))
        {
            return;
        }

        var names = (ParticipantsInput.Text ?? "")
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        var array = new JsonArray();
        foreach (var name in names) array.Add(JsonValue.Create(name));

        string path = Path.Combine(_activeCallDir, "participants.json");
        WriteJsonAtomic(path, array);

        SettingsSavedHint.Text = L("Teilnehmer gespeichert.", "Participants saved.");
        SettingsSavedHint.Visibility = Visibility.Visible;
    }

    private void DiscardCallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeCallDir)) return;

        var result = System.Windows.MessageBox.Show(
            L("Diesen Anruf wirklich verwerfen? Es wird nichts gespeichert.",
              "Really discard this call? Nothing will be saved."),
            L("Anruf verwerfen", "Discard call"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            // Abort-Datei-Protokoll laut Contract 6.7: der naechste Poll-Tick des
            // Watchers sieht die Datei, stoppt beide Capture-Threads und loescht
            // das Aufnahmeverzeichnis komplett — hier wird NICHT selbst geloescht,
            // um keine Race Condition mit dem laufenden Capture-Thread zu riskieren.
            string abortFile = Path.Combine(_activeCallDir, "abort");
            File.WriteAllText(abortFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            System.Windows.MessageBox.Show(
                L("Konnte Abbruch nicht auslösen.", "Could not trigger abort."),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------
    // Ausstehende Sprecher-Zuordnungen: state/pending/*.json
    // ---------------------------------------------------------------

    private void RefreshPending()
    {
        PendingList.Children.Clear();

        List<string> files;
        try
        {
            if (!Directory.Exists(Paths.PendingDir))
            {
                PendingSection.Visibility = Visibility.Collapsed;
                return;
            }
            files = Directory.GetFiles(Paths.PendingDir, "*.json").OrderByDescending(f => f).ToList();
        }
        catch
        {
            PendingSection.Visibility = Visibility.Collapsed;
            return;
        }

        if (files.Count == 0)
        {
            PendingSection.Visibility = Visibility.Collapsed;
            return;
        }

        PendingSection.Visibility = Visibility.Visible;

        foreach (var file in files)
        {
            var obj = TryReadJsonObject(file);
            if (obj == null) continue;

            var card = BuildPendingCard(file, obj);
            if (card != null) PendingList.Children.Add(card);
        }
    }

    /// <summary>
    /// Baut eine Karte pro pending-Datei: pro Sprecher ein Dropdown (bestehende
    /// participants als Vorschlaege + suggestion vorausgewaehlt) + "Übernehmen"-
    /// Button, der pipeline/apply_speakers.py aufruft (Contract: Windows-Analogon
    /// von apply-speakers.sh).
    /// </summary>
    private Border? BuildPendingCard(string file, JsonObject obj)
    {
        string stamp = obj["stamp"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
        string app = obj["app"]?.GetValue<string>() ?? "";
        var speakersNode = obj["speakers"] as JsonArray;
        var participantsNode = obj["participants"] as JsonArray;
        var participantOptions = participantsNode?
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0)
            .ToList() ?? new List<string>();

        if (speakersNode == null || speakersNode.Count == 0) return null;

        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        outer.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(app) ? stamp : $"{stamp} ({app})",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var speakerCombos = new List<(string label, ComboBox combo)>();

        foreach (var speakerNode in speakersNode)
        {
            if (speakerNode is not JsonObject speaker) continue;
            string label = speaker["label"]?.GetValue<string>() ?? "?";
            string clip = speaker["clip"]?.GetValue<string>() ?? "";
            string suggestion = speaker["suggestion"]?.GetValue<string>() ?? "";

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (SolidColorBrush)FindResource("BrushTextSecondary"),
                FontSize = 12,
            };
            Grid.SetColumn(labelBlock, 0);

            var combo = new ComboBox { IsEditable = true, Margin = new Thickness(0, 0, 6, 0) };
            foreach (var name in participantOptions) combo.Items.Add(name);
            combo.Text = suggestion;
            Grid.SetColumn(combo, 1);

            var playButton = new Button
            {
                Content = "▶",
                Style = (Style)FindResource("PillButton"),
                Tag = clip,
            };
            playButton.Click += (_, _) => PlayClip(clip);
            Grid.SetColumn(playButton, 2);

            row.Children.Add(labelBlock);
            row.Children.Add(combo);
            row.Children.Add(playButton);
            outer.Children.Add(row);

            speakerCombos.Add((label, combo));
        }

        var applyButton = new Button
        {
            Content = L("Übernehmen", "Apply"),
            Style = (Style)FindResource("AccentButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        applyButton.Click += (_, _) => ApplyPendingSpeakers(file, stamp, speakerCombos);
        outer.Children.Add(applyButton);

        return new Border
        {
            Background = (SolidColorBrush)FindResource("BrushCardBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = outer,
        };
    }

    private void PlayClip(string clipPath)
    {
        if (string.IsNullOrEmpty(clipPath) || !File.Exists(clipPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(clipPath) { UseShellExecute = true });
        }
        catch
        {
            // Kein registrierter Player fuer .m4a o.ae. -> stumm ignorieren,
            // der Nutzer sieht ohnehin die Dropdown-Vorbelegung als Vorschlag.
        }
    }

    /// <summary>
    /// Ruft "python pipeline/apply_speakers.py &lt;pending-file&gt; &lt;zuordnungen-json&gt;"
    /// auf (venvPython aus config.json, Fallback "python"). Bei Erfolg wird die
    /// pending-Datei entfernt (macht regulaer bereits das Python-Skript selbst,
    /// hier zusaetzlich als Sicherheitsnetz falls das Skript das nicht tut).
    /// </summary>
    private void ApplyPendingSpeakers(string pendingFile, string stamp, List<(string label, ComboBox combo)> combos)
    {
        var assignment = new JsonObject();
        foreach (var (label, combo) in combos)
        {
            string name = (combo.Text ?? "").Trim();
            assignment[label] = JsonValue.Create(name);
        }

        string tempAssignmentFile = Path.Combine(Path.GetTempPath(), $"callnotes-speakers-{stamp}.json");
        try
        {
            File.WriteAllText(tempAssignmentFile, assignment.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            string pythonExe = ResolveVenvPython();
            string pipelineScript = ResolvePipelineScript("apply_speakers.py");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(pipelineScript);
            psi.ArgumentList.Add(pendingFile);
            psi.ArgumentList.Add(tempAssignmentFile);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);

            if (proc != null && proc.ExitCode == 0)
            {
                TryDeleteFile(pendingFile);
            }
            else
            {
                string stderr = proc?.StandardError.ReadToEnd() ?? "";
                System.Windows.MessageBox.Show(
                    L($"Sprecher-Zuordnung fehlgeschlagen:\n{stderr}", $"Speaker assignment failed:\n{stderr}"),
                    "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                L($"Konnte apply_speakers.py nicht starten: {ex.Message}", $"Could not start apply_speakers.py: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TryDeleteFile(tempAssignmentFile);
            RefreshPending();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------
    // Letzte Notizen
    // ---------------------------------------------------------------

    private void RefreshRecentNotes()
    {
        RecentNotesList.Children.Clear();

        List<string> notes;
        try
        {
            string notesDir = Paths.NotesDir;
            if (!Directory.Exists(notesDir))
            {
                RecentNotesEmpty.Visibility = Visibility.Visible;
                return;
            }
            notes = Directory.GetFiles(notesDir, "*.md")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5)
                .ToList();
        }
        catch
        {
            RecentNotesEmpty.Visibility = Visibility.Visible;
            return;
        }

        if (notes.Count == 0)
        {
            RecentNotesEmpty.Visibility = Visibility.Visible;
            return;
        }

        RecentNotesEmpty.Visibility = Visibility.Collapsed;

        foreach (var note in notes)
        {
            var link = new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(note),
                Foreground = (SolidColorBrush)FindResource("BrushAccent"),
                Cursor = System.Windows.Input.Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            link.MouseLeftButtonUp += (_, _) => OpenNote(note);
            RecentNotesList.Children.Add(link);
        }
    }

    private static void OpenNote(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Keine mit .md verknuepfte App -> stumm ignorieren.
        }
    }

    // ---------------------------------------------------------------
    // Fehlgeschlagene Aufnahmen
    // ---------------------------------------------------------------

    private void RefreshFailed()
    {
        FailedList.Children.Clear();

        List<string> dirs;
        try
        {
            if (!Directory.Exists(Paths.FailedDir))
            {
                FailedSection.Visibility = Visibility.Collapsed;
                return;
            }
            dirs = Directory.GetDirectories(Paths.FailedDir).OrderByDescending(d => d).ToList();
        }
        catch
        {
            FailedSection.Visibility = Visibility.Collapsed;
            return;
        }

        if (dirs.Count == 0)
        {
            FailedSection.Visibility = Visibility.Collapsed;
            return;
        }

        FailedSection.Visibility = Visibility.Visible;

        foreach (var dir in dirs)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = Path.GetFileName(dir),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(nameBlock, 0);

            var retryButton = new Button { Content = L("Erneut versuchen", "Retry"), Style = (Style)FindResource("PillButton"), Margin = new Thickness(0, 0, 6, 0) };
            retryButton.Click += (_, _) => RetryFailed(dir);
            Grid.SetColumn(retryButton, 1);

            var discardButton = new Button { Content = L("Verwerfen", "Discard"), Style = (Style)FindResource("DangerButton") };
            discardButton.Click += (_, _) => DiscardFailed(dir);
            Grid.SetColumn(discardButton, 2);

            row.Children.Add(nameBlock);
            row.Children.Add(retryButton);
            row.Children.Add(discardButton);
            FailedList.Children.Add(row);
        }
    }

    private void RetryFailed(string dir)
    {
        try
        {
            string pythonExe = ResolveVenvPython();
            string pipelineScript = ResolvePipelineScript("process_call.py");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(pipelineScript);
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                L($"Konnte Verarbeitung nicht neu starten: {ex.Message}", $"Could not restart processing: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshFailed();
        }
    }

    private void DiscardFailed(string dir)
    {
        var result = System.Windows.MessageBox.Show(
            L("Diese fehlgeschlagene Aufnahme endgültig löschen?", "Permanently delete this failed recording?"),
            "CallNotes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                L($"Löschen fehlgeschlagen: {ex.Message}", $"Deletion failed: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshFailed();
        }
    }

    // ---------------------------------------------------------------
    // Settings (minimal): Speicherorte, Sprache, Transcriber, Summarizer
    // ---------------------------------------------------------------

    private void SettingsToggle_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _settingsExpanded = !_settingsExpanded;
        SettingsBody.Visibility = _settingsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggle.Text = _settingsExpanded ? "▴" : "▾";
    }

    private void LoadSettingsIntoUi()
    {
        var config = TryReadJsonObject(Paths.ConfigFile) ?? new JsonObject();

        OutDirInput.Text = config["outDir"]?.GetValue<string>() ?? Paths.DataRoot;
        NotesDirInput.Text = config["notesDir"]?.GetValue<string>() ?? Paths.NotesDir;

        SelectComboByTag(LanguageCombo, config["uiLanguage"]?.GetValue<string>() ?? "system");
        SelectComboByTag(TranscriberCombo, config["transcriber"]?.GetValue<string>() ?? "local");
        SelectComboByTag(SummarizerCombo, config["summarizer"]?.GetValue<string>() ?? "claude");

        GroqKeyInput.Text = config["groqApiKey"]?.GetValue<string>() ?? "";
        SummarizerUrlInput.Text = config["summarizerUrl"]?.GetValue<string>() ?? "";
        SummarizerModelInput.Text = config["summarizerModel"]?.GetValue<string>() ?? "";
        SummarizerApiKeyInput.Text = config["summarizerApiKey"]?.GetValue<string>() ?? "";

        UpdateTranscriberFieldVisibility();
        UpdateSummarizerFieldVisibility();

        // Settings-Block standardmaessig eingeklappt, wie im Panel-Kontrakt gefordert
        // (Panel bleibt kompakt; "Minimal"-Anspruch aus dem Auftrag).
        SettingsBody.Visibility = Visibility.Collapsed;
        SettingsToggle.Text = "▾";
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nur UI-Vorschau; wirksam wird es erst nach "Einstellungen speichern"
        // (dann schreibt SaveSettingsButton_Click uiLanguage + ruft L10n.Refresh()).
    }

    private void TranscriberCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTranscriberFieldVisibility();

    private void SummarizerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummarizerFieldVisibility();

    private void UpdateTranscriberFieldVisibility()
    {
        bool isGroq = (TranscriberCombo.SelectedItem as ComboBoxItem)?.Tag as string == "groq";
        GroqKeyLabel.Visibility = isGroq ? Visibility.Visible : Visibility.Collapsed;
        GroqKeyInput.Visibility = isGroq ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummarizerFieldVisibility()
    {
        bool isOpenAi = (SummarizerCombo.SelectedItem as ComboBoxItem)?.Tag as string == "openai";
        SummarizerUrlLabel.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        SummarizerUrlInput.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        SummarizerModelLabel.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        SummarizerModelInput.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        SummarizerApiKeyLabel.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        SummarizerApiKeyInput.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseOutDirButton_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder(OutDirInput.Text);
        if (picked != null) OutDirInput.Text = picked;
    }

    private void BrowseNotesDirButton_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder(NotesDirInput.Text);
        if (picked != null) NotesDirInput.Text = picked;
    }

    private static string? PickFolder(string initial)
    {
        // OpenFolderDialog ist seit .NET 8 Teil von Microsoft.Win32 (WinForms-frei).
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Schreibt config.json atomar (Temp-Datei + File.Replace), damit ein
    /// gleichzeitig laufender Watch-Daemon nie eine halb geschriebene Datei liest —
    /// gleiche Atomaritaets-Anforderung wie beim Mac-Original (WatchConfig.save()).
    /// Bestehende, hier nicht editierte Keys bleiben unveraendert erhalten.
    /// </summary>
    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = TryReadJsonObject(Paths.ConfigFile) ?? new JsonObject();

            config["outDir"] = JsonValue.Create(OutDirInput.Text?.Trim() ?? "");
            config["notesDir"] = JsonValue.Create(NotesDirInput.Text?.Trim() ?? "");
            config["uiLanguage"] = JsonValue.Create((string)((ComboBoxItem)LanguageCombo.SelectedItem).Tag);
            config["transcriber"] = JsonValue.Create((string)((ComboBoxItem)TranscriberCombo.SelectedItem).Tag);
            config["groqApiKey"] = JsonValue.Create(GroqKeyInput.Text ?? "");
            config["summarizer"] = JsonValue.Create((string)((ComboBoxItem)SummarizerCombo.SelectedItem).Tag);
            config["summarizerUrl"] = JsonValue.Create(SummarizerUrlInput.Text ?? "");
            config["summarizerModel"] = JsonValue.Create(SummarizerModelInput.Text ?? "");
            config["summarizerApiKey"] = JsonValue.Create(SummarizerApiKeyInput.Text ?? "");

            WriteJsonAtomic(Paths.ConfigFile, config);

            L10n.Refresh();
            ApplyLocalization();

            SettingsSavedHint.Text = L("Gespeichert.", "Saved.");
            SettingsSavedHint.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                L($"Speichern fehlgeschlagen: {ex.Message}", $"Save failed: {ex.Message}"),
                "CallNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------
    // JSON-/Pfad-Hilfsfunktionen
    // ---------------------------------------------------------------

    private static JsonObject? TryReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonArray? TryReadJsonArray(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            return JsonNode.Parse(text) as JsonArray;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atomares Schreiben: erst in eine Temp-Datei im selben Verzeichnis, dann
    /// File.Move mit Overwrite (auf NTFS atomar innerhalb desselben Volumes) —
    /// verhindert, dass ein gleichzeitig lesender Prozess (Watcher/Pipeline) eine
    /// halb geschriebene Datei sieht.
    /// </summary>
    private static void WriteJsonAtomic(string path, JsonNode node)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ResolveVenvPython()
    {
        var config = TryReadJsonObject(Paths.ConfigFile);
        string? venvPython = config?["venvPython"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(venvPython) && File.Exists(venvPython)) return venvPython!;

        // Fallback: venv unter %LOCALAPPDATA%\callnotes\venv\Scripts\python.exe
        // (Default aus Contract 3.1), sonst schlicht "python" (PATH).
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultVenv = Path.Combine(localAppData, "callnotes", "venv", "Scripts", "python.exe");
        return File.Exists(defaultVenv) ? defaultVenv : "python";
    }

    private static string ResolvePipelineScript(string fileName)
    {
        // pipeline/ liegt relativ zur Installation neben dem Programmverzeichnis
        // (siehe Contract-Baumstruktur: .../callnotes-windows/pipeline/*.py).
        // Zur Laufzeit wird zuerst neben der EXE gesucht, dann eine Ebene darueber
        // (Entwicklungsszenario: dotnet run aus src/CallNotesTray/bin/... heraus).
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "pipeline", fileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "pipeline", fileName),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return candidates[0];
    }
}
