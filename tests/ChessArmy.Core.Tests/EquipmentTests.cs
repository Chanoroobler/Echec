using System;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Equip;
using Xunit;

namespace ChessArmy.Core.Tests;

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

    [Fact]
    public void MultiEffectEquipment_AppliesStatAndTrait_Together()
    {
        var baseHp = Domaines.Dame.BaseClass.MaxHp;
        var spec = Soldat();   // sans Rempart natif
        spec.Equipment = Equipment.Of("cuirasse", "Cuirasse", EquipmentRarity.Rare, new[]
        {
            EquipEffect.OfStat(EquipStat.Hp, 6),
            EquipEffect.OfTrait(Trait.Rempart),
        });

        var unit = spec.Spawn(Faction.Player);

        Assert.Equal(baseHp + 6, unit.MaxHp);          // le bonus de stat s'applique
        Assert.True(unit.HasTrait(Trait.Rempart));     // ET le trait s'applique
    }

    [Fact]
    public void MultiEffectEquipment_TwoStats_BothApply()
    {
        var b = Domaines.Dame.BaseClass;
        var spec = Soldat();
        spec.Equipment = Equipment.Of("brassards", "Brassards", EquipmentRarity.Common, new[]
        {
            EquipEffect.OfStat(EquipStat.Hp, 4),
            EquipEffect.OfStat(EquipStat.Damage, 2),
        });

        var unit = spec.Spawn(Faction.Player);

        Assert.Equal(b.MaxHp + 4, unit.MaxHp);
        Assert.Equal(b.Damage + 2, unit.Damage);
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
        Assert.False(vigueur.GrantsAnyTrait);
        Assert.Equal(5, vigueur.BonusFor(EquipStat.Hp));
        Assert.Equal(EquipmentRarity.Common, vigueur.Rarity);

        var lame = list[1];
        Assert.True(lame.GrantsAnyTrait);
        Assert.True(lame.GrantsTrait(Trait.Riposte));
        Assert.Equal(EquipmentRarity.Rare, lame.Rarity);
    }

    [Fact]
    public void Catalog_FromJson_ParsesMultiEffect_StatPlusTrait_AndTwoStats()
    {
        const string json = """
        { "equipments": [
            { "id": "cuirasse", "name": "Cuirasse", "rarity": "Rare", "effects": [
                { "stat": "Hp", "amount": 6 },
                { "trait": "Rempart" }
            ] },
            { "id": "brassards", "name": "Brassards", "effects": [
                { "stat": "Hp", "amount": 4 },
                { "stat": "Damage", "amount": 2 }
            ] }
        ] }
        """;

        var list = EquipmentCatalog.FromJson(json);

        var cuirasse = list[0];
        Assert.Equal(6, cuirasse.BonusFor(EquipStat.Hp));      // effet de stat
        Assert.True(cuirasse.GrantsTrait(Trait.Rempart));      // + effet de trait
        Assert.True(cuirasse.GrantsAnyTrait);

        var brassards = list[1];
        Assert.Equal(4, brassards.BonusFor(EquipStat.Hp));     // deux stats cumulées
        Assert.Equal(2, brassards.BonusFor(EquipStat.Damage));
        Assert.False(brassards.GrantsAnyTrait);
    }

    [Fact]
    public void Catalog_FromJson_ParsesLegendaryRarity()
    {
        const string json = """
        { "equipments": [
            { "id": "couronne", "name": "Couronne", "rarity": "Legendary", "effects": [
                { "stat": "Hp", "amount": 8 }, { "trait": "Rempart" }
            ] }
        ] }
        """;

        var item = EquipmentCatalog.FromJson(json).Single();
        Assert.Equal(EquipmentRarity.Legendary, item.Rarity);
        Assert.Equal(8, item.BonusFor(EquipStat.Hp));
        Assert.True(item.GrantsTrait(Trait.Rempart));
    }

    [Fact]
    public void Catalog_FromJson_EnemyAllowed_DefaultsTrue_AndReadsFalse()
    {
        const string json = """
        { "equipments": [
            { "id": "libre",   "name": "Libre",   "rarity": "Common", "stat": "Hp", "amount": 5 },
            { "id": "reserve", "name": "Réservé", "rarity": "Common", "stat": "Hp", "amount": 5, "enemyAllowed": false }
        ] }
        """;

        var list = EquipmentCatalog.FromJson(json);
        Assert.True(list[0].EnemyAllowed);    // champ absent → défaut true
        Assert.False(list[1].EnemyAllowed);   // explicitement interdit à l'IA
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

    [Fact]
    public void ShippedEquipment_Parses_AndEnemyFlagIsUsedBothWays()
    {
        // Charge le VRAI equipment.json (valide JSON + commentaires + accents). Le catalogue livré n'est plus
        // « tout autorisé à l'IA » : une partie des objets est RÉSERVÉE au joueur (enemyAllowed=false, ex. la
        // Lance ou le Soin). On vérifie donc que le drapeau est bien présent dans les DEUX sens plutôt qu'une
        // valeur figée, qui casserait à chaque arbitrage d'équilibrage.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config", "equipment.json");

        var list = EquipmentCatalog.FromJson(System.IO.File.ReadAllText(path));
        Assert.NotEmpty(list);
        Assert.Contains(list, e => e.EnemyAllowed);    // l'IA a de quoi s'équiper
        Assert.Contains(list, e => !e.EnemyAllowed);   // et certains objets restent au joueur seul
    }

    /// <summary>
    /// Chaque équipement livré doit avoir un NOM localisé dans strings.csv, sous <c>equip.&lt;id&gt;</c> ou, à
    /// défaut, sous la clé partagée par ses variantes de rareté (<c>equip.&lt;id sans Rare/Legendaire&gt;</c> —
    /// même règle que <c>UI.EquipmentNames</c>). Sans ce test l'oubli est INVISIBLE : le jeu retombe sur le nom
    /// FRANÇAIS brut d'equipment.json, donc l'objet reste lisible en français et n'est jamais traduit ailleurs
    /// (c'est ce qui était arrivé à la queue de phénix et à la fronde de David).
    /// </summary>
    [Fact]
    public void ShippedEquipment_AllHaveALocalizedName()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var assets = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config");

        var list = EquipmentCatalog.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(assets, "equipment.json")));
        var keys = new HashSet<string>();
        foreach (var line in System.IO.File.ReadAllLines(System.IO.Path.Combine(assets, "strings.csv")))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            var comma = line.IndexOf(',');
            if (comma > 0)
                keys.Add(line[..comma].Trim());
        }

        // Même dérivation que UI.EquipmentNames.BaseId (le projet Game n'est pas référencé ici).
        static string BaseId(string id) =>
            id.EndsWith("Legendaire", StringComparison.Ordinal) ? id[..^"Legendaire".Length]
            : id.EndsWith("Rare", StringComparison.Ordinal) ? id[..^"Rare".Length]
            : id;

        foreach (var e in list)
            Assert.True(keys.Contains("equip." + e.Id) || keys.Contains("equip." + BaseId(e.Id)),
                $"nom non traduit pour l'équipement '{e.Id}' ({e.Name}) : ajouter equip.{BaseId(e.Id)} dans strings.csv.");
    }

    /// <summary>
    /// Chaque TRAIT octroyé par un équipement livré doit être un trait CONNU du moteur (<see cref="Trait.All"/>),
    /// à l'accent près. Le chargeur d'équipement prend la chaîne telle quelle et <c>Unit.HasTrait</c> compare par
    /// ÉGALITÉ STRICTE : un « Seisme » sans accent ne correspond à aucun trait et l'objet n'accorde RIEN, sans
    /// la moindre erreur — c'est exactement ce qui neutralisait le marteau tellurique et le sceptre de tempête.
    /// </summary>
    [Fact]
    public void ShippedEquipment_OnlyGrantsKnownTraits()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config", "equipment.json");

        var list = EquipmentCatalog.FromJson(System.IO.File.ReadAllText(path));
        foreach (var e in list)
            foreach (var t in e.Traits)
                Assert.True(Trait.All.Contains(t),
                    $"trait inconnu sur l'équipement '{e.Id}' : \"{t}\" (accent ou orthographe — cf. Trait.All).");
    }

    [Fact]
    public void Roll_Filter_HardExcludesDisallowedEquipment()
    {
        // Filtre = celui que RollEnemyEquipment applique (e => e.EnemyAllowed). Un pool réduit à des items
        // interdits à l'IA renvoie null (jamais donné à l'ennemi), alors que le tirage NON filtré le sort.
        try
        {
            Equipments.Load(new[]
            {
                Equipment.OfStat("reserve", "Réservé", EquipStat.Hp, 5, enemyAllowed: false),
            });
            Assert.NotNull(Equipments.Roll(EquipmentRarity.Common, new Random(1)));                              // sans filtre : dispo
            Assert.Null(Equipments.Roll(EquipmentRarity.Common, new Random(1), filter: e => e.EnemyAllowed));    // filtré : exclu
        }
        finally { Equipments.ResetToDefaults(); }
    }

    [Fact]
    public void RollChestEquipment_LessLikelyForItemsAlreadyOwnedTwice()
    {
        try
        {
            var dup = Equipment.OfStat("dup", "Doublon", EquipStat.Hp, 1);
            var uniq = Equipment.OfStat("uniq", "Unique", EquipStat.Hp, 1);
            Equipments.Load(new[] { dup, uniq });        // 2 items communs

            var run = RunWith(Soldat());
            run.AddEquipment(dup);                        // possédé 2× → doit devenir rare au coffre
            run.AddEquipment(dup);

            var rng = new Random(1234);
            int dupHits = 0, uniqHits = 0;
            for (var i = 0; i < 600; i++)
                if (run.RollChestEquipment(rng)!.Id == "dup") dupHits++; else uniqHits++;

            // Poids 0.25 (doublon) vs 1 (autre) → l'unique sort NETTEMENT plus souvent, mais le doublon reste possible.
            Assert.True(uniqHits > dupHits * 2, $"uniq={uniqHits} dup={dupHits} : le doublon doit être bien plus rare");
            Assert.True(dupHits > 0, "le doublon doit rester possible (jamais exclu)");
        }
        finally { Equipments.ResetToDefaults(); }
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
    public void Equip_TraitAlreadyOnClass_IsNowAllowed_TraitDoesNotStack()
    {
        // Le Garde (domaine Tour) a nativement Rempart. Un équipement de trait Rempart est désormais AUTORISÉ
        // (le trait ne s'empile pas : aucun effet supplémentaire, mais l'objet est bien porté).
        var garde = Domaines.Tour.BaseClass.Evolutions[0]; // Garde (Rempart)
        var run = RunWith(new UnitSpec(Domaine.Tour, garde));
        var g = run.Roster.First(u => !u.Essential);
        var rempart = RempartEquip;
        run.AddEquipment(rempart);

        Assert.True(run.CanEquip(g, rempart));
        Assert.True(run.Equip(g, rempart));
        Assert.Equal(rempart, g.Equipment);
        Assert.DoesNotContain(rempart, run.EquipmentInventory);   // consommé (posé sur le pion)

        // Un Soldat (sans Rempart) l'accepte aussi, comme avant.
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
    public void Equip_AttaqueLibre_ForbiddenOnDameDomaine_AllowedElsewhere()
    {
        Equipment Viseur() => Equipment.OfTrait("viseur", "Viseur", Trait.AttaqueLibre);

        // Domaine DAME : tire déjà comme une Dame → « Attaque libre » refusé (redondant).
        var run = RunWith(Soldat());   // Soldat = domaine Dame
        var dame = run.Roster.First(u => !u.Essential);
        var v1 = Viseur();
        run.AddEquipment(v1);
        Assert.False(run.CanEquip(dame, v1));
        Assert.False(run.Equip(dame, v1));
        Assert.Null(dame.Equipment);
        Assert.Contains(v1, run.EquipmentInventory);   // pas consommé

        // Hors domaine Dame (Tour) : accepté.
        var run2 = RunWith(new UnitSpec(Domaine.Tour, Domaines.Tour.BaseClass));
        var tour = run2.Roster.First(u => !u.Essential);
        var v2 = Viseur();
        run2.AddEquipment(v2);
        Assert.True(run2.CanEquip(tour, v2));
        Assert.True(run2.Equip(tour, v2));
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
