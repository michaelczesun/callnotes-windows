// Program.cs
// Subcommand-Dispatch fuer die Konsolenanwendung "calltap" (mirrors calltap.swift
// usage() + switch cmd) — siehe Contract Abschnitt 5 fuer den vollstaendigen
// CLI-Vertrag von procs / setup / record / watch, Abschnitt 6 fuer die
// Watch-Zustandsmaschine. Diese Datei buendelt bewusst CLI-Dispatch, Config-Laden,
// Pfadaufloesung, die Exe-Allowlist-Matching-Logik und die Watch-Poll-Loop in einer
// Datei, da fuer diesen Auftrag nur CallTap.csproj/Program.cs/
// ProcessLoopbackCapture.cs/MicCapture.cs/WavWriter.cs vorgesehen sind (kein
// separates CallTap.Core-Projekt mit Interop/Capture/Recording/Watch-Unterordnern
// wie im vollen Langfrist-Contract, Abschnitt 2).

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CallTap.Capture;
using CallTap.Recording;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CallTap
{
    /// <summary>Klassischer Einstiegspunkt (kein Top-Level-Statement), damit diese
    /// Datei zusaetzlich die internen Hilfstypen (CallTapConfig, AudioSessionProbe)
    /// im selben Namespace-Block deklarieren kann.</summary>
    internal static class CallTapProgram
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string cmd = args[0];
            string[] rest = args.Skip(1).ToArray();

            try
            {
                return cmd switch
                {
                    "procs" => await CmdProcs(rest),
                    "setup" => await CmdSetup(),
                    "record" => await CmdRecord(rest),
                    "watch" => await CmdWatch(rest),
                    "-h" or "--help" or "help" => PrintUsageAndReturn0(),
                    _ => UnknownCommand(cmd),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unerwarteter Fehler: {ex}");
                return 1;
            }
        }

        private static int PrintUsageAndReturn0() { PrintUsage(); return 0; }

        private static int UnknownCommand(string cmd)
        {
            Console.Error.WriteLine($"Unbekannter Befehl: {cmd}");
            PrintUsage();
            return 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                """
                calltap.exe procs [--watch]
                    Listet Prozesse mit aktiver Mikrofon-Session.
                    --watch: Bildschirm loeschen, alle 1.5s aktualisieren, Ctrl+C zum Beenden.

                calltap.exe setup
                    Einmalige Berechtigungs-/Faehigkeits-Pruefung (Mikrofon-Zugriff +
                    Process-Loopback-Selbsttest).

                calltap.exe record --out DIR [--seconds N] [--exe NAME] [--debug]
                    Manuelle Aufnahme, Ctrl+C stoppt. --exe beschraenkt die
                    System-Audio-Aufnahme auf den Prozessbaum des ersten laufenden
                    Prozesses mit diesem Namen (".exe" optional).

                calltap.exe watch [--config FILE] [--debug]
                    Daemon/Vordergrund-Loop; Default-Konfigpfad %APPDATA%\callnotes\config.json
                    (ueberschreibbar via --config oder CALLNOTES_CONFIG env var).
                """);
        }

        // ==================== procs ====================

        private static async Task<int> CmdProcs(string[] args)
        {
            bool watch = args.Contains("--watch");

            if (!watch)
            {
                PrintProcsOnce();
                return 0;
            }

            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            while (true)
            {
                Console.Clear();
                PrintProcsOnce();
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
            }
        }

        private static void PrintProcsOnce()
        {
            var sessions = AudioSessionProbe.GetActiveMicSessions();
            if (sessions.Count == 0)
            {
                Console.WriteLine("Keine aktiven Mikrofon-Sessions gefunden.");
                return;
            }

            Console.WriteLine($"{"PID",-8} {"Prozess",-28} Anzeigename");
            foreach (var s in sessions)
                Console.WriteLine($"{s.ProcessId,-8} {s.ProcessName,-28} {s.DisplayName}");
        }

        // ==================== setup ====================

        private static async Task<int> CmdSetup()
        {
            bool ok = true;

            Console.WriteLine("1) Pruefe Mikrofonzugriff...");
            if (AudioSessionProbe.HasWorkingCaptureDevice())
            {
                Console.WriteLine("   OK: Standard-Mikrofon-Endpunkt gefunden.");
            }
            else
            {
                Console.WriteLine("   FEHLER: Kein Mikrofon-Endpunkt verfuegbar oder Zugriff verweigert.");
                Console.WriteLine("   -> Einstellungen > Datenschutz und Sicherheit > Mikrofon: Desktop-Apps erlauben.");
                ok = false;
            }

            Console.WriteLine("2) Pruefe Process-Loopback-Aktivierung (Selbsttest)...");
            try
            {
                await ProcessLoopbackCapture.SelfTestAsync();
                Console.WriteLine("   OK: ActivateAudioInterfaceAsync + AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK funktioniert.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   FEHLER: {ex.Message}");
                Console.WriteLine("   -> Moegliche Ursachen: Windows aelter als Build 20348, kein Wiedergabegeraet,");
                Console.WriteLine("      oder 0x88890021 = ungueltige Stream-Flags (Bug melden).");
                ok = false;
            }

            return ok ? 0 : 1;
        }

        // ==================== record ====================

        private static async Task<int> CmdRecord(string[] args)
        {
            string? outDir = GetOption(args, "--out");
            string? exeName = GetOption(args, "--exe");
            string? secondsRaw = GetOption(args, "--seconds");
            bool debug = args.Contains("--debug");

            if (string.IsNullOrEmpty(outDir))
            {
                Console.Error.WriteLine("Fehlt: --out DIR");
                return 1;
            }

            outDir = Environment.ExpandEnvironmentVariables(outDir);
            Directory.CreateDirectory(outDir);

            int targetPid;
            bool excludeTree = false;

            if (!string.IsNullOrEmpty(exeName))
            {
                string bare = StripExeSuffix(exeName);
                var procs = Process.GetProcessesByName(bare);
                if (procs.Length == 0)
                {
                    Console.Error.WriteLine($"Kein laufender Prozess mit Namen '{exeName}' gefunden.");
                    return 1;
                }
                targetPid = procs[0].Id;
            }
            else
            {
                // Ohne --exe: "manuelle" Aufnahme ohne Bundle-Bezug, wie im Contract
                // beschrieben (globaler/manueller Modus). Da Process-Loopback zwingend
                // eine Ziel-PID braucht, wird hier der eigene Prozess als Ziel-PID
                // verwendet; das liefert praktisch Stille auf der System-Spur, was fuer
                // einen reinen Mic-Mitschnitt ("manuell", kein System-Audio-Ziel
                // ausgewaehlt) das erwartete Verhalten ist.
                targetPid = Environment.ProcessId;
            }

            if (debug)
                Console.WriteLine($"[debug] outDir={outDir} exe={exeName} targetPid={targetPid} excludeTree={excludeTree}");

            using var levelWriter = new LevelWriter(outDir);
            var mic = new MicCapture();
            var system = new ProcessLoopbackCapture();

            string micPath = Path.Combine(outDir, "mic.wav");
            string sysPath = Path.Combine(outDir, "system.wav");
            var start = DateTimeOffset.UtcNow;

            try
            {
                // System-Spur zuerst starten (Regel aus Contract 6.5: schlaegt eine
                // Spur fehl, darf die andere nicht alleine weiterlaufen).
                await system.StartAsync(targetPid, excludeTree, sysPath, levelWriter);
                mic.Start(micPath, deviceId: null, levelWriter);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Aufnahmestart fehlgeschlagen: {ex.Message}");
                try { system.Stop(); } catch { }
                try { mic.Stop(); } catch { }
                return 1;
            }

            Console.WriteLine($"REC START -> {outDir} (Ctrl+C zum Beenden)");

            var stopSignal = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopSignal.TrySetResult(); };

            if (int.TryParse(secondsRaw, out int seconds) && seconds > 0)
                await Task.WhenAny(stopSignal.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            else
                await stopSignal.Task;

            mic.Stop();
            system.Stop();
            mic.Dispose();
            system.Dispose();

            var end = DateTimeOffset.UtcNow;
            string metaPath = Path.Combine(outDir, "meta.json");
            var meta = new
            {
                app = exeName ?? "manuell",
                appName = string.IsNullOrEmpty(exeName) ? "manual" : StripExeSuffix(exeName).ToLowerInvariant(),
                start = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                durationSec = (end - start).TotalSeconds,
            };
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"REC STOP -> {metaPath}");
            return 0;
        }

        // ==================== watch ====================

        private static async Task<int> CmdWatch(string[] args)
        {
            string configPath = GetOption(args, "--config") ?? DefaultConfigPath();
            bool debug = args.Contains("--debug");

            var config = LoadConfig(configPath);
            string dataRoot = string.IsNullOrEmpty(config.OutDir)
                ? DefaultDataRoot()
                : Environment.ExpandEnvironmentVariables(config.OutDir);

            Console.WriteLine($"calltap watch — Konfig: {configPath}");
            Console.WriteLine($"Datenwurzel: {dataRoot}");
            Console.WriteLine($"Erlaubte Apps: {string.Join(", ", config.Apps)}");

            string stateDir = Path.Combine(dataRoot, "state");
            string pendingDir = Path.Combine(stateDir, "pending");
            string recRoot = Path.Combine(dataRoot, "rec");
            string failedDir = Path.Combine(dataRoot, "failed");
            string logDir = Path.Combine(dataRoot, "log");
            string currentCallFile = Path.Combine(stateDir, "current-call.json");

            Directory.CreateDirectory(stateDir);
            Directory.CreateDirectory(pendingDir);
            Directory.CreateDirectory(recRoot);
            Directory.CreateDirectory(failedDir);
            Directory.CreateDirectory(logDir);

            // Regel 1: Startup-Cleanup — verwaiste current-call.json + rec/-Ordner ohne meta.json.
            TryDelete(currentCallFile);
            foreach (var dir in Directory.GetDirectories(recRoot))
            {
                if (File.Exists(Path.Combine(dir, "meta.json"))) continue;
                TryDelete(Path.Combine(dir, "levels.json"));
                string dest = Path.Combine(failedDir, Path.GetFileName(dir));
                try
                {
                    if (Directory.Exists(dest)) dest += "_" + Guid.NewGuid().ToString("N")[..8];
                    Directory.Move(dir, dest);
                    Log($"Verwaiste Aufnahme nach Absturz -> failed/: {Path.GetFileName(dir)}");
                }
                catch (Exception ex)
                {
                    Log($"WARNUNG: konnte verwaistes Verzeichnis nicht verschieben: {dir} ({ex.Message})");
                }
            }

            // Regel 2: Selbsttest, warnen statt abstuerzen.
            try
            {
                await ProcessLoopbackCapture.SelfTestAsync();
                Log("Self-Test: Systemaudio-Aktivierung ok.");
            }
            catch (Exception ex)
            {
                Log($"WARNUNG: Systemaudio-Selbsttest fehlgeschlagen ({ex.Message}) — " +
                    "Windows < Build 20348, kein Wiedergabegeraet oder Stream-Flag-Problem (0x88890021).");
            }

            int selfPid = Environment.ProcessId;
            var loggedUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? suppressExe = null;
            DateTimeOffset? suppressIdleSince = null;
            DateTimeOffset? idleSince = null;
            DateTimeOffset? lastStartError = null;

            RunningRecording? active = null;
            bool stopRequested = false;

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested = true; };

            // Regel 3: Poll-Loop alle 2s.
            while (!stopRequested)
            {
                try
                {
                    // Abort-Marker-Datei (Regel 7, Nutzer-Discard).
                    if (active != null && File.Exists(Path.Combine(active.Dir, "abort")))
                    {
                        Log($"Aufnahme abgebrochen (Nutzer-Discard) {active.AppName} -> {active.Dir}");
                        StopRunningRecording(active);
                        TryDelete(currentCallFile);
                        try { Directory.Delete(active.Dir, recursive: true); } catch { }
                        suppressExe = NormalizeExe(active.AppName);
                        suppressIdleSince = null;
                        active = null;
                        idleSince = null;
                        continue;
                    }

                    var sessions = AudioSessionProbe.GetActiveMicSessions();
                    (int Pid, string ExeName)? matched = null;

                    foreach (var s in sessions)
                    {
                        if (s.ProcessId == selfPid) continue;
                        if (config.Matches(s.ProcessName))
                        {
                            matched = (s.ProcessId, s.ProcessName);
                            break;
                        }
                        if (loggedUnknown.Add(s.ProcessName))
                            Log($"Info: Prozess '{s.ProcessName}' hat aktives Mikrofon, steht aber nicht in der apps-Liste.");
                    }

                    // Regel 4: Suppress-Zustand nach Discard.
                    if (suppressExe != null)
                    {
                        bool stillSuppressed = matched.HasValue &&
                            string.Equals(NormalizeExe(matched.Value.ExeName), suppressExe, StringComparison.OrdinalIgnoreCase);
                        if (stillSuppressed)
                        {
                            suppressIdleSince = null;
                            matched = null;
                        }
                        else
                        {
                            suppressIdleSince ??= DateTimeOffset.UtcNow;
                            if ((DateTimeOffset.UtcNow - suppressIdleSince.Value).TotalSeconds >= config.StopGraceSeconds)
                            {
                                suppressExe = null;
                                suppressIdleSince = null;
                            }
                        }
                    }

                    if (active == null)
                    {
                        // Regel 5: neue Aufnahme starten.
                        if (matched.HasValue)
                        {
                            if (lastStartError.HasValue && (DateTimeOffset.UtcNow - lastStartError.Value).TotalSeconds < 60)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(2));
                                continue;
                            }

                            active = await TryStartRecording(matched.Value.Pid, matched.Value.ExeName, config, recRoot, currentCallFile);
                            if (active == null) lastStartError = DateTimeOffset.UtcNow;
                        }
                    }
                    else
                    {
                        // Regel 6: Maximaldauer.
                        if ((DateTimeOffset.UtcNow - active.Started).TotalHours >= config.MaxHours)
                        {
                            Log($"REC STOP (Maximaldauer erreicht) {active.AppName} -> {active.Dir}");
                            await FinishRecording(active, config, currentCallFile, failedDir: null, logDir);
                            active = null;
                            idleSince = null;
                        }
                        else
                        {
                            bool stillActive = matched.HasValue &&
                                string.Equals(NormalizeExe(matched.Value.ExeName), NormalizeExe(active.AppName), StringComparison.OrdinalIgnoreCase);

                            if (stillActive)
                            {
                                idleSince = null;
                            }
                            else
                            {
                                idleSince ??= DateTimeOffset.UtcNow;
                                if ((DateTimeOffset.UtcNow - idleSince.Value).TotalSeconds >= config.StopGraceSeconds)
                                {
                                    Log($"REC STOP (Anruf beendet) {active.AppName} -> {active.Dir}");
                                    await FinishRecording(active, config, currentCallFile, failedDir: null, logDir);
                                    active = null;
                                    idleSince = null;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"FEHLER im Poll-Tick: {ex.Message}");
                }

                if (debug) Log("[debug] Tick abgeschlossen.");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            // Regel 9: beim Beenden laufende Aufnahme sauber abschliessen.
            if (active != null)
            {
                Log($"REC STOP (callwatch gestoppt) {active.AppName} -> {active.Dir}");
                await FinishRecording(active, config, currentCallFile, failedDir: null, logDir);
            }

            return 0;
        }

        // ==================== Watch-Hilfstypen/-funktionen ====================

        private sealed class RunningRecording
        {
            public required string Dir { get; init; }
            public required string App { get; init; }
            public required string AppName { get; init; }
            public required DateTimeOffset Started { get; init; }
            public required MicCapture Mic { get; init; }
            public required ProcessLoopbackCapture System { get; init; }
            public required LevelWriter LevelWriter { get; init; }
        }

        private static async Task<RunningRecording?> TryStartRecording(
            int matchedPid, string exeName, CallTapConfig config, string recRoot, string currentCallFile)
        {
            string shortName = StripExeSuffix(exeName).ToLowerInvariant();
            string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
            string dir = Path.Combine(recRoot, $"{stamp}_{shortName}");
            Directory.CreateDirectory(dir);

            var levelWriter = new LevelWriter(dir);
            var mic = new MicCapture();
            var system = new ProcessLoopbackCapture();

            try
            {
                int rootPid = ResolveRootPid(exeName) ?? matchedPid;
                string sysPath = Path.Combine(dir, "system.wav");
                string micPath = Path.Combine(dir, "mic.wav");

                await system.StartAsync(rootPid, config.ExcludeTree, sysPath, levelWriter);
                mic.Start(micPath, config.MicDeviceId, levelWriter);

                var rec = new RunningRecording
                {
                    Dir = dir,
                    App = exeName,
                    AppName = shortName,
                    Started = DateTimeOffset.UtcNow,
                    Mic = mic,
                    System = system,
                    LevelWriter = levelWriter,
                };

                var payload = new
                {
                    dir = rec.Dir,
                    app = rec.App,
                    appName = rec.AppName,
                    start = rec.Started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                File.WriteAllText(currentCallFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

                Log($"REC START {exeName} -> {dir}");
                return rec;
            }
            catch (Exception ex)
            {
                Log($"FEHLER beim Aufnahmestart fuer {exeName}: {ex.Message}");
                try { system.Stop(); } catch { }
                try { mic.Stop(); } catch { }
                system.Dispose();
                mic.Dispose();
                levelWriter.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { }
                return null;
            }
        }

        private static void StopRunningRecording(RunningRecording rec)
        {
            try { rec.Mic.Stop(); } catch { }
            try { rec.System.Stop(); } catch { }
            rec.Mic.Dispose();
            rec.System.Dispose();
            rec.LevelWriter.Dispose();
        }

        private static async Task FinishRecording(RunningRecording rec, CallTapConfig config, string currentCallFile, string? failedDir, string logDir)
        {
            double durationSec = (DateTimeOffset.UtcNow - rec.Started).TotalSeconds;
            StopRunningRecording(rec);
            TryDelete(currentCallFile);

            if (durationSec < config.MinSeconds)
            {
                Log($"Aufnahme verworfen (< {config.MinSeconds}s minSeconds), Dauer {durationSec:0}s.");
                try { Directory.Delete(rec.Dir, recursive: true); } catch { }
                return;
            }

            var meta = new
            {
                app = rec.App,
                appName = rec.AppName,
                start = rec.Started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                durationSec,
            };
            File.WriteAllText(Path.Combine(rec.Dir, "meta.json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

            if (string.IsNullOrWhiteSpace(config.PostScript))
            {
                Log("Kein postScript konfiguriert — Aufnahme bleibt unverarbeitet liegen.");
                return;
            }

            try
            {
                Directory.CreateDirectory(logDir);
                var psi = new ProcessStartInfo
                {
                    FileName = config.PostScript,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                psi.ArgumentList.Add(rec.Dir);

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    string logPath = Path.Combine(logDir, "process.log");
                    _ = Task.Run(async () =>
                    {
                        using var outWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
                        await outWriter.WriteLineAsync($"--- {DateTimeOffset.Now:O} Verarbeitung gestartet fuer {rec.Dir} ---");
                        string stdout = await proc.StandardOutput.ReadToEndAsync();
                        string stderr = await proc.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrEmpty(stdout)) await outWriter.WriteAsync(stdout);
                        if (!string.IsNullOrEmpty(stderr)) await outWriter.WriteAsync(stderr);
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"FEHLER beim Start der Post-Processing-Pipeline: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private static int? ResolveRootPid(string exeName)
        {
            try
            {
                var procs = Process.GetProcessesByName(StripExeSuffix(exeName));
                return procs.Length > 0 ? procs[0].Id : null;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeExe(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        private static string StripExeSuffix(string name) => NormalizeExe(name);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void Log(string message) =>
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");

        // ==================== Config / Pfade ====================

        private static string DefaultConfigPath()
        {
            string? envOverride = Environment.GetEnvironmentVariable("CALLNOTES_CONFIG");
            if (!string.IsNullOrEmpty(envOverride)) return Environment.ExpandEnvironmentVariables(envOverride);

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "callnotes", "config.json");
        }

        private static string DefaultDataRoot()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "CallNotes");
        }

        private static CallTapConfig LoadConfig(string path)
        {
            if (!File.Exists(path)) return new CallTapConfig();

            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            return JsonSerializer.Deserialize<CallTapConfig>(json, options) ?? new CallTapConfig();
        }

        private static string? GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    /// <summary>
    /// Nur die fuer den Watch-Loop/CLI relevanten config.json-Felder (Contract 3.1) —
    /// Whisper/Diarize/Summarizer/Destinations werden von pipeline/config.py (Python)
    /// gelesen und hier bewusst nicht dupliziert.
    /// </summary>
    internal sealed class CallTapConfig
    {
        [JsonPropertyName("apps")]
        public List<string> Apps { get; set; } = new();

        [JsonPropertyName("minSeconds")]
        public int MinSeconds { get; set; } = 20;

        [JsonPropertyName("stopGraceSeconds")]
        public int StopGraceSeconds { get; set; } = 6;

        [JsonPropertyName("maxHours")]
        public double MaxHours { get; set; } = 4;

        [JsonPropertyName("tapScope")]
        public string TapScope { get; set; } = "app";

        [JsonPropertyName("outDir")]
        public string? OutDir { get; set; }

        [JsonPropertyName("postScript")]
        public string? PostScript { get; set; }

        [JsonPropertyName("micDeviceId")]
        public string? MicDeviceId { get; set; }

        [JsonPropertyName("processLoopbackMode")]
        public string ProcessLoopbackMode { get; set; } = "includeTree";

        public bool ExcludeTree => string.Equals(ProcessLoopbackMode, "excludeTree", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Prueft, ob <paramref name="exeName"/> (z.B. "Zoom.exe" oder "Zoom") gegen einen
        /// Allowlist-Eintrag passt. Regeln (Contract 4.1): Gross/Klein egal, ".exe" optional,
        /// "*" matcht alles, "Foo*" matcht als Praefix, sonst exakter Vergleich.
        /// </summary>
        public bool Matches(string exeName)
        {
            string candidate = Normalize(exeName);
            foreach (var raw in Apps)
            {
                string pattern = Normalize(raw);
                if (pattern == "*") return true;
                if (pattern.EndsWith('*'))
                {
                    if (candidate.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(candidate, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Normalize(string name)
        {
            string n = name.Trim();
            return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
        }
    }

    /// <summary>
    /// "Welcher Prozess hat gerade das Mikrofon aktiv" — Windows-Analogon zu macOS
    /// procIsRunningInput/processObjects() ueber Core Audio (Contract Abschnitt 4.3).
    /// Vollstaendig durch NAudio.CoreAudioApi abgedeckt, kein rohes COM-Interop noetig.
    /// </summary>
    internal static class AudioSessionProbe
    {
        public sealed record ActiveMicSession(int ProcessId, string ProcessName, string DisplayName);

        public static IReadOnlyList<ActiveMicSession> GetActiveMicSessions()
        {
            var byPid = new Dictionary<int, ActiveMicSession>();
            CollectFrom(Role.Communications, byPid);
            CollectFrom(Role.Multimedia, byPid);
            return byPid.Values.ToList();
        }

        private static void CollectFrom(Role role, Dictionary<int, ActiveMicSession> byPid)
        {
            MMDevice? device = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);

                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                    uint pid = session.GetProcessID;
                    if (pid == 0 || byPid.ContainsKey((int)pid)) continue;

                    string name = "";
                    try { name = Process.GetProcessById((int)pid).ProcessName; }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }

                    if (string.IsNullOrEmpty(name)) continue;
                    byPid[(int)pid] = new ActiveMicSession((int)pid, name, session.DisplayName ?? name);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Kein Standard-Capture-Geraet fuer diese Rolle (z.B. CI ohne Audio-Hardware).
            }
            finally
            {
                device?.Dispose();
            }
        }

        public static bool HasWorkingCaptureDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return device != null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
    }
}
