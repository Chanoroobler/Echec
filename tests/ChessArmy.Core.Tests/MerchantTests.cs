using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command;
using ChessArmy.Core.Command.Config;
using ChessArmy.Core.Equip;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Commandant MARCHAND : tout ce que son arbre apporte et que personne d'autre n'a — slots d'équipement
/// (le commandant s'équipe ; les pions en portent deux), butin de coffre (nombre d'objets et rareté),
/// butin de relance, recrue double, source de points « sur butin » (plafonnée par combat) et
/// « Renaissance ultime » (une fois par PARTIE).
/// </summary>
public class MerchantTests
{
    // ─── Outillage : run pilotée par l'arbre RÉELLEMENT LIVRÉ ────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AssetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { RepoRoot(), "src", "ChessArmy.Game", "Assets" }.Concat(parts).ToArray());

    private const string TreeId = "commandantMarchand";
    private const string CommanderId = "Commandant_marchand";

    /// <summary>
    /// Run neuve menée par le MARCHAND livré (units.json + commander_trees.json), avec assez de points pour
    /// acheter n'importe quel nœud. Les registres statiques sont remplacés le temps du test : l'appelant
    /// DOIT passer par <see cref="WithMerchant"/>, qui les restaure.
    /// </summary>
    private static Run MerchantRun(params string[] nodes)
    {
        var def = Commandes.ById(CommanderId)!;
        var roster = new List<UnitSpec> { new(def.Movement, def.BaseClass, essential: true) };
        var run = Run.Restore(roster, combatNumber: 1, seed: 1, firstRun: false,
            commandPoints: 100, commanderId: CommanderId);
        foreach (var id in nodes)
        {
            var node = run.Tree.ById(id) ?? throw new Xunit.Sdk.XunitException($"nœud absent de l'arbre : {id}");
            // Les PRÉREQUIS manquants sont achetés d'office (1er nœud de chaque niveau inférieur de la branche) :
            // l'arbre est réarrangé au fil de l'équilibrage, un test ne doit pas casser parce qu'un nœud a
            // changé de niveau. Un test qui tient à un prérequis PRÉCIS le cite simplement avant dans la liste.
            for (var level = 1; level < node.Level; level++)
            {
                if (run.Tree.Nodes.Any(n => n.Branch == node.Branch && n.Level == level && run.IsUnlocked(n.Id)))
                    continue;
                var filler = run.Tree.Nodes.First(n => n.Branch == node.Branch && n.Level == level);
                Assert.True(run.Unlock(filler), $"prérequis non achetable : {filler.Id}");
            }
            Assert.True(run.Unlock(node), $"nœud non achetable : {id}");
        }
        return run;
    }

    /// <summary>Charge les catalogues LIVRÉS (commandants + arbres) le temps de l'action, puis les restaure.</summary>
    private static void WithMerchant(System.Action<Run> body, params string[] nodes)
    {
        var previousCommandes = Commandes.All;   // les autres tests comptent sur le registre qu'ils ont trouvé
        try
        {
            Commandes.Load(Battle.Config.DomaineCatalog.CommandesFromJson(
                System.IO.File.ReadAllText(AssetPath("Config", "units.json"))));
            CommandTrees.Load(CommandTreeCatalog.FromJson(
                System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json"))));
            body(MerchantRun(nodes));
        }
        finally
        {
            CommandTrees.ResetToDefaults();
            Commandes.Load(previousCommandes);
        }
    }

    // ─── Config livrée ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShippedMerchant_IsLocked_AndEarnsItsPointsOnLoot()
    {
        var commanders = Battle.Config.DomaineCatalog.CommandesFromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "units.json")));
        var marchand = commanders.Single(c => c.Id == CommanderId);

