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
/// Persistance disque des réglages et de la progression, sous <c>%AppData%\Echec</c>. Les deux sont
/// dans des dossiers SÉPARÉS pour que la progression seule puisse être synchronisée sur le cloud
/// (les réglages, eux, décrivent la machine : résolution, mode d'affichage, volumes) :
///  • Réglages, à la racine : un unique <c>options.json</c> (réécrit à chaque modification).
///  • Progression, dans <c>save\</c> : le profil global (<c>profile.json</c>) et <see cref="SlotCount"/>
///    slots indépendants (<c>save-slot-N.json</c>), chacun une <see cref="RunSave"/> auto-sauvegardée
///    en phase de placement.
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

    // Racine (réglages, propres à la machine) et sous-dossier de progression (candidat au cloud).
    private readonly string _dir;
    private readonly string _saveDir;

    // Sérialise les écritures de slot en arrière-plan (SaveSlotAsync) pour qu'elles ne se chevauchent pas.
    private readonly object _ioLock = new();

    public SaveService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _dir = Path.Combine(appData, "Echec");
        _saveDir = Path.Combine(_dir, "save");
        TryEnsureDir(_dir);
        TryEnsureDir(_saveDir);
        MigrateLegacyLayout();
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
    /// Marque une unité comme découverte. Idempotent. Renvoie <c>true</c> si c'était une NOUVEAUTÉ (utile au
    /// récap de fin de run). Le set mémoire est mis à jour SYNCHRONE (IsUnitDiscovered est correct dès le
    /// retour) ; la persistance disque (lecture-modification-écriture de profile.json, sous verrou pour
    /// préserver les autres champs) part en arrière-plan pour ne pas figer la frame appelante.
    /// </summary>
    public bool DiscoverUnit(string asset)
    {
        if (!DiscoveredSet().Add(asset))
            return false;
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
        return true;
    }

    // Cache mémoire des équipements découverts (même logique que les unités).
    private HashSet<string>? _discoveredEquip;

    private HashSet<string> DiscoveredEquipSet() =>
        _discoveredEquip ??= new HashSet<string>(TryRead<ProfileDto>(ProfilePath)?.DiscoveredEquipment ?? new List<string>());

    /// <summary>Vrai si le joueur a déjà obtenu l'équipement d'id <paramref name="id"/> (toutes parties).</summary>
    public bool IsEquipmentDiscovered(string id) => DiscoveredEquipSet().Contains(id);

    /// <summary>
    /// Marque un équipement comme découvert. Idempotent. Renvoie <c>true</c> si c'était une NOUVEAUTÉ. Set
    /// mémoire mis à jour SYNCHRONE ; persistance disque (lecture-modification-écriture sous verrou pour
    /// préserver les autres champs) en arrière-plan.
    /// </summary>
    public bool DiscoverEquipment(string id)
    {
        if (!DiscoveredEquipSet().Add(id))
            return false;
        var snapshot = new List<string>(DiscoveredEquipSet());
        Task.Run(() =>
        {
            lock (_ioLock)
            {
                var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
                dto.DiscoveredEquipment = snapshot;
                TryWrite(ProfilePath, dto);
            }
        });
        return true;
    }

    // Cache mémoire des commandants débloqués (même logique que les unités).
    private HashSet<string>? _unlockedCommanders;

    private HashSet<string> UnlockedCommanderSet() =>
        _unlockedCommanders ??= new HashSet<string>(TryRead<ProfileDto>(ProfilePath)?.UnlockedCommanders ?? new List<string>());

    /// <summary>Ids des commandants débloqués (méta-progression), à injecter dans une <see cref="Run"/>.</summary>
    public IReadOnlySet<string> UnlockedCommanders() => UnlockedCommanderSet();

    /// <summary>Vrai si le commandant d'id <paramref name="id"/> a été débloqué (toutes parties confondues).</summary>
    public bool IsCommanderUnlocked(string id) => UnlockedCommanderSet().Contains(id);

    /// <summary>
    /// Débloque un commandant (le rend jouable dans le carrousel). Idempotent. Renvoie <c>true</c> si c'était
    /// une NOUVEAUTÉ. Set mémoire mis à jour SYNCHRONE ; persistance disque (lecture-modification-écriture sous
    /// verrou pour préserver les autres champs) en arrière-plan.
    /// </summary>
    public bool UnlockCommander(string id)
    {
        if (string.IsNullOrEmpty(id) || !UnlockedCommanderSet().Add(id))
            return false;
        var snapshot = new List<string>(UnlockedCommanderSet());
        Task.Run(() =>
        {
            lock (_ioLock)
            {
                var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
                dto.UnlockedCommanders = snapshot;
                TryWrite(ProfilePath, dto);
            }
        });
        return true;
    }

    /// <summary>Efface toute la méta-progression (unités, équipements découverts ET commandants débloqués). Garde le reste du profil.</summary>
    public void ResetMetaProgression()
    {
        _discovered = new HashSet<string>();
        _discoveredEquip = new HashSet<string>();
        _unlockedCommanders = new HashSet<string>();
        var dto = TryRead<ProfileDto>(ProfilePath) ?? new ProfileDto();
        dto.DiscoveredUnits = new List<string>();
        dto.DiscoveredEquipment = new List<string>();
        dto.UnlockedCommanders = new List<string>();
        TryWrite(ProfilePath, dto);
    }

    // ── Slots de progression ───────────────────────────────────────────────────────

    public bool SlotExists(int index) => File.Exists(SlotPath(index));

    /// <summary>Lit le slot, ou null s'il est vide / illisible / hors échelle (cf. <see cref="RunSave.IsUsable"/>).</summary>
    public RunSave? LoadSlot(int index) =>
        TryRead<RunSave>(SlotPath(index)) is { IsUsable: true } save ? save : null;

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
    private string ProfilePath => Path.Combine(_saveDir, "profile.json");
    private string SlotPath(int index) => Path.Combine(_saveDir, $"save-slot-{index + 1}.json");

    /// <summary>
    /// Récupère la progression des versions d'avant la séparation, qui écrivaient tout à plat à la
    /// racine, en la déplaçant vers <c>save\</c>. Un fichier déjà présent dans la nouvelle
    /// arborescence fait autorité : l'ancien est alors laissé tel quel plutôt qu'écrasé.
    /// </summary>
    private void MigrateLegacyLayout()
    {
        TryMove(Path.Combine(_dir, "profile.json"), ProfilePath);
        for (var i = 0; i < SlotCount; i++)
            TryMove(Path.Combine(_dir, $"save-slot-{i + 1}.json"), SlotPath(i));
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
                File.Move(from, to);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Migration de {from} ignorée : {ex.Message}");
        }
    }

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

    private static void TryWrite<T>(string path, T value)
    {
        try
        {
            TryEnsureDir(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Écriture de {path} ignorée : {ex.Message}");
        }
    }

    private static void TryEnsureDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Création de {dir} ignorée : {ex.Message}"); }
    }
}
