using System;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Equip;
using Xunit;

namespace Echec.Core.Tests;

/// <summary>
/// Système d'ÉQUIPEMENT : bonus de stat / octroi de trait sur un pion (jamais le commandant), un seul
/// par pion, collé au pion (suit le gabarit, perdu à sa mort), rendu à l'inventaire à la fusion.
/// Catalogue : <see cref="EquipmentCatalog"/> / <see cref="Equipments"/>. Pose/retrait : <see cref="Run"/>.
/// </summary>
public class EquipmentTests
{
    private static Equipment Vigueur => Equipment.OfStat("vigueur", "Vigueur", EquipStat.Hp, 5);
    private static Equipment RempartEquip => Equipment.OfTrait("rempart", "Rempart", Trait.Rempart);

    private static Run RunWith(params UnitSpec[] units) =>
        Run.Restore(units.ToList(), combatNumber: 1, seed: 1, firstRun: false);

    private static UnitSpec Soldat() => new(Domaine.Dame, Domaines.Dame.BaseClass);

    // ─── Application au Unit (combat) ────────────────────────────────────────────────────────────

    [Fact]
    public void StatEquipment_RaisesMaxHp_AndStartsFull()
    {
        var baseHp = Domaines.Dame.BaseClass.MaxHp;
        var spec = Soldat();
        spec.Equipment = Vigueur;

        var unit = spec.Spawn(Faction.Player);

        Assert.Equal(baseHp + 5, unit.MaxHp);
        Assert.Equal(baseHp + 5, unit.Hp);   // PV pleins, bonus inclus
    }

    [Fact]
    public void StatEquipment_OnlyAffectsItsOwnStat()
    {
        var spec = Soldat();
        spec.Equipment = Equipment.OfStat("force", "Force", EquipStat.Damage, 3);

        var unit = spec.Spawn(Faction.Player);

        Assert.Equal(Domaines.Dame.BaseClass.Damage + 3, unit.Damage);
        Assert.Equal(Domaines.Dame.BaseClass.MaxHp, unit.MaxHp);          // PV inchangés
        Assert.Equal(Domaines.Dame.BaseClass.MoveRange, unit.MoveRange);  // déplacement inchangé
    }

    [Fact]
    public void TraitEquipment_GrantsTrait_EvenOnAClassWithoutIt()
    {
        var spec = Soldat();   // le Soldat de base n'a aucun trait
        Assert.False(spec.Spawn(Faction.Player).HasTrait(Trait.Rempart));

        spec.Equipment = RempartEquip;
        Assert.True(spec.Spawn(Faction.Player).HasTrait(Trait.Rempart));
    }

    // ─── Catalogue / registre ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_FromJson_ParsesStatAndTrait()
    {
        const string json = """
        { "equipments": [
            { "id": "vigueur", "name": "Vigueur", "rarity": "Common", "kind": "Stat", "stat": "Hp", "amount": 5 },
            { "id": "lame",    "name": "Lame",    "rarity": "Rare",   "kind": "Trait", "trait": "Riposte" }
        ] }
        """;

        var list = EquipmentCatalog.FromJson(json);

        Assert.Equal(2, list.Count);
        var vigueur = list[0];
        Assert.Equal(EquipmentKind.Stat, vigueur.Kind);
        Assert.Equal(5, vigueur.BonusFor(EquipStat.Hp));
        Assert.Equal(EquipmentRarity.Common, vigueur.Rarity);

        var lame = list[1];
        Assert.Equal(EquipmentKind.Trait, lame.Kind);
        Assert.True(lame.GrantsTrait(Trait.Riposte));
        Assert.Equal(EquipmentRarity.Rare, lame.Rarity);
    }

    [Fact]
    public void Catalog_Icon_DefaultsToId_OrUsesExplicitField()
    {
        const string json = """
        { "equipments": [
            { "id": "vigueur", "name": "Vigueur", "kind": "Stat", "stat": "Hp", "amount": 5 },
            { "id": "lame", "name": "Lame", "kind": "Trait", "trait": "Riposte", "icon": "epee_courbe" }
        ] }
        """;

        var list = EquipmentCatalog.FromJson(json);

        Assert.Equal("vigueur", list[0].Icon);        // pas de champ icon → défaut = id
        Assert.Equal("epee_courbe", list[1].Icon);    // champ icon explicite
    }

    [Fact]
    public void Registry_Defaults_ResolveById_AndExposeCommonPool()
    {
        // Repli codé : 2 équipements communs de test (vigueur + rempart).
        Assert.NotNull(Equipments.ById("vigueur"));
        Assert.Null(Equipments.ById("inexistant"));
        Assert.All(Equipments.OfRarity(EquipmentRarity.Common), e => Assert.Equal(EquipmentRarity.Common, e.Rarity));
        Assert.NotEmpty(Equipments.OfRarity(EquipmentRarity.Common));
        Assert.NotNull(Equipments.Roll(EquipmentRarity.Common, new Random(1)));
    }

