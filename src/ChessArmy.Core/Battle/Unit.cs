using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Battle;

/// <summary>
/// Unité jouable. Deux axes : le <see cref="Domaine"/> (qui fournit le style de
/// déplacement) et la <see cref="Class"/> (asset + stats : PV, dégâts, portée).
/// Le level-up n'est pas encore défini : l'unité reste sur la classe qu'on lui donne.
///
/// Trois sources de stats et de traits, cumulatives : la <see cref="Class"/>, un éventuel
/// <see cref="Equipment"/> (collé au pion) et les <see cref="Buffs"/> de l'arbre de commandement
/// (bonus d'armée du joueur, figés au placement — les ennemis n'en ont jamais).
/// </summary>
public sealed class Unit
{
    public Unit(Domaine domaine, Faction faction, UnitClass unitClass, Equipment? equipment = null,
        CommandBuffs? buffs = null, int kills = 0)
    {
        Domaine = domaine;
        Faction = faction;
        Class = unitClass;
        Equipment = equipment;
        Buffs = buffs ?? CommandBuffs.None;
        Kills = kills;   // total d'ennemis tués à VIE (repris du gabarit, cf. UnitSpec.Spawn)
        Hp = MaxHp;   // PV pleins, bonus d'équipement et d'arbre inclus
    }

    public Domaine Domaine { get; }
    public Faction Faction { get; }
    public UnitClass Class { get; }

    /// <summary>
    /// Domaine du pattern d'ATTAQUE (directions + glissé/sauté) : celui de la classe s'il diffère, sinon le
    /// domaine de DÉPLACEMENT. Cavalier monté (archer) : déplacement en L (<see cref="Domaine"/> Cavalier),
    /// mais tir en lignes (attaque = Dame). Voir <see cref="UnitClass.AttackDomaine"/> et <see cref="Match"/>.
    /// </summary>
    public Domaine AttackDomaine => Class.AttackDomaine ?? Domaine;

    /// <summary>Équipement porté (collé au pion), ou null. Stat ou trait, jamais sur le commandant. Peut être
    /// BRISÉ en combat (mis à null) par la « Queue de phénix » — cf. <see cref="ReviveConsumingEquipment"/>.</summary>
    public Equipment? Equipment { get; private set; }

    /// <summary>
    /// Bonus de l'arbre de commandement applicables à CETTE unité (ceux du commandant, ou ceux des troupes).
    /// <see cref="CommandBuffs.None"/> pour tout ennemi. Calculés au placement par <see cref="Campaign.Run"/>.
    /// </summary>
    public CommandBuffs Buffs { get; }

    public int Hp { get; private set; }

    /// <summary>
    /// Nombre d'ennemis TUÉS par cette unité, cumulé À VIE (persistant via <see cref="UnitSpec.Kills"/>).
    /// Amorcé au spawn avec le total du gabarit, puis incrémenté par <see cref="Match"/> à chaque mise à
    /// mort (attaque directe, éclaboussure, transpercement, foudre, interception, riposte). Recopié dans
    /// le gabarit à la fin d'un combat gagné pour les survivants (les morts partent avec — permadeath).
    /// </summary>
    public int Kills { get; private set; }

    /// <summary>Comptabilise une mise à mort infligée par cette unité. Appelé par le moteur de combat.</summary>
    public void RecordKill() => Kills++;

    /// <summary>
    /// Nombre de fois où cette unité a été TOUCHÉE (a réellement encaissé des dégâts) durant le combat
    /// courant. Amorcé à 0 au spawn et NON persisté : chaque combat repart d'une unité neuve. Incrémenté par
    /// <see cref="Match"/>. Sert à la source de points « sur coup reçu » d'un commandant (cf. <c>CommandeDef.OnHitPoints</c>).
    /// </summary>
    public int TimesHit { get; private set; }

    /// <summary>Comptabilise un coup REÇU (dégâts réellement encaissés). Appelé par le moteur de combat.</summary>
    public void RecordHit() => TimesHit++;

    /// <summary>
    /// Total des dégâts RÉELLEMENT infligés par cette unité durant le combat courant (esquive/bouclier
    /// déduits). Amorcé à 0 au spawn et NON persisté : chaque combat repart d'une unité neuve. Incrémenté par
    /// <see cref="Match"/>. Sert au récap de fin de run (dégâts par type de pion).
    /// </summary>
    public int DamageDealt { get; private set; }

    /// <summary>Comptabilise des dégâts INFLIGÉS (montant réellement encaissé par la cible). Appelé par le moteur.</summary>
    public void RecordDamage(int amount) => DamageDealt += System.Math.Max(0, amount);

