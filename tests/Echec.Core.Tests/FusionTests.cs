using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Xunit;

namespace Echec.Core.Tests;

/// <summary>
/// Cœur du système de FUSION (phase de placement) : 3 unités d'une même classe → 1 évolution choisie.
/// Voir <see cref="Run"/> (CanFuse / FusionOptions / Fuse). L'UI (réserve/plateau) est testée à part.
/// </summary>
public class FusionTests
{
    // Construit une run EN PLACEMENT avec un roster contrôlé (via Restore, sans API de test dédiée).
    private static Run RunWith(params UnitSpec[] units) =>
        Run.Restore(units.ToList(), combatNumber: 1, seed: 1, firstRun: false);

    private static UnitSpec Soldat() => new(Domaine.Pion, Domaines.Pion.BaseClass);
    private static UnitSpec Mage() => new(Domaine.Fou, Domaines.Fou.BaseClass);

    [Fact]
    public void ThreeIdentical_CanFuse_AndOffersTwoEvolutions()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);

        Assert.True(run.CanFuse(soldat));
        var options = run.FusionOptions(soldat);
        Assert.Equal(Domaines.Pion.BaseClass.Evolutions, options); // Garde / Spadassin
        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void TwoIdentical_CannotFuse()
    {
        var run = RunWith(Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);

        Assert.False(run.CanFuse(soldat));
        Assert.Empty(run.FusionOptions(soldat));
        Assert.Null(run.Fuse(soldat, Domaines.Pion.BaseClass.Evolutions[0]));
    }

    [Fact]
    public void DifferentClasses_DoNotCountTogether()
    {
        // 2 soldats + 1 mage : aucune classe n'atteint 3 exemplaires.
        var run = RunWith(Soldat(), Soldat(), Mage());
        Assert.False(run.CanFuse(run.Roster.First(u => u.Domaine == Domaine.Pion && !u.Essential)));
        Assert.False(run.CanFuse(run.Roster.First(u => u.Domaine == Domaine.Fou)));
    }

    [Fact]
    public void Fuse_ConsumesThree_AddsChosenEvolution()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat(), Mage());
        var soldat = run.Roster.First(u => u.Domaine == Domaine.Pion && !u.Essential);
        var spadassin = Domaines.Pion.BaseClass.Evolutions[1]; // Spadassin

        var before = run.Roster.Count;
        var fused = run.Fuse(soldat, spadassin);

        Assert.NotNull(fused);
        Assert.Equal(spadassin, fused!.UnitClass);
        Assert.Equal(Domaine.Pion, fused.Domaine);
        Assert.Equal(before - 2, run.Roster.Count);                       // -3 +1
        Assert.Equal(0, run.Roster.Count(u => Run.SameClass(u, soldat))); // plus aucun soldat de base
        Assert.Contains(run.Roster, u => u.UnitClass == spadassin);
        Assert.Contains(run.Roster, u => u.Domaine == Domaine.Fou);       // le mage est intact
    }

    [Fact]
    public void Fuse_OnlyConsumesThree_WhenMoreCopiesExist()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);

        run.Fuse(soldat, Domaines.Pion.BaseClass.Evolutions[0]);

        Assert.Equal(1, run.Roster.Count(u => u.UnitClass == Domaines.Pion.BaseClass)); // 4 - 3 = 1
        Assert.Single(run.Roster, u => u.UnitClass == Domaines.Pion.BaseClass.Evolutions[0]);
    }

    [Fact]
    public void Commander_NeverFuses_EvenWithTwoSoldiers()
    {
        // Roster de départ : commandant + 2 soldats. Le commandant (Pion essentiel) n'est jamais fusable,
        // et il ne compte pas dans les exemplaires des soldats.
        var run = new Run(seed: 1);
        Assert.False(run.CanFuse(run.Commander));
    }

    [Fact]
    public void LeafClass_CannotFuse()
    {
        // 3 Gardes (tier 2, feuille) : pas d'évolution disponible → fusion refusée (tier 3 plus tard).
        var garde = Domaines.Pion.BaseClass.Evolutions[0];
        var run = RunWith(
            new UnitSpec(Domaine.Pion, garde),
            new UnitSpec(Domaine.Pion, garde),
            new UnitSpec(Domaine.Pion, garde));
        var g = run.Roster.First(u => !u.Essential);

        Assert.True(garde.IsLeaf);
        Assert.False(run.CanFuse(g));
        Assert.Empty(run.FusionOptions(g));
    }

    [Fact]
    public void Fuse_RejectsEvolutionForeignToTheTree()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var foreign = Domaines.Fou.BaseClass.Evolutions[0]; // Clerc : pas une évolution du Soldat

        Assert.Null(run.Fuse(soldat, foreign));
        Assert.Equal(3, run.Roster.Count(u => u.UnitClass == Domaines.Pion.BaseClass)); // rien consommé
    }

    [Fact]
    public void Fuse_OnlyDuringPlacement()
    {
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);

        run.StartBattle(); // on n'est plus en placement
        Assert.False(run.CanFuse(soldat));
        Assert.Null(run.Fuse(soldat, Domaines.Pion.BaseClass.Evolutions[0]));
    }

    [Fact]
    public void FusedUnit_IsLeaf_AndCannotChainFuse_InV1()
    {
        // Après fusion vers un tier 2 (feuille), même avec 3 exemplaires on ne peut pas re-fusionner.
        var garde = Domaines.Pion.BaseClass.Evolutions[0];
        var run = RunWith(
            new UnitSpec(Domaine.Pion, garde),
            new UnitSpec(Domaine.Pion, garde),
            new UnitSpec(Domaine.Pion, garde));

        Assert.False(run.CanFuse(run.Roster.First(u => !u.Essential)));
    }

    [Fact]
    public void FuseGroup_ConsumesTheExactGivenInstances()
    {
        // 4 soldats : on fusionne 3 instances PRÉCISES, la 4e (non listée) reste intacte.
        var run = RunWith(Soldat(), Soldat(), Soldat(), Soldat());
        var soldiers = run.Roster.Where(u => !u.Essential).ToList();
        var group = soldiers.Take(3).ToList();
        var survivor = soldiers[3];

        var fused = run.Fuse(group, Domaines.Pion.BaseClass.Evolutions[0]);

        Assert.NotNull(fused);
        Assert.Contains(survivor, run.Roster);                       // l'instance non listée survit
        Assert.DoesNotContain(group[0], run.Roster);
        Assert.DoesNotContain(group[2], run.Roster);
    }

    [Fact]
    public void FuseGroup_RejectsDuplicateInstance()
    {
        // Même instance répétée 3 fois : pas 3 instances distinctes → refusé, roster intact.
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var one = run.Roster.First(u => !u.Essential);

        Assert.Null(run.Fuse(new[] { one, one, one }, Domaines.Pion.BaseClass.Evolutions[0]));
        Assert.Equal(3, run.Roster.Count(u => !u.Essential));
    }

    [Fact]
    public void FuseGroup_RejectsInstanceForeignToRoster()
    {
        var run = RunWith(Soldat(), Soldat());
        var inRoster = run.Roster.Where(u => !u.Essential).ToList();
        var foreign = Soldat(); // instance jamais ajoutée au roster

        Assert.Null(run.Fuse(new[] { inRoster[0], inRoster[1], foreign }, Domaines.Pion.BaseClass.Evolutions[0]));
        Assert.Equal(2, run.Roster.Count(u => !u.Essential));
    }

    [Fact]
    public void Fusion_SurvivesSaveRoundTrip_WhenCommitted()
    {
        // Une fois la fusion faite et la run sauvegardée (ce que fait la scène au placement suivant),
        // l'unité évoluée se reconstruit à l'identique depuis la sauvegarde.
        var run = RunWith(Soldat(), Soldat(), Soldat());
        var soldat = run.Roster.First(u => !u.Essential);
        var spadassin = Domaines.Pion.BaseClass.Evolutions[1];
        run.Fuse(soldat, spadassin);

        var restored = RunSave.From(run).ToRun();

        Assert.Contains(restored.Roster, u => u.UnitClass == spadassin);
    }
}
