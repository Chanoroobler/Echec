using System;
using System.IO;

namespace ChessArmy.LocEditor;

/// <summary>
/// Localise le fichier de traduction dans les SOURCES du jeu : on remonte les dossiers depuis
/// l'exécutable jusqu'à <c>ChessArmy.sln</c>, comme les autres outils (MapEditor / SaveEditor).
/// C'est bien le fichier SOURCE qu'on édite (git) ; le build du jeu le recopie ensuite dans sa sortie.
/// </summary>
internal static class GamePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string StringsCsv =>
        Path.Combine(RepoRoot, "src", "ChessArmy.Game", "Assets", "Config", "strings.csv");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChessArmy.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Repli : dossier courant (le fichier sera peut-être introuvable ; l'éditeur le signalera).
        return Directory.GetCurrentDirectory();
    }
}