        Assert.False(marchand.StartsUnlocked);          // il se débloque au compteur de coffres
        Assert.Equal(TreeId, marchand.TreeId);
        Assert.Equal(1, marchand.LootPoints);           // +1 point par butin…
        Assert.Equal(3, marchand.LootCap);              // …dans la limite de 3 par combat
        // Contrepartie : la mission réussie ne lui rapporte qu'UN point (2 pour tous les autres).
        Assert.Equal(1, marchand.MissionPoints);
        Assert.Equal(Run.PointsPerMission, commanders.First(c => c.Id != CommanderId).MissionPoints);
        // Sa source est le BUTIN : aucune des autres (sinon la ligne de gain de l'arbre en montrerait une autre).
        Assert.Equal(0, marchand.FusionPoints);
        Assert.Equal(0, marchand.OnHitPoints);
        Assert.Equal(0, marchand.RangedHitPoints);
        Assert.Equal(0, marchand.JumpPoints);
    }

    /// <summary>
    /// Les six effets propres à cet arbre n'existent nulle part ailleurs : si un id de nœud est renommé ou un
    /// « kind » mal orthographié, le nœud continue de s'acheter mais ne fait plus rien. On épingle le câblage.
    /// </summary>
    [Fact]
    public void ShippedMerchantTree_WiresItsSixOwnEffects()
    {
        var tree = CommandTreeCatalog.FromJson(
            System.IO.File.ReadAllText(AssetPath("Config", "commander_trees.json"))).Single(t => t.Id == TreeId);

        // Premier effet du nœud : c'est celui qui porte son intention (un nœud peut en cumuler plusieurs,
        // cf. « mar_cmd_slot_1 » qui ajoute le bonus par objet porté APRÈS son emplacement).
        CommandEffect Effect(string id) => tree.ById(id)!.Effects[0];

        Assert.Equal(CommandEffectKind.CommanderEquipSlots, Effect("mar_cmd_slot_1").Kind);
        Assert.Equal(CommandEffectKind.CommanderEquipSlots, Effect("mar_cmd_slot_2").Kind);
        Assert.Equal(CommandEffectKind.UnitEquipSlots, Effect("mar_unit_slot_2").Kind);
        Assert.Equal(CommandEffectKind.ChestExtraItem, Effect("mar_loot_coffre_double").Kind);
        Assert.Equal(CommandEffectKind.ChestRarityBonus, Effect("mar_loot_rarete_1").Kind);
        Assert.Equal(CommandEffectKind.RerollEquipment, Effect("mar_loot_relance").Kind);
        Assert.Equal(CommandEffectKind.RecruitExtraUnit, Effect("mar_logi_recrue_double").Kind);
        Assert.Equal(Trait.RenaissanceUltime, Effect("mar_cmd_renaissance").Trait);
        // Les deux nœuds « par équipement possédé » comptent tout le STOCK (porté ou en réserve)…
        Assert.Equal(CommandScale.PerEquippedItem, Effect("mar_cmd_pv_stock").Scale);
        Assert.Equal(CommandScale.PerEquippedItem, Effect("mar_cmd_puissance_stock").Scale);

        // …alors que la SACOCHE ne compte que ce que le commandant a lui-même sur le dos : +2 pv / +2 puissance
        // par objet porté, en plus de l'emplacement. Les deux échelles se ressemblent, les confondre passerait
        // inaperçu en jeu.
        var satchel = tree.ById("mar_cmd_slot_1")!.Effects;
        Assert.Equal(3, satchel.Count);
        foreach (var stat in satchel.Where(e => e.Kind == CommandEffectKind.CommanderStat))
        {
            Assert.Equal(CommandScale.PerOwnEquippedItem, stat.Scale);
            Assert.Equal(2, stat.Amount);
        }
        Assert.Contains(satchel, e => e.Stat == EquipStat.Hp && e.Kind == CommandEffectKind.CommanderStat);
        Assert.Contains(satchel, e => e.Stat == EquipStat.Damage && e.Kind == CommandEffectKind.CommanderStat);
    }

    // ─── Slots d'équipement ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommanderHasNoEquipmentSlot_UntilTheNodeIsBought()
    {
        WithMerchant(run =>
        {
            var vigueur = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            run.AddEquipment(vigueur);

            Assert.Equal(0, run.CommanderEquipSlots);
            Assert.False(run.CanEquip(run.Commander, vigueur));
            Assert.False(run.Equip(run.Commander, vigueur));
        });
    }

    [Fact]
    public void CommanderEquipSlotNodes_LetHimCarryOneThenTwoItems()
    {
        WithMerchant(run =>
        {
            var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 3);
            run.AddEquipment(a);
            run.AddEquipment(b);

            Assert.Equal(2, run.CommanderEquipSlots);   // les deux nœuds achetés
            Assert.True(run.Equip(run.Commander, a));
            Assert.True(run.Equip(run.Commander, b));
            Assert.Equal(new[] { a, b }, run.Commander.Equipments);

            // Les DEUX bonus se cumulent sur l'unité posée, plus le +2/+2 PAR OBJET PORTÉ de la Sacoche
            // (deux objets sur le dos → +4/+4).
            var unit = run.Commander.Spawn(Faction.Player, run.BuffsFor(run.Commander));
            Assert.Equal(run.Commander.UnitClass.MaxHp + 5 + 4, unit.MaxHp);
            Assert.Equal(run.Commander.UnitClass.Damage + 3 + 4, unit.Damage);
        }, "mar_cmd_slot_1", "mar_cmd_esquive", "mar_cmd_slot_2");   // slot_2 est au niveau 3 : il faut un niveau 2
    }

    /// <summary>
    /// Le Marchand se déplace en L : il est MONTÉ. Les deux interdits du domaine Cavalier (bottes pour tous,
    /// arc pour la mêlée) s'appliquent donc à lui comme à n'importe quel cavalier — avoir payé ses slots dans
    /// l'arbre ne les lève pas.
    /// </summary>
    [Fact]
    public void MountedCommander_StillRefusesBootsAndBow()
    {
        WithMerchant(run =>
        {
            var cmd = run.Commander;
            Assert.Equal(Domaine.Cavalier, cmd.Domaine);
            Assert.Equal(1, run.CommanderEquipSlots);   // le slot est bien acheté : c'est le DOMAINE qui refuse

            var bottes = Equipment.OfStat("bottes", "Bottes", EquipStat.MoveRange, 1);
            var arc = Equipment.OfStat("arc", "Arc", EquipStat.AttackRange, 1);
            run.AddEquipment(bottes);
            run.AddEquipment(arc);

            Assert.False(run.CanEquip(cmd, bottes));
            Assert.False(run.CanEquip(cmd, arc));
            Assert.False(run.Equip(cmd, bottes));
            Assert.False(run.Equip(cmd, arc));

            // Les autres objets passent : la restriction vise ces deux familles, pas l'équipement en général.
            var vigueur = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            run.AddEquipment(vigueur);
            Assert.True(run.Equip(cmd, vigueur));
        }, "mar_cmd_slot_1");
    }

    [Fact]
    public void UnitEquipSlotNode_GivesEveryPawnASecondSlot()
    {
        WithMerchant(run =>
        {
            var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            run.AddUnit(soldat);
            var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 3);
            run.AddEquipment(a);
            run.AddEquipment(b);

            Assert.Equal(2, run.UnitEquipSlots);
            Assert.True(run.Equip(soldat, a));
            Assert.True(run.Equip(soldat, b));
            Assert.Equal(2, soldat.Equipments.Count);   // les deux TIENNENT (sans le nœud, le 1er serait éjecté)
        }, "mar_unit_slot_2");
    }

    [Fact]
    public void WithoutTheNode_ASecondItemEvictsTheFirstBackToTheInventory()
    {
        var run = Run.Restore(
            new List<UnitSpec> { new(Domaine.Dame, Commandes.Commander.BaseClass, essential: true) },
            combatNumber: 1, seed: 1, firstRun: false);
        var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
        run.AddUnit(soldat);
        var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
        var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 3);
        run.AddEquipment(a);
        run.AddEquipment(b);

        Assert.True(run.Equip(soldat, a));
        Assert.True(run.Equip(soldat, b));
        Assert.Equal(new[] { b }, soldat.Equipments);           // un seul slot : le second remplace le premier
        Assert.Contains(a, run.EquipmentInventory);             // …et l'ancien revient à l'inventaire
    }

    [Fact]
    public void CanReceive_RefusesASecondCopyOfTheSameItem()
    {
        WithMerchant(run =>
        {
            var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            run.AddUnit(soldat);
            var vigueur = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            run.AddEquipment(vigueur);
            run.AddEquipment(vigueur);

            Assert.True(run.Equip(soldat, vigueur));
            Assert.False(run.CanReceive(soldat, vigueur));   // deux fois le même objet gaspillerait le slot
            Assert.True(run.CanEquip(soldat, vigueur));      // …mais la COMPATIBILITÉ de principe reste vraie
            Assert.False(run.Equip(soldat, vigueur));
            Assert.Single(soldat.Equipments);
        }, "mar_unit_slot_2");
    }

    // ─── Bonus « par équipement en réserve » ─────────────────────────────────────────────────────

    [Fact]
    public void ArsenalNodes_ScaleOnTheWholeStock_EquippedOrNot()
    {
        WithMerchant(run =>
        {
            var cmd = run.Commander;
            var baseHp = cmd.UnitClass.MaxHp;
            var baseDmg = cmd.UnitClass.Damage;

            var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 0);
            var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 0);
            run.AddEquipment(a);
            run.AddEquipment(b);

            // Deux objets en INVENTAIRE, posés sur personne : ils comptent quand même — c'est le STOCK
            // du Marchand qui porte ces nœuds, pas ce qu'il en a équipé.
            Assert.Equal(2, run.EquippedItemCount);
            var stocked = cmd.Spawn(Faction.Player, run.BuffsFor(cmd));
            Assert.Equal(baseHp + 2 * 2, stocked.MaxHp);
            Assert.Equal(baseDmg + 2 * 1, stocked.Damage);

            // ÉQUIPER ne change rien : l'objet passe de l'inventaire au pion, le total est le même. On les pose
            // sur des PIONS et jamais sur le commandant : le +2/+2 « par objet qu'il porte » de la Sacoche
            // (nœud obligatoire pour atteindre ceux-ci) brouillerait la mesure. Il a son propre test.
            var s1 = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            var s2 = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            run.AddUnit(s1);
            run.AddUnit(s2);
            Assert.True(run.Equip(s1, a));
            Assert.True(run.Equip(s2, b));
            Assert.Equal(2, run.EquippedItemCount);
            var armed = cmd.Spawn(Faction.Player, run.BuffsFor(cmd));
            Assert.Equal(baseHp + 2 * 2, armed.MaxHp);
            Assert.Equal(baseDmg + 2 * 1, armed.Damage);

            // Retirer un objet le renvoie à l'inventaire : toujours le même total.
            run.Unequip(s1);
            Assert.Equal(2, run.EquippedItemCount);
            Assert.Equal(baseHp + 2 * 2, cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).MaxHp);

            // Seule une VRAIE perte fait retomber le bonus (ici l'objet est détruit).
            run.RemoveEquipment(a);
            Assert.Equal(1, run.EquippedItemCount);
            Assert.Equal(baseHp + 2, cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).MaxHp);
        }, "mar_cmd_slot_1", "mar_cmd_pv_stock", "mar_cmd_puissance_stock");
    }

    [Fact]
    public void SatchelNode_GivesTheCommanderStats_PerItemHeCarriesHimself()
    {
        WithMerchant(run =>
        {
            var cmd = run.Commander;
            var baseHp = cmd.UnitClass.MaxHp;
            var baseDmg = cmd.UnitClass.Damage;

            // Objets SANS bonus propre : ce qu'on mesure ne vient que du nœud.
            var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 0);
            var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 0);
            run.AddEquipment(a);
            run.AddEquipment(b);

            // Deux objets en STOCK mais aucun sur le dos du commandant : ce nœud-ci ne donne rien.
            Assert.Equal(baseHp, cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).MaxHp);
            Assert.Equal(baseDmg, cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).Damage);

            // Posé sur un PION : toujours rien pour le commandant (l'échelle est « ce que JE porte »).
            var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            run.AddUnit(soldat);
            Assert.True(run.Equip(soldat, a));
            Assert.Equal(baseHp, cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).MaxHp);

            // Sur le COMMANDANT : +2 pv et +2 puissance.
            Assert.True(run.Equip(cmd, b));
            var armed = cmd.Spawn(Faction.Player, run.BuffsFor(cmd));
            Assert.Equal(baseHp + 2, armed.MaxHp);
            Assert.Equal(baseDmg + 2, armed.Damage);
        }, "mar_cmd_slot_1");
    }

    [Fact]
    public void SatchelNode_Stacks_OnTheSecondSlot()
    {
        WithMerchant(run =>
        {
            var cmd = run.Commander;
            var baseHp = cmd.UnitClass.MaxHp;
            var baseDmg = cmd.UnitClass.Damage;

            var a = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 0);
            var b = Equipment.OfStat("force", "Force", EquipStat.Damage, 0);
            run.AddEquipment(a);
            run.AddEquipment(b);
            Assert.True(run.Equip(cmd, a));
            Assert.True(run.Equip(cmd, b));   // BESACE : deuxième emplacement

            // Le bonus est PAR OBJET PORTÉ : deux objets = +4/+4. Et la Besace ne le redonne pas une
            // seconde fois — il n'est déclaré que sur la Sacoche.
            var armed = cmd.Spawn(Faction.Player, run.BuffsFor(cmd));
            Assert.Equal(baseHp + 4, armed.MaxHp);
            Assert.Equal(baseDmg + 4, armed.Damage);
        }, "mar_cmd_slot_1", "mar_cmd_esquive", "mar_cmd_slot_2");   // Esquive : palier 2 SANS bonus de stat
    }

    // ─── Butin : coffres, raretés, relance, recrue ───────────────────────────────────────────────

    [Fact]
    public void ChestGivesTwoItems_OnlyWithTheNode()
    {
        WithMerchant(run =>
        {
            Assert.Equal(1, run.ChestItemCount);
            Assert.Single(run.RollChestContents(new System.Random(1)));
        });

        WithMerchant(run =>
        {
            Assert.Equal(2, run.ChestItemCount);
            Assert.Equal(2, run.RollChestContents(new System.Random(1)).Count);
        }, "mar_loot_coffre_double");
    }

    [Fact]
    public void RarityNodes_AddSevenPointsEach_ToRareAndLegendary()
    {
        // Phase 1 sans nœud : légendaire < 5 %, rare < 5+30 %. Avec les DEUX nœuds : 19 % et 19+44 %.
        WithMerchant(run =>
        {
            Assert.Equal(EquipmentRarity.Common, run.ResolveChestRarity(36.0));
        });

        WithMerchant(run =>
        {
            Assert.Equal(14, run.ChestRarityBonus);
            Assert.Equal(EquipmentRarity.Legendary, run.ResolveChestRarity(18.0));   // 5 + 14 points
            Assert.Equal(EquipmentRarity.Rare, run.ResolveChestRarity(60.0));        // 19 + (30 + 14) points
        }, "mar_loot_rarete_1", "mar_loot_rarete_2");

        // Borne haute, sur une run NEUVE (les tirages ci-dessus font bouger la pitié) : 19 + 44 = 63 points,
        // au-delà c'est commun.
        WithMerchant(run => Assert.Equal(EquipmentRarity.Common, run.ResolveChestRarity(63.0)),
            "mar_loot_rarete_1", "mar_loot_rarete_2");
    }

    [Fact]
    public void RerollNode_AlsoYieldsAnEquipment()
    {
        WithMerchant(run =>
        {
            var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
            run.AddUnit(soldat);
            run.AddReroll();

            var loot = new List<Equipment>();
            Assert.NotNull(run.RerollUnit(soldat, new System.Random(1), _ => true, loot));

            Assert.Single(loot);
            Assert.Contains(loot[0], run.EquipmentInventory);   // l'objet est DÉJÀ en inventaire
        }, "mar_loot_relance");
    }

    [Fact]
    public void RecycleNode_GivesNothing_WithoutTheNode()
    {
        WithMerchant(run =>
        {
            Assert.Equal(0, run.RecycleRecruits);
            Assert.False(run.RecycleRecruitAvailable);
            Assert.Empty(run.GrantRecycleRecruits(new System.Random(1), _ => true));
        });
    }

    [Fact]
    public void RecycleNode_GivesTheTier1_TheArmyHasTheMostOf()
    {
        WithMerchant(run =>
        {
            // 2 Soldats (Dame) contre 1 seul du domaine Tour : la recrue doit renforcer la MAJORITÉ, c'est ce
            // qui rapproche d'une fusion.
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
            run.AddUnit(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));

            var granted = run.GrantRecycleRecruits(new System.Random(1), _ => true);

            Assert.Single(granted);
            Assert.Equal(Domaines.Dame.BaseClass, granted[0].UnitClass);
            Assert.Contains(granted[0], run.Roster);   // le pion est DÉJÀ dans l'armée
        }, "mar_loot_relance", "mar_loot_ferraille");
    }

    [Fact]
    public void RecycleNode_IgnoresTier2AndTheCommander()
    {
        WithMerchant(run =>
        {
            // Le commandant et un tier 2 ne doivent PAS peser dans le compte : seul le Lancier (tier 1) est
            // éligible, c'est donc lui qui sort même s'il est minoritaire en nombre de pions.
            var tier2 = Domaines.Dame.BaseClass.Evolutions[0];
            run.AddUnit(new UnitSpec(Domaine.Dame, tier2));
            run.AddUnit(new UnitSpec(Domaine.Dame, tier2));
            run.AddUnit(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));

            var granted = run.GrantRecycleRecruits(new System.Random(1), _ => true);

            Assert.Single(granted);
            Assert.Equal(1, granted[0].UnitClass.Tier);
            Assert.Equal(Domaines.Tour.BaseClass, granted[0].UnitClass);
        }, "mar_loot_relance", "mar_loot_ferraille");
    }

    [Fact]
    public void RecycleNode_GivesOnlyOnePawn_PerPlacementPhase()
    {
        WithMerchant(run =>
        {
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));

            Assert.True(run.RecycleRecruitAvailable);
            Assert.Single(run.GrantRecycleRecruits(new System.Random(1), _ => true));

            // Recycler d'autres objets dans la MÊME phase de placement rapporte toujours la relance, mais
            // plus aucun pion.
            Assert.False(run.RecycleRecruitAvailable);
            Assert.Empty(run.GrantRecycleRecruits(new System.Random(1), _ => true));
            Assert.Empty(run.GrantRecycleRecruits(new System.Random(2), _ => true));
        }, "mar_loot_relance", "mar_loot_ferraille");
    }

    [Fact]
    public void RecycleNode_ChargeComesBack_AtTheNextMission()
    {
        WithMerchant(run =>
        {
            run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
            Assert.Single(run.GrantRecycleRecruits(new System.Random(1), _ => true));
            Assert.False(run.RecycleRecruitAvailable);

            run.CompleteCombat(System.Array.Empty<UnitSpec>(), System.Array.Empty<UnitSpec>());
            run.SkipRecruitment();   // mission suivante = nouvelle phase de placement

            Assert.True(run.RecycleRecruitAvailable);
            Assert.Single(run.GrantRecycleRecruits(new System.Random(1), _ => true));
        }, "mar_loot_relance", "mar_loot_ferraille");
    }

    [Fact]
    public void RecycleNode_FullReserve_DoesNotBurnTheCharge()
    {
        WithMerchant(run =>
        {
            while (!run.IsReserveFull)
                run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));

            Assert.Empty(run.GrantRecycleRecruits(new System.Random(1), _ => true));
            Assert.True(run.RecycleRecruitAvailable);   // rien n'est arrivé : la charge reste entière
        }, "mar_loot_relance", "mar_loot_ferraille");
    }

    [Fact]
    public void RecruitTile_GivesTwoPawns_OnlyWithTheNode()
    {
        WithMerchant(run => Assert.Equal(1, run.RecruitTileUnits));
        WithMerchant(run => Assert.Equal(2, run.RecruitTileUnits),
            "mar_logi_deploy_1", "mar_logi_reserve_1", "mar_logi_recrue_double");
    }

    // ─── Source de points « sur butin » ──────────────────────────────────────────────────────────

    [Fact]
    public void LootPoints_AreCappedPerCombat_AndResetAtTheNextBattle()
    {
        WithMerchant(run =>
        {
            var before = run.CommandPoints;
            run.StartBattle();

            Assert.Equal(1, run.GrantLootPoints());   // coffre
            Assert.Equal(1, run.GrantLootPoints());   // recrue : MÊME plafond
            Assert.Equal(1, run.GrantLootPoints());   // 3e butin : encore bon (plafond 3)
            Assert.Equal(0, run.GrantLootPoints());   // 4e butin du combat : plus rien
            Assert.Equal(before + 3, run.CommandPoints);

            run.ReturnToPlacement();
            run.StartBattle();                        // combat suivant : le plafond repart à zéro
            Assert.Equal(1, run.GrantLootPoints());
            Assert.Equal(before + 4, run.CommandPoints);
        });
    }

    /// <summary>
    /// Le Marchand paie son butin de terrain sur la MISSION : une mission réussie ne lui rapporte qu'un point
    /// là où les autres commandants en touchent deux. Ce sont les DEUX chemins de clôture qui doivent le
    /// respecter (combat normal et mission spéciale sans draft).
    /// </summary>
    [Fact]
    public void CompletedMission_GrantsTheMerchantOnlyOnePoint()
    {
        WithMerchant(run =>
        {
            var before = run.CommandPoints;
            run.StartBattle();
            run.CompleteCombat(System.Array.Empty<UnitSpec>(), System.Array.Empty<UnitSpec>());
            Assert.Equal(before + 1, run.CommandPoints);
        });

        WithMerchant(run =>
        {
            var before = run.CommandPoints;
            run.StartBattle();
            run.CompleteSpecialNoDraft(System.Array.Empty<UnitSpec>());
            Assert.Equal(before + 1, run.CommandPoints);
        });
    }

    [Fact]
    public void LootPoints_DoNothingForACommanderWhoseSourceIsElsewhere()
    {
        var run = Run.Restore(
            new List<UnitSpec> { new(Domaine.Dame, Commandes.Commander.BaseClass, essential: true) },
            combatNumber: 1, seed: 1, firstRun: false);
        run.StartBattle();
        Assert.Equal(0, run.GrantLootPoints());
    }

    // ─── Renaissance ultime ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void UltimateRevive_BringsTheCommanderBackAtFullHp_ThenNeverAgain()
    {
        var cls = new UnitClass("Chef", "chef", tier: 1, maxHp: 20, damage: 0, moveRange: 1, attackRange: 1);
        var buffs = CommandBuffs.From(
            new[] { CommandEffect.CommanderTrait(Trait.RenaissanceUltime) }, commander: true, distinctPairs: 0);
        var commander = new Unit(Domaine.Dame, Faction.Player, cls, buffs: buffs);

        commander.TakeDamage(999);
        Assert.False(commander.IsAlive);
        Assert.True(commander.TryUltimateRevive());
        Assert.Equal(commander.MaxHp, commander.Hp);   // PV PLEINS (pas 1 PV comme la Queue de phénix)

        commander.TakeDamage(999);
        Assert.False(commander.TryUltimateRevive());   // une seule fois
    }

    [Fact]
    public void UltimateRevive_OnceConsumed_TheTraitLeavesTheCommanderBuffs()
    {
        WithMerchant(run =>
        {
            var cmd = run.Commander;
            Assert.True(cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).HasTrait(Trait.RenaissanceUltime));

            run.UseUltimateRevive();   // consommée pour TOUTE la partie
            Assert.True(run.UltimateReviveUsed);
            Assert.False(cmd.Spawn(Faction.Player, run.BuffsFor(cmd)).HasTrait(Trait.RenaissanceUltime));
        }, "mar_cmd_slot_1", "mar_cmd_esquive", "mar_cmd_slot_2", "mar_cmd_renaissance");
    }

    // ─── Persistance ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_RoundTripsEveryWornItem_AndTheSpentUltimateRevive()
    {
        try
        {
            var vigueur = Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
            var force = Equipment.OfStat("force", "Force", EquipStat.Damage, 3);
            Equipments.Load(new[] { vigueur, force });

            WithMerchant(run =>
            {
                var soldat = new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass);
                run.AddUnit(soldat);
                run.AddEquipment(vigueur);
                run.AddEquipment(force);
                Assert.True(run.Equip(soldat, vigueur));
                Assert.True(run.Equip(soldat, force));
                run.UseUltimateRevive();

                var restored = RunSave.From(run).ToRun();

                var restoredSoldat = restored.Roster.Single(u => !u.Essential);
                Assert.Equal(new[] { "vigueur", "force" }, restoredSoldat.Equipments.Select(e => e.Id));
                Assert.True(restored.UltimateReviveUsed);
            }, "mar_unit_slot_2");
        }
        finally { Equipments.ResetToDefaults(); }
    }

    /// <summary>
    /// Une sauvegarde MONO-SLOT (champ hérité <c>equipment</c>, écrite avant le Marchand) doit continuer à
    /// rendre son équipement : sans ce repli, toutes les parties en cours perdraient leur équipement posé.
    /// </summary>
    [Fact]
    public void LegacySingleSlotSave_StillRestoresItsItem()
    {
        try
        {
            Equipments.Load(new[] { Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5) });

            var legacy = new UnitSpecSave
            {
                Domaine = Domaine.Dame,
                Class = Domaines.Dame.BaseClass.Asset,
                Equipment = "vigueur",   // ancien champ, EquipmentIds absent
            };

            Assert.Equal(new[] { "vigueur" }, legacy.ToSpec().Equipments.Select(e => e.Id));
        }
        finally { Equipments.ResetToDefaults(); }
    }
}
