namespace ChessArmy.Core.Pvp;

/// <summary>
/// Emplacement de la save multi selon la variante de build. La progression multi du PLAYTEST (et des builds
/// Debug du dev) ne doit JAMAIS passer dans le jeu complet : le jeu complet lit un autre fichier
/// (<c>pvpfull_&lt;steamid&gt;.dat</c>) ET signe avec une autre clé (propriétaire <c>&lt;steamid&gt;:full</c>), si bien
/// qu'un fichier playtest renommé est refusé comme retouché. Aucune migration. La variante est choisie à la
/// COMPILATION par le jeu (symboles PLAYTEST / DEBUG), jamais à l'exécution.
/// </summary>
public static class PvpSaveSlot
{
    /// <summary>Nom du fichier de save multi (playtest/Debug : inchangé depuis la première version).</summary>
    public static string FileName(string steamId, bool fullGame) =>
        fullGame ? $"pvpfull_{steamId}.dat" : $"pvp_{steamId}.dat";

    /// <summary>Propriétaire passé à <see cref="PvpProfile.Encode"/> / <see cref="PvpProfile.Decode"/> (dérive la clé).</summary>
    public static string OwnerKey(string steamId, bool fullGame) =>
        fullGame ? steamId + ":full" : steamId;
}
