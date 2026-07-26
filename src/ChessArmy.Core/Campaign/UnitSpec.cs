using ChessArmy.Core.Battle;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Campaign;

/// <summary>
/// Gabarit d'unité (domaine + classe) utilisé par la campagne : inventaire du joueur,
/// options de recrutement et vagues ennemies. <see cref="Essential"/> marque le
/// commandant (joueur) ou le boss (ennemi) ; leur mort décide la partie.
/// Un même gabarit produit une <see cref="Unit"/> neuve (PV pleins) à chaque combat.
/// L'<see cref="Equipment"/> est « collé au pion » : il suit ce gabarit d'un combat à l'autre et
/// disparaît avec lui (permadeath). Géré par <see cref="Run.Equip"/> / <see cref="Run.Unequip"/>.
/// </summary>
public sealed class UnitSpec
{
    public UnitSpec(Domaine domaine, UnitClass unitClass, bool essential = false)
    {
        Domaine = domaine;
        UnitClass = unitClass;
        Essential = essential;
    }

    public Domaine Domaine { get; }
    public UnitClass UnitClass { get; }
    public bool Essential { get; }

    /// <summary>Équipement porté (un seul, jamais sur le commandant), ou null. Voir <see cref="Run.Equip"/>.</summary>
    public Equipment? Equipment { get; set; }

    /// <summary>
    /// Total d'ennemis tués À VIE par ce pion (persistant, sauvegardé). Recopié sur l'<see cref="Unit"/>
    /// spawnée puis remis à jour à la fin d'un combat gagné (survivants seulement). Une évolution issue
    /// d'une fusion repart de 0 (le pion sort neuf). Voir <see cref="Unit.Kills"/>.
    /// </summary>
    public int Kills { get; set; }

    public string Name => UnitClass.Name;

    /// <summary>
    /// Instancie une unité neuve (PV au maximum) pour ce camp, équipement inclus. <paramref name="buffs"/> =
    /// bonus de l'arbre de commandement à appliquer (cf. <see cref="Run.BuffsFor"/>) ; null / omis pour un
    /// ennemi ou un spawn hors campagne.
    /// </summary>
    public Unit Spawn(Faction faction, CommandBuffs? buffs = null) =>
        new(Domaine, faction, UnitClass, Equipment, buffs, Kills) { IsEssential = Essential };
}
