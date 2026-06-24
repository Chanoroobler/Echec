using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Echec.Core.Campaign;
using Echec.Engine.Settings;

namespace Echec.Engine.Persistence;

/// <summary>
/// Persistance disque des réglages et de la progression, sous <c>%AppData%\Echec</c>.
///  • Réglages : un unique <c>options.json</c> (réécrit à chaque modification).
///  • Progression : <see cref="SlotCount"/> slots indépendants (<c>save-slot-N.json</c>), chacun
///    une <see cref="RunSave"/> auto-sauvegardée en phase de placement.
/// Toutes les E/S sont tolérantes aux pannes : un fichier manquant ou corrompu retombe sur les
/// valeurs par défaut / un slot vide plutôt que de faire planter le jeu.
/// </summary>
public sealed class SaveService
{
    public const int SlotCount = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;

    public SaveService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _dir = Path.Combine(appData, "Echec");
        TryEnsureDir();
    }

    // ── Réglages ──────────────────────────────────────────────────────────────────

    /// <summary>Charge <c>options.json</c> dans <paramref name="settings"/> (sans effet si absent/invalide).</summary>
    public void LoadInto(GameSettings settings)
    {
        if (TryRead<SettingsDto>(OptionsPath) is { } dto)
            dto.ApplyTo(settings);
    }

    /// <summary>Écrit l'état courant des réglages sur disque.</summary>
    public void SaveSettings(GameSettings settings) => TryWrite(OptionsPath, SettingsDto.From(settings));

    // ── Profil global ───────────────────────────────────────────────────────────────

    /// <summary>Vrai si le joueur a déjà démarré une campagne (sinon : sa première = tutorielle).</summary>
    public bool HasPlayedBefore() => TryRead<ProfileDto>(ProfilePath)?.HasPlayedBefore ?? false;

    /// <summary>Marque la première campagne comme entamée (idempotent).</summary>
    public void MarkPlayed()
    {
        var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
        if (dto.HasPlayedBefore)
            return;
        dto.HasPlayedBefore = true;
        TryWrite(ProfilePath, dto);
    }

    // ── Slots de progression ───────────────────────────────────────────────────────

    public bool SlotExists(int index) => File.Exists(SlotPath(index));

    /// <summary>Lit le slot, ou null s'il est vide / illisible.</summary>
    public RunSave? LoadSlot(int index) => TryRead<RunSave>(SlotPath(index));

    public void SaveSlot(int index, RunSave save) => TryWrite(SlotPath(index), save);

    public void DeleteSlot(int index)
    {
        try
        {
            var path = SlotPath(index);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Suppression du slot {index} ignorée : {ex.Message}");
        }
    }

    // ── E/S internes ────────────────────────────────────────────────────────────────

    private string OptionsPath => Path.Combine(_dir, "options.json");
    private string ProfilePath => Path.Combine(_dir, "profile.json");
    private string SlotPath(int index) => Path.Combine(_dir, $"save-slot-{index + 1}.json");

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Lecture de {path} ignorée : {ex.Message}");
            return null;
        }
    }

    private void TryWrite<T>(string path, T value)
    {
        try
        {
            TryEnsureDir();
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Écriture de {path} ignorée : {ex.Message}");
        }
    }

    private void TryEnsureDir()
    {
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Création de {_dir} ignorée : {ex.Message}"); }
    }
}
