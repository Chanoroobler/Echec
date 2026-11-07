using System;
using System.IO;
using ChessArmy.Core.Pvp;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Profil multi du joueur sur le disque : <c>%AppData%/Echec/save/pvp_&lt;steamid&gt;.dat</c> (PLAYTEST et Debug) ou
/// <c>pvpfull_&lt;steamid&gt;.dat</c> (jeu complet, autre clé : cf. <see cref="PvpSaveSlot"/>), chiffré et signé
/// (cf. <see cref="PvpProfile.Encode"/>), lié au compte Steam. Un fichier retouché ou copié d'un autre compte
/// est mis de côté (<c>.rejete</c>) et remplacé par un profil neuf. Rien n'est enregistré si une valeur a été
/// trafiquée en mémoire (<see cref="PvpProfile.Tampered"/>).
/// </summary>
public static class PvpProfileStore
{
    private static PvpProfile? s_profile;

    /// <summary>Vrai si le fichier trouvé au chargement était illisible ou retouché (profil remis à zéro).</summary>
    public static bool WasReset { get; private set; }

    // Save multi SÉPARÉE du jeu complet, figée à la compilation (cf. PvpSaveSlot) : les playtesteurs repartent de
    // zéro à la sortie du multi dans le jeu complet. PLAYTEST et Debug gardent le fichier historique pvp_<id>.dat.
#if PLAYTEST || DEBUG
    private const bool FullGameSave = false;
#else
    private const bool FullGameSave = true;
#endif

    private static string SteamId =>
        SteamService.IsActive ? SteamUser.GetSteamID().m_SteamID.ToString() : "local";

    private static string OwnerId => PvpSaveSlot.OwnerKey(SteamId, FullGameSave);

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echec", "save");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, PvpSaveSlot.FileName(SteamId, FullGameSave));
        }
    }

    /// <summary>Profil courant (chargé au premier accès, créé s'il n'existe pas).</summary>
    public static PvpProfile Current => s_profile ??= Load();

    private static PvpProfile Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return PvpProfile.CreateNew();
        try
        {
            if (PvpProfile.Decode(File.ReadAllBytes(path), OwnerId) is { } profile)
                return profile;
            File.Move(path, path + ".rejete", overwrite: true);   // retouché ou d'un autre compte : gardé pour enquête
        }
        catch (IOException)
        {
        }
        WasReset = true;
        return PvpProfile.CreateNew();
    }

#if DEBUG
    /// <summary>Tests du dev (F9 sur l'écran multi) : supprime la sauvegarde multi et repart d'un profil neuf
    /// (armée de départ, points et cote de départ).</summary>
    internal static void ResetToDefault()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
        s_profile = PvpProfile.CreateNew();
        WasReset = false;
    }

    private static bool s_memoryOnly;

    /// <summary>Captures d'écran de développement : profil en mémoire seulement (le vrai fichier n'est ni lu ni écrit).</summary>
    internal static void UseInMemory(PvpProfile profile)
    {
        s_profile = profile;
        s_memoryOnly = true;
    }
#endif

    /// <summary>Enregistre le profil (écriture atomique). Refusé si une valeur a été trafiquée en mémoire.</summary>
    public static void Save()
    {
        if (s_profile == null || PvpProfile.Tampered)
            return;
#if DEBUG
        if (s_memoryOnly)
            return;
#endif
        var path = FilePath;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, s_profile.Encode(OwnerId));
        File.Move(temp, path, overwrite: true);
    }
}
