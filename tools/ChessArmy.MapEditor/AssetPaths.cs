using System;
using System.IO;

namespace ChessArmy.MapEditor;

/// <summary>
/// Localise les dossiers d'assets du jeu à partir de l'emplacement de l'exécutable :
/// on remonte les dossiers jusqu'à trouver <c>ChessArmy.sln</c> (racine du dépôt), puis on
/// pointe sur <c>src/ChessArmy.Game/Assets/…</c>. Ainsi l'éditeur écrit dans les MÊMES
/// fichiers que le jeu, sans chemin codé en dur.
/// </summary>
internal static class AssetPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string AssetsDir  => Path.Combine(RepoRoot, "src", "ChessArmy.Game", "Assets");
    public static string TilesJson  => Path.Combine(AssetsDir, "Tiles", "tiles.json");
    public static string TilesetsDir => Path.Combine(AssetsDir, "Tilesets");
    public static string MapsDir     => Path.Combine(AssetsDir, "Maps");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChessArmy.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Repli : dossier courant (permet au moins d'ouvrir des fichiers à la main).
        return Directory.GetCurrentDirectory();
    }
}
