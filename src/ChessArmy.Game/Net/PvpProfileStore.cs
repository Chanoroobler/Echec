using System;
using System.IO;
using ChessArmy.Core.Pvp;
using Steamworks;

namespace ChessArmy.Game.Net;

/// <summary>
/// Profil multi du joueur sur le disque : <c>%AppData%/Echec/save/pvp_&lt;steamid&gt;.dat</c>, chiffré et signé
/// (cf. <see cref="PvpProfile.Encode"/>), lié au compte Steam. Un fichier retouché ou copié d'un autre compte
/// est mis de côté (<c>.rejete</c>) et remplacé par un profil neuf. Rien n'est enregistré si une valeur a été
/// trafiquée en mémoire (<see cref="PvpProfile.Tampered"/>).
/// </summary>
public static class PvpProfileStore
{
    private static PvpProfile? s_profile;

    /// <summary>Vrai si le fichier trouvé au chargement était illisible ou retouché (profil remis à zéro).</summary>
    public static bool WasReset { get; private set; }

    private static string OwnerId =>
        SteamService.IsActive ? SteamUser.GetSteamID().m_SteamID.ToString() : "local";

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echec", "save");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"pvp_{OwnerId}.dat");
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

    /// <summary>Enregistre le profil (écriture atomique). Refusé si une valeur a été trafiquée en mémoire.</summary>
    public static void Save()
    {
        if (s_profile == null || PvpProfile.Tampered)
            return;
        var path = FilePath;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, s_profile.Encode(OwnerId));
        File.Move(temp, path, overwrite: true);
    }
}
