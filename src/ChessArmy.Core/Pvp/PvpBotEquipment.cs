using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Équipements des armées de bots, tirés à chaque partie dans le budget d'équipement. Les bots d'Argent les posent
/// au hasard ; ceux d'Or et Master les donnent aux BONS pions, comme un joueur : PV et traits défensifs aux pions
/// de mêlée du front, dégâts et portée aux tireurs, auras au milieu des alliés, déplacement aux pièces lentes.
/// Les restrictions du solo sont respectées (monture : ni bottes ni arc sauf archer monté ; pas d'« Attaque libre »
/// sur une Dame).
/// </summary>
public static class PvpBotEquipment
{
    private static readonly HashSet<string> Defensive = new()
    {
        Trait.Rempart, Trait.Esquive, Trait.Riposte, Trait.Epines, Trait.Renaissance, Trait.Interception,
        Trait.DrainDeVie, Trait.Berserk, Trait.Rage, Trait.Recule, Trait.Impact, Trait.Duelliste, Trait.LoupSolitaire,
    };

    private static readonly HashSet<string> Shooting = new()
    {
        Trait.Balistique, Trait.Transpercement, Trait.BalleRebondissante, Trait.JavelotMeurtrier, Trait.Orage,
        Trait.Statique, Trait.Grenade, Trait.Tempete,
    };

    private static readonly HashSet<string> Support = new()
    {
        Trait.AuraDeRempart, Trait.AuraDePuissance, Trait.AuraDeCelerite, Trait.LienDePuissance, Trait.Formation,
        Trait.Soin, Trait.Seringue,
    };

    private static readonly HashSet<string> Mobility = new() { Trait.Vol, Trait.Franchissement };

    /// <summary>Même armée, équipements tirés au hasard dans le budget ; <paramref name="smart"/> = chacun sur le
    /// pion qui en profite le plus, sinon sur un pion au hasard (qui peut le porter).</summary>
    public static PvpBuild Equip(PvpBuild army, Random rng, bool smart)
    {
        var bare = army.WithoutEquipment();
        var classes = bare.Slots.Select(s => PvpCatalog.FindClass(s.ClassAsset)).ToList();
        bool Carries(int i, Equipment item) => classes[i] is { } c && CanCarry(c.Domaine, c.Class, item);
        PvpBuild? last = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = bare.Clone();
            var items = DrawItems(result.Slots.Count, rng, e => Enumerable.Range(0, classes.Count).Any(i => Carries(i, e)));
            var free = Enumerable.Range(0, result.Slots.Count).ToList();
            var placed = true;
            foreach (var item in items)
            {
                var carriers = free.Where(i => Carries(i, item)).ToList();
                if (carriers.Count == 0)
                {
                    placed = false;   // son seul porteur a déjà un objet : on retire
                    break;
                }
                var pick = smart
                    ? carriers.OrderByDescending(i => Fit(item, classes[i]!.Value.Class, result.Slots[i])).ThenBy(_ => rng.Next()).First()
                    : carriers[rng.Next(carriers.Count)];
                result.Slots[pick] = result.Slots[pick] with { EquipmentId = item.Id };
                free.Remove(pick);
            }
            if (placed)
                return result;
            last = result;
        }
        return last ?? bare;
    }

    /// <summary>Objets tirés au hasard jusqu'à remplir le budget d'équipement (pile si possible), au plus un par
    /// pion ; les plus chers d'abord pour que le placement malin serve d'abord les gros objets.</summary>
    private static List<Equipment> DrawItems(int pieces, Random rng, Func<Equipment, bool> usable)
    {
        var config = PvpConfig.Current;
        var pool = PvpCatalog.AllEquipment().Where(e => config.CostOf(e) > 0 && usable(e)).ToList();
        List<Equipment>? best = null;
        var bestLeft = int.MaxValue;
        for (var attempt = 0; attempt < 50 && bestLeft > 0; attempt++)
        {
            var items = new List<Equipment>();
            var left = config.EquipmentBudget;
            while (left > 0 && items.Count < pieces)
            {
                var affordable = pool.Where(e => config.CostOf(e) <= left
                    && items.Count(i => i.Id == e.Id) < config.MaxSameEquipment).ToList();   // 2 exemplaires au plus
                if (affordable.Count == 0)
                    break;
                var pick = affordable[rng.Next(affordable.Count)];
                items.Add(pick);
                left -= config.CostOf(pick);
            }
            if (left < bestLeft)
                (best, bestLeft) = (items, left);
        }
        return (best ?? new List<Equipment>()).OrderByDescending(config.CostOf).ToList();
    }

    /// <summary>Restrictions d'équipement du solo (cf. Run.CanEquip) : monture et Dame.</summary>
    public static bool CanCarry(Domaine domaine, UnitClass cls, Equipment item)
    {
        if (domaine == Domaine.Dame && item.GrantsTrait(Trait.AttaqueLibre))
            return false;
        if (domaine == Domaine.Cavalier)
        {
            if (item.BonusFor(EquipStat.AttackRange) > 0 && !cls.Traits.Contains(Trait.ZoneMorte))
                return false;
            if (item.BonusFor(EquipStat.MoveRange) > 0)
                return false;
        }
        return true;
    }

    /// <summary>Intérêt de donner <paramref name="item"/> à ce pion (plus haut = mieux).</summary>
    public static double Fit(Equipment item, UnitClass cls, PvpBuildSlot slot)
    {
        var ranged = cls.AttackRange >= Match.RangedAttackRange;
        var front = slot.Line == PvpBuild.FrontLine;
        var central = 3.5 - Math.Abs(slot.Column - 3.5);   // 0.5 au bord, 3.5 au centre
        var worth = 1 + (cls.MaxHp * 0.3 + cls.Damage * 0.4) / 10;   // les meilleures pièces d'abord
        var score = 0.0;
        foreach (var effect in item.Effects)
        {
            if (effect.Trait is { } trait)
            {
                if (cls.Traits.Contains(trait))
                    continue;   // déjà natif : sans intérêt
                if (Defensive.Contains(trait)) score += (ranged ? 0.3 : 1.0) + (front ? 0.5 : 0);
                else if (Shooting.Contains(trait)) score += ranged ? 1.5 : 0.2;
                else if (Support.Contains(trait)) score += 0.6 + central * 0.25 + (front ? 0 : 0.3);
                else if (Mobility.Contains(trait)) score += 1.5 - Math.Min(cls.MoveRange, 4) * 0.3;
                else score += 0.5;
                continue;
            }
            score += effect.Stat switch
            {
                EquipStat.Hp => effect.Amount * ((ranged ? 0.4 : 1.0) + (front ? 0.4 : 0)),
                EquipStat.Damage => effect.Amount * (ranged ? 1.0 : 0.8),
                EquipStat.AttackRange => effect.Amount * (ranged ? 1.5 : 0.2),
                EquipStat.MoveRange => effect.Amount * (1.5 - Math.Min(cls.MoveRange, 4) * 0.3),
                _ => 0,
            };
        }
        return score * worth;
    }
}
