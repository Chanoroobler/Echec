using Echec.Core.Battle;

namespace Echec.Core.Campaign;

/// <summary>
/// Gabarit d'unité (domaine + classe) utilisé par la campagne : inventaire du joueur,
/// options de recrutement et vagues ennemies. <see cref="Essential"/> marque le
/// commandant (joueur) ou le boss (ennemi) ; leur mort décide la partie.
/// Un même gabarit produit une <see cref="Unit"/> neuve (PV pleins) à chaque combat.
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

    public string Name => UnitClass.Name;

    /// <summary>Instancie une unité neuve (PV au maximum) pour ce camp.</summary>
    public Unit Spawn(Faction faction) =>
        new(Domaine, faction, UnitClass) { IsEssential = Essential };
}