    // ─── Pose / retrait via Run ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Equip_TakesFromInventory_AndSticksToPawn()
    {
        var run = RunWith(Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var vigueur = Vigueur;
        run.AddEquipment(vigueur);

        Assert.True(run.Equip(soldat, vigueur));
        Assert.Same(vigueur, soldat.Equipment);
        Assert.Empty(run.EquipmentInventory);            // retiré de l'inventaire
    }

    [Fact]
    public void Equip_Swaps_OldReturnsToInventory()
    {
        var run = RunWith(Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var vigueur = Vigueur;
        var rempart = RempartEquip;
        run.AddEquipment(vigueur);
        run.AddEquipment(rempart);

        run.Equip(soldat, vigueur);
        run.Equip(soldat, rempart);   // remplace vigueur

        Assert.Same(rempart, soldat.Equipment);
        Assert.Contains(vigueur, run.EquipmentInventory);  // l'ancien revient
        Assert.DoesNotContain(rempart, run.EquipmentInventory);
    }

    [Fact]
    public void Equip_TraitAlreadyOnClass_IsRejected_ButAllowedElsewhere()
    {
        // Le Garde (domaine Tour) a nativement Rempart : refus de l'équipement de trait Rempart sur lui.
        var garde = Domaines.Tour.BaseClass.Evolutions[0]; // Garde (Rempart)
        var run = RunWith(new UnitSpec(Domaine.Tour, garde));
        var g = run.Roster.First(u => !u.Essential);
        var rempart = RempartEquip;
        run.AddEquipment(rempart);

        Assert.False(run.CanEquip(g, rempart));
        Assert.False(run.Equip(g, rempart));
        Assert.Null(g.Equipment);
        Assert.Contains(rempart, run.EquipmentInventory);   // pas consommé

        // Un Soldat (sans Rempart), lui, l'accepte.
        var run2 = RunWith(Soldat());
        var soldat = run2.Roster.First(u => !u.Essential);
        var rempart2 = RempartEquip;
        run2.AddEquipment(rempart2);
        Assert.True(run2.CanEquip(soldat, rempart2));
        Assert.True(run2.Equip(soldat, rempart2));
    }

    [Fact]
    public void Equip_RangeItem_ForbiddenOnMeleeCavalier_ButAllowedOnMountedArcherAndOthers()
    {
        Equipment Arc() => Equipment.OfStat("arc", "Arc", EquipStat.AttackRange, 1);

        // Cavalier de MÊLÉE (classe de base, sans « Zone morte ») : l'objet de portée est refusé.
        var melee = new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass);
        var run = RunWith(melee);
        var meleeUnit = run.Roster.First(u => !u.Essential);
        var arc = Arc();
        run.AddEquipment(arc);
        Assert.False(run.CanEquip(meleeUnit, arc));
        Assert.False(run.Equip(meleeUnit, arc));
        Assert.Null(meleeUnit.Equipment);
        Assert.Contains(arc, run.EquipmentInventory);    // pas consommé

        // Archer monté (évolution archère du Cavalier, trait « Zone morte ») : accepté.
        var archer = Domaines.Cavalier.BaseClass.Evolutions[1];   // Archer monté
        var run2 = RunWith(new UnitSpec(Domaine.Cavalier, archer));
        var archerUnit = run2.Roster.First(u => !u.Essential);
        var arc2 = Arc();
        run2.AddEquipment(arc2);
        Assert.True(run2.CanEquip(archerUnit, arc2));
        Assert.True(run2.Equip(archerUnit, arc2));

        // Hors domaine Cavalier (Soldat) : la restriction ne s'applique pas.
        var run3 = RunWith(Soldat());
        var soldat = run3.Roster.First(u => !u.Essential);
        var arc3 = Arc();
        run3.AddEquipment(arc3);
        Assert.True(run3.CanEquip(soldat, arc3));
        Assert.True(run3.Equip(soldat, arc3));
    }

    [Fact]
    public void Equip_MoveItem_ForbiddenOnAllCavaliers_IncludingMountedArcher()
    {
        Equipment Bottes() => Equipment.OfStat("botte", "Bottes", EquipStat.MoveRange, 1);

        // Cavalier de mêlée : refus de l'objet de mouvement.
        var run = RunWith(new UnitSpec(Domaine.Cavalier, Domaines.Cavalier.BaseClass));
        var melee = run.Roster.First(u => !u.Essential);
        var b1 = Bottes();
        run.AddEquipment(b1);
        Assert.False(run.CanEquip(melee, b1));
        Assert.False(run.Equip(melee, b1));
        Assert.Contains(b1, run.EquipmentInventory);    // pas consommé

        // Archer monté : refusé AUSSI (contrairement à l'objet de portée), aucune exception.
        var archer = Domaines.Cavalier.BaseClass.Evolutions[1];   // Archer monté
        var run2 = RunWith(new UnitSpec(Domaine.Cavalier, archer));
        var archerUnit = run2.Roster.First(u => !u.Essential);
        var b2 = Bottes();
        run2.AddEquipment(b2);
        Assert.False(run2.CanEquip(archerUnit, b2));
        Assert.False(run2.Equip(archerUnit, b2));

        // Hors domaine Cavalier (Soldat) : accepté.
        var run3 = RunWith(Soldat());
        var soldat = run3.Roster.First(u => !u.Essential);
        var b3 = Bottes();
        run3.AddEquipment(b3);
        Assert.True(run3.CanEquip(soldat, b3));
        Assert.True(run3.Equip(soldat, b3));
    }

    [Fact]
    public void Equip_Commander_IsRejected()
    {
        var run = new Run(seed: 1);
        var vigueur = Vigueur;
        run.AddEquipment(vigueur);

        Assert.False(run.Equip(run.Commander, vigueur));
        Assert.Null(run.Commander.Equipment);
        Assert.Contains(vigueur, run.EquipmentInventory);  // pas consommé
    }

    [Fact]
    public void HasEquipment_True_WhenEquipped_EvenWithEmptyInventory()
    {
        // La phase Équipement doit s'ouvrir même si tout est déjà équipé (réagencer/retirer).
        var run = RunWith(Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var casque = Vigueur;
        run.AddEquipment(casque);
        Assert.True(run.HasEquipment);            // en inventaire

        run.Equip(soldat, casque);
        Assert.Empty(run.EquipmentInventory);     // inventaire vide
        Assert.True(run.HasEquipment);            // mais équipé → toujours vrai

        run.Unequip(soldat);
        Assert.True(run.HasEquipment);
    }

    [Fact]
    public void HasEquipment_False_WhenNoneAtAll()
    {
        Assert.False(new Run(seed: 1).HasEquipment);
    }

    [Fact]
    public void Unequip_ReturnsToInventory()
    {
        var run = RunWith(Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var vigueur = Vigueur;
        run.AddEquipment(vigueur);
        run.Equip(soldat, vigueur);

        run.Unequip(soldat);

        Assert.Null(soldat.Equipment);
        Assert.Contains(vigueur, run.EquipmentInventory);
    }

    // ─── Fusion et permadeath ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fusion_ReturnsEquipmentToInventory_FusedIsNude()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var soldats = run.Roster.Where(u => !u.Essential).ToList();
        var vigueur = Vigueur;
        run.AddEquipment(vigueur);
        run.Equip(soldats[0], vigueur);     // un des 3 porte un équipement

        var fused = run.Fuse(soldats[0], Domaines.Dame.BaseClass.Evolutions[0]);

        Assert.NotNull(fused);
        Assert.Null(fused!.Equipment);                     // l'évolution sort nue
        Assert.Contains(vigueur, run.EquipmentInventory);  // l'équipement est rendu, pas perdu
    }

    [Fact]
    public void Death_LosesEquippedItem_SurvivorKeepsIt()
    {
        var run = RunWith(Soldat(), Soldat());
        var soldats = run.Roster.Where(u => !u.Essential).ToList();
        var doomed = soldats[0];
        var survivor = soldats[1];
        var vigueur = Vigueur;
        var rempart = RempartEquip;
        run.AddEquipment(vigueur);
        run.AddEquipment(rempart);
        run.Equip(doomed, vigueur);
        run.Equip(survivor, rempart);

        run.StartBattle();
        run.CompleteCombat(new[] { doomed }, Array.Empty<UnitSpec>());

        Assert.DoesNotContain(doomed, run.Roster);
        Assert.DoesNotContain(vigueur, run.EquipmentInventory);   // mort avec son équipement → perdu
        Assert.Same(rempart, survivor.Equipment);                 // le survivant garde le sien
    }

    [Fact]
    public void SaveRoundTrip_PreservesEquippedAndInventory()
    {
        var run = RunWith(Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var vigueur = Equipments.ById("vigueur")!;   // instances du registre (résolues par id à la reprise)
        var rempart = Equipments.ById("rempart")!;
        run.AddEquipment(vigueur);
        run.AddEquipment(rempart);
        run.Equip(soldat, vigueur);                  // vigueur équipé, rempart en inventaire

        var restored = RunSave.From(run).ToRun();

        var restoredSoldat = restored.Roster.First(u => !u.Essential);
        Assert.Equal("vigueur", restoredSoldat.Equipment?.Id);
        Assert.Contains(restored.EquipmentInventory, e => e.Id == "rempart");
        Assert.Single(restored.EquipmentInventory);
    }
}
