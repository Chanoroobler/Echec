using System;
using System.IO;

namespace Echec.SaveEditor;

/// <summary>
/// Les deux arborescences dont l'éditeur a besoin :
///  • les CATALOGUES du jeu (units/equipment/commander_trees), lus dans les sources — on remonte les
///    dossiers depuis l'exécutable jusqu'à <c>Echec.sln</c>, comme l'éditeur de maps ;
///  • les SAUVEGARDES, sous <c>%AppData%\Echec\save</c> — mêmes chemins que
///    <c>Echec.Engine.Persistence.SaveService</c>, qu'on ne peut pas réutiliser ici (il dépend de MonoGame).
/// Toute modification des chemins de SaveService doit être répercutée ici.
/// </summary>
internal static class GamePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ConfigDir => Path.Combine(RepoRoot, "src", "Echec.Game", "Assets", "Config");
    public static string UnitsJson => Path.Combine(ConfigDir, "units.json");
    public static string EquipmentJson => Path.Combine(ConfigDir, "equipment.json");
    public static string CommandTreesJson => Path.Combine(ConfigDir, "commander_trees.json");

    /// <summary>Nombre de slots du jeu (cf. <c>SaveService.SlotCount</c>).</summary>
    public const int SlotCount = 3;

    public static string SaveDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echec", "save");

    /// <summary>Chemin d'un slot, index 0-based (le fichier, lui, est numéroté à partir de 1).</summary>
    public static string SlotPath(int index) => Path.Combine(SaveDir, $"save-slot-{index + 1}.json");

    public static string ProfilePath => Path.Combine(SaveDir, "profile.json");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Echec.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Repli : dossier courant. Les catalogues seront introuvables et l'éditeur retombera sur les
        // valeurs codées d'Echec.Core, qui restent utilisables.
        return Directory.GetCurrentDirectory();
    }
}
