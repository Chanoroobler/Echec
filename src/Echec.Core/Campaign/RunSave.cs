using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;

namespace Echec.Core.Campaign;

/// <summary>
/// Forme sérialisable d'une <see cref="Run"/> : numéro de combat + inventaire. Sauvegardée pendant
/// la phase de placement (entre les combats), jamais en plein combat. Volontairement minimale et
/// stable : chaque unité est décrite par son domaine, l'asset de sa classe et le drapeau essentiel,
/// ce qui permet de reconstruire la <see cref="UnitClass"/> exacte même si l'arbre de classes évolue.
/// </summary>
public sealed class RunSave
{
    /// <summary>Version du format (pour migrer/ignorer une sauvegarde incompatible plus tard).</summary>
    public int Version { get; set; } = 1;

    public int CombatNumber { get; set; } = 1;

    /// <summary>Graine de la run : rejoue EXACTEMENT la même vague/terrain au combat repris.</summary>
    public int Seed { get; set; }

    public List<UnitSpecSave> Roster { get; set; } = new();

    /// <summary>Nombre d'unités de l'inventaire (résumé léger pour l'écran de slots).</summary>
    public int UnitCount => Roster.Count;

    /// <summary>Capture l'état persistant d'une run en cours.</summary>
    public static RunSave From(Run run)
    {
        var save = new RunSave { CombatNumber = run.CombatNumber, Seed = run.Seed };
        foreach (var spec in run.Roster)
            save.Roster.Add(UnitSpecSave.From(spec));
        return save;
    }

    /// <summary>Reconstruit une run jouable à partir de la sauvegarde.</summary>
    public Run ToRun() => Run.Restore(Roster.Select(s => s.ToSpec()).ToList(), CombatNumber, Seed);
}

/// <summary>Forme sérialisable d'un <see cref="UnitSpec"/> (un emplacement d'inventaire).</summary>
public sealed class UnitSpecSave
{
    public Domaine Domaine { get; set; }

    /// <summary>Asset de la classe (identifiant stable dans l'arbre du domaine).</summary>
    public string Class { get; set; } = "";

    public bool Essential { get; set; }

    public static UnitSpecSave From(UnitSpec spec) => new()
    {
        Domaine = spec.Domaine,
        Class = spec.UnitClass.Asset,
        Essential = spec.Essential,
    };

    public UnitSpec ToSpec()
    {
        if (Essential)
        {
            // Unité COMMANDE (commandant) : retrouvée par asset dans le registre, repli sur le commandant.
            var def = Commandes.All.FirstOrDefault(c => c.BaseClass.Asset == Class) ?? Commandes.Commander;
            return new UnitSpec(def.Movement, def.BaseClass, essential: true);
        }

        // Classe quelconque de l'arbre du domaine (base ou évolution), repli sur la classe de base.
        var cls = FindClass(Domaines.Of(Domaine).BaseClass, Class) ?? Domaines.Of(Domaine).BaseClass;
        return new UnitSpec(Domaine, cls);
    }

    /// <summary>Recherche en profondeur la classe d'asset donné dans un arbre de classes.</summary>
    private static UnitClass? FindClass(UnitClass root, string asset)
    {
        if (root.Asset == asset)
            return root;
        foreach (var evolution in root.Evolutions)
            if (FindClass(evolution, asset) is { } found)
                return found;
        return null;
    }
}
