using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CallNotesTray;

/// <summary>
/// Statische Lokalisierung nach dem gleichen Muster wie im Mac-Original
/// (SettingsApp.swift: <c>func L(_ de: String, _ en: String) -> String</c>).
/// Quelle der Wahrheit ist "uiLanguage" in config.json ("system" | "de" | "en"),
/// mit Fallback auf die Systemsprache wenn "system" oder der Key fehlt/ungueltig ist.
/// </summary>
public static class L10n
{
    /// <summary>true = Deutsch, false = Englisch. Wird bei Refresh()/Init() neu ermittelt.</summary>
    public static bool IsGerman { get; private set; } = DetectSystemGerman();

    /// <summary>
    /// Liest "uiLanguage" aus der config.json (falls vorhanden) und aktualisiert
    /// <see cref="IsGerman"/> entsprechend. Wird beim App-Start und nach jedem
    /// Speichern der Settings aufgerufen (SettingsView -> applyLanguageChange()
    /// im Mac-Original).
    /// </summary>
    public static void Refresh(string? configPath = null)
    {
        IsGerman = ResolveIsGerman(ReadUiLanguage(configPath));
    }

    /// <summary>Liefert de bei Deutsch, sonst en — 1:1 die Signatur des Mac-Originals.</summary>
    public static string L(string de, string en) => IsGerman ? de : en;

    private static bool ResolveIsGerman(string? uiLanguage)
    {
        return uiLanguage switch
        {
            "de" => true,
            "en" => false,
            _ => DetectSystemGerman(), // "system" oder unbekannt/fehlend
        };
    }

    private static string? ReadUiLanguage(string? configPath)
    {
        try
        {
            string path = configPath ?? DefaultConfigPath();
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("uiLanguage", out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        catch
        {
            // config.json fehlt/ist kaputt -> stiller Fallback auf Systemsprache,
            // gleiche "warn, don't crash"-Haltung wie im Rest des Projekts.
        }
        return null;
    }

    private static bool DetectSystemGerman()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            return culture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Standard-Konfigurationspfad laut Contract Abschnitt 3:
    /// %APPDATA%\callnotes\config.json, ueberschreibbar per CALLNOTES_CONFIG.
    /// Lokal dupliziert (statt CallTap.Core.Config.Paths zu referenzieren), da
    /// L10n bewusst ohne Abhaengigkeit auf andere Projektteile lauffaehig bleibt.
    /// </summary>
    private static string DefaultConfigPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("CALLNOTES_CONFIG");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "callnotes", "config.json");
    }
}
