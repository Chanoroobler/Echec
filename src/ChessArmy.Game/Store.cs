using System.Diagnostics;

namespace ChessArmy.Game;

/// <summary>
/// Lien boutique (page Steam de CHESS ARMY) et ouverture du navigateur. Sert la version DÉMO : bouton
/// « liste de souhaits » du menu principal et invitation en fin de démo. Échec silencieux : ouvrir un lien
/// ne doit jamais faire planter le jeu (navigateur absent, sandbox, etc.).
/// </summary>
public static class Store
{
    /// <summary>Page Steam de CHESS ARMY.</summary>
    public const string SteamUrl = "https://store.steampowered.com/app/4971900/Chess_Army/";

    /// <summary>Ouvre la page Steam dans le navigateur par défaut.</summary>
    public static void OpenWishlist()
    {
        try { Process.Start(new ProcessStartInfo(SteamUrl) { UseShellExecute = true }); }
        catch (System.Exception ex) { Debug.WriteLine($"Ouverture de la page Steam echouee : {ex.Message}"); }
    }
}
