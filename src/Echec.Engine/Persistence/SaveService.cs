using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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

    // Sérialise les écritures de slot en arrière-plan (SaveSlotAsync) pour qu'elles ne se chevauchent pas.
    private readonly object _ioLock = new();

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

    // ── Méta-progression : unités découvertes ────────────────────────────────────────

    // Cache mémoire : évite de relire le profil sur disque à chaque frame (le rendu interroge souvent).
    private HashSet<string>? _discovered;

    private HashSet<string> DiscoveredSet() =>
        _discovered ??= new HashSet<string>(TryRead<ProfileDto>(ProfilePath)?.DiscoveredUnits ?? new List<string>());

    /// <summary>Vrai si le joueur a déjà obtenu l'unité d'asset <paramref name="asset"/> (toutes parties).</summary>
    public bool IsUnitDiscovered(string asset) => DiscoveredSet().Contains(asset);

    /// <summary>
    /// Marque une unité comme découverte. Idempotent. Le set mémoire est mis à jour SYNCHRONE (IsUnitDiscovered
    /// est correct dès le retour) ; la persistance disque (lecture-modification-écriture de profile.json, sous
    /// verrou pour préserver les autres champs) part en arrière-plan pour ne pas figer la frame appelante.
    /// </summary>
    public void DiscoverUnit(string asset)
    {
        if (!DiscoveredSet().Add(asset))
            return;
        var snapshot = new List<string>(DiscoveredSet());
        Task.Run(() =>
        {
            lock (_ioLock)
            {
                var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
                dto.DiscoveredUnits = snapshot;
                TryWrite(ProfilePath, dto);
            }
        });
    }

    /// <summary>Efface toute la méta-progression (unités découvertes). Garde le reste du profil.</summary>
    public void ResetMetaProgression()
    {
        _discovered = new HashSet<string>();
        var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
        dto.DiscoveredUnits = new List<string>();
        TryWrite(ProfilePath, dto);
    }

    // ── Slots de progression ───────────────────────────────────────────────────────

    public bool SlotExists(int index) => File.Exists(SlotPath(index));

    /// <summary>Lit le slot, ou null s'il est vide / illisible.</summary>
    public RunSave? LoadSlot(int index) => TryRead<RunSave>(SlotPath(index));

    public void SaveSlot(int index, RunSave save) => TryWrite(SlotPath(index), save);

    /// <summary>
    /// Sauvegarde un slot SANS bloquer l'appelant : la sérialisation JSON + l'écriture disque partent sur
    /// un thread d'arrière-plan (évite un hitch sur la frame appelante, ex. l'entrée en placement).
    /// <paramref name="save"/> doit être un INSTANTANÉ détaché (cf. <see cref="RunSave.From"/>), jamais muté
    /// après l'appel. Les écritures sont sérialisées par un verrou pour ne pas se chevaucher. Tolérant aux
    /// pannes comme <see cref="SaveSlot"/> (toute erreur est avalée).
    /// </summary>
    public void SaveSlotAsync(int index, RunSave save)
    {
        var path = SlotPath(index);
        Task.Run(() =>
        {
            lock (_ioLock)
                TryWrite(path, save);
        });
    }

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