    /// <summary>
    /// Puissance offerte par le trait « Rage » : +<see cref="Match.RagePowerBonus"/> dès qu'un PREMIER allié
    /// meurt pendant le combat courant. NON cumulable et une seule fois par combat — les morts suivantes ne
    /// l'augmentent pas. Amorcée à 0 au spawn et NON persistée (chaque combat repart d'une unité neuve, donc le
    /// bonus se réamorce à chaque mission). Ajoutée à la puissance effective par <see cref="Match.EffectivePower"/>
    /// tant que l'unité porte le trait.
    /// </summary>
    public int RagePower { get; private set; }

    /// <summary>Active la « Rage » (à la mort d'un allié) : FIXE le bonus sans le cumuler (idempotent, borné à
    /// <paramref name="amount"/>). Appelé par le moteur de combat.</summary>
    public void ActivateRage(int amount) => RagePower = System.Math.Max(RagePower, System.Math.Max(0, amount));

    /// <summary>
    /// Unité « pivot » dont la mort décide la partie : le commandant (joueur) ou le
    /// boss (ennemi). Voir <see cref="Match"/> pour les conditions de victoire.
    /// </summary>
    public bool IsEssential { get; init; }

    /// <summary>
    /// Comportement d'IA de cette unité (ennemis seulement). <see cref="AiKind.Normal"/> par défaut :
    /// fonce sur le joueur. <see cref="AiKind.Defensif"/> : garde une position (mission spéciale),
    /// posé à la pose de la vague selon la case de spawn de la map. Voir <see cref="EnemyAi"/>.
    /// </summary>
    public AiKind AiKind { get; set; } = AiKind.Normal;

    public MovementKind MovementKind => Movement.Kind(Domaine);
    public int MaxHp => Stat(EquipStat.Hp, Class.MaxHp);
    public int Damage => Stat(EquipStat.Damage, Class.Damage);
    public int MoveRange => Stat(EquipStat.MoveRange, Class.MoveRange);
    public int AttackRange => Stat(EquipStat.AttackRange, Class.AttackRange);

    /// <summary>Stat effective : valeur de la classe + bonus d'équipement + bonus d'arbre. Jamais négative.</summary>
    private int Stat(EquipStat stat, int fromClass) =>
        System.Math.Max(0, fromClass + EquipBonus(stat) + Buffs.BonusFor(stat));

    /// <summary>
    /// Portée d'attaque MINIMALE effective : le trait « Zone morte » interdit de frapper au contact
    /// (min = 2). Sinon la valeur de la classe (1 par défaut). Appliquée en ligne droite seulement
    /// (cf. <see cref="Match.AttackTargets"/>) : le tir diagonal au contact reste possible.
    /// </summary>
    public int MinAttackRange =>
        System.Math.Max(Class.MinAttackRange, HasTrait(Battle.Trait.ZoneMorte) ? 2 : 1);

    /// <summary>Bonus de l'équipement porté sur une stat (0 si aucun, ou si l'équipement vise une autre stat).</summary>
    private int EquipBonus(EquipStat stat) => Equipment?.BonusFor(stat) ?? 0;

    public bool IsAlive => Hp > 0;

    public void TakeDamage(int amount) => Hp = System.Math.Max(0, Hp - amount);

    /// <summary>
    /// « Queue de phénix » : l'unité TOMBÉE (0 PV) ressuscite à 1 PV et son équipement se BRISE (consommé,
    /// mis à null — une seule fois, puisque le trait <see cref="Battle.Trait.Renaissance"/> disparaît avec lui).
    /// Renvoie l'équipement brisé (pour le feedback côté scène), ou null s'il n'y avait rien à consommer.
    /// </summary>
    public Equipment? ReviveConsumingEquipment()
    {
        var broken = Equipment;
        Equipment = null;
        Hp = 1;
        return broken;
    }

    /// <summary>Soigne l'unité (borné à ses PV max).</summary>
    public void Heal(int amount) => Hp = System.Math.Min(MaxHp, Hp + amount);

    /// <summary>
    /// Vrai si l'unité porte ce <paramref name="trait"/> (cf. <see cref="Trait"/>) — par sa classe, par son
    /// <see cref="Equipment"/> OU par les <see cref="Buffs"/> de l'arbre de commandement. « Traverse allié »
    /// est porté par <see cref="UnitClass.PiercesAllies"/> et non par la liste de traits.
    /// </summary>
    public bool HasTrait(string trait)
    {
        if (Equipment is { } e && e.GrantsTrait(trait))
            return true;
        if (Buffs.GrantsTrait(trait))
            return true;
        if (trait == Battle.Trait.TraverseAllie)
            return Class.PiercesAllies;
        foreach (var t in Class.Traits)
            if (t == trait)
                return true;
        return false;
    }
}

/// <summary>Fabrique d'unités : démarre sur la classe de base du domaine.</summary>
public static class Units
{
    public static Unit Of(Domaine domaine, Faction faction) =>
        new(domaine, faction, Domaines.Of(domaine).BaseClass);

    /// <summary>Soldat (base du domaine Dame) — l'unité de troupe élémentaire du joueur.</summary>
    public static Unit Soldat(Faction faction) => Of(Domaine.Dame, faction);
}
