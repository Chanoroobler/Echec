using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Campaign;

/// <summary>
/// Gabarit d'unité (domaine + classe) utilisé par la campagne : inventaire du joueur,
/// options de recrutement et vagues ennemies. <see cref="Essential"/> marque le
/// commandant (joueur) ou le boss (ennemi) ; leur mort décide la partie.
/// Un même gabarit produit une <see cref="Unit"/> neuve (PV pleins) à chaque combat.
/// Les <see cref="Equipments"/> sont « collés au pion » : ils suivent ce gabarit d'un combat à l'autre et
/// disparaissent avec lui (permadeath). Gérés par <see cref="Run.Equip"/> / <see cref="Run.Unequip"/>.
/// </summary>
public sealed class UnitSpec
{
    public UnitSpec(Domaine domaine, UnitClass unitClass, bool essential = false, bool companion = false)
    {
        Domaine = domaine;
        UnitClass = unitClass;
        Essential = essential;
        CommanderBody = essential && !companion;
        Companion = companion;
    }

    private readonly List<Equipment> _equipment = new();

    public Domaine Domaine { get; }

    /// <summary>
    /// Classe du pion. Mutable par une ÉVOLUTION D'ARBRE (cf. <see cref="EvolveTo"/>), à la différence d'une
    /// fusion qui, elle, fabrique un gabarit neuf.
    /// </summary>
    public UnitClass UnitClass { get; private set; }

    /// <summary>Sa mort DÉCIDE la partie (commandant, second meneur, ou pion promu par « Révolte »).</summary>
    public bool Essential { get; private set; }

    /// <summary>
    /// SECOND meneur d'un commandant DUO (cf. <see cref="Battle.CommandeDef.CompanionId"/>) : essentiel comme
    /// le commandant, mais distingué de lui pour que l'arbre puisse viser l'un OU l'autre (effets
    /// <c>companionStat</c> / <c>companionTrait</c>). Toujours <c>false</c> pour un commandant solo.
    /// </summary>
    public bool Companion { get; }

    /// <summary>
    /// CORPS DU COMMANDANT : le pion que visent les effets d'arbre <c>commanderStat</c> / <c>commanderTrait</c>.
    /// Confondu avec <see cref="Essential"/> dans le cas général, il s'en sépare avec « Révolte » (arbre de la
    /// BRUTE) : le Paysan promu devient essentiel (sa mort perd la run) tandis que la Brute, redevenue un pion
    /// ordinaire et mortelle comme les autres, GARDE tous les bonus de sa branche.
    /// </summary>
    public bool CommanderBody { get; private set; }

    /// <summary>
    /// Nombre de missions comptabilisées par le trait « Survivant » (+1 puissance et +2 PV max chacune,
    /// appliqués au spawn par <see cref="Run.BuffsFor"/>). Persisté ; remis à zéro quand le pion tombe.
    /// </summary>
    public int SurvivantStacks { get; set; }

    /// <summary>
    /// Fait ÉVOLUER le pion en place (nœud d'arbre à choix) : il garde son identité, donc ses équipements
    /// collés, ses mises à mort et ses paliers de « Survivant » — au contraire d'une fusion, qui sort un pion
    /// neuf. Sans effet si la classe visée est nulle.
    /// </summary>
    public void EvolveTo(UnitClass? unitClass)
    {
        if (unitClass != null)
            UnitClass = unitClass;
    }

    /// <summary>
    /// Change le rôle du pion dans la run : <paramref name="essential"/> = sa mort décide la partie,
    /// <paramref name="commanderBody"/> = il porte les bonus « commandant » de l'arbre. Réservé à « Révolte »
    /// (cf. <see cref="Run.Revolte"/>) et à la reconstruction d'une sauvegarde.
    /// </summary>
    public void SetRole(bool essential, bool commanderBody)
    {
        Essential = essential;
        CommanderBody = commanderBody;
    }

    /// <summary>
    /// Équipements portés, dans l'ordre où ils ont été posés. Le NOMBRE DE SLOTS n'est pas porté ici mais par
    /// la run (cf. <see cref="Run.SlotsFor"/>) : il dépend de l'arbre de commandement, et le commandant en a
    /// 0 tant qu'un nœud ne lui en donne pas. Voir <see cref="Run.Equip"/> / <see cref="Run.Unequip"/>.
    /// </summary>
    public IReadOnlyList<Equipment> Equipments => _equipment;

    /// <summary>Vrai si le pion porte au moins un équipement.</summary>
    public bool HasEquipment => _equipment.Count > 0;

    /// <summary>Pose un équipement (aucun contrôle de slot : c'est <see cref="Run.Equip"/> qui décide).</summary>
    public void AddEquipment(Equipment equipment) => _equipment.Add(equipment);

    /// <summary>Retire un exemplaire précis (faux s'il ne le portait pas).</summary>
    public bool RemoveEquipment(Equipment equipment) => _equipment.Remove(equipment);

    /// <summary>Retire TOUS les équipements portés et les renvoie (l'appelant décide de leur sort).</summary>
    public IReadOnlyList<Equipment> TakeAllEquipment()
    {
        var taken = _equipment.ToList();
        _equipment.Clear();
        return taken;
    }

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
        new(Domaine, faction, UnitClass, _equipment, buffs, Kills)
        {
            IsEssential = Essential,
            IsCompanion = Companion,
        };
}
