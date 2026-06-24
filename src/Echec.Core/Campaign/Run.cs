using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Map;

namespace Echec.Core.Campaign;

/// <summary>Phase courante de la boucle de jeu.</summary>
public enum RunPhase
{
    Placement,    // on déploie ses unités avant le combat
    Battle,       // combat en cours (géré par le Match)
    Recruitment,  // après une victoire : on choisit une unité
    Victory,      // boss vaincu : campagne gagnée
    Defeat        // commandant tombé ou armée détruite
}

/// <summary>
/// État de la campagne (première boucle de gameplay) : inventaire du joueur,
/// numéro de combat, génération des vagues ennemies (difficulté croissante) et du
/// draft de recrutement. Boucle : Placement → Battle → Recruitment → Placement … ;
/// au <see cref="TotalCombats"/>e combat, c'est le combat de BOSS (tuer le boss).
///
/// Persistance : permadeath (les unités mortes quittent l'inventaire) + soin complet
/// (les survivantes reviennent à PV pleins, via un nouveau Spawn au combat suivant).
/// </summary>
public sealed class Run
{
    public const int TotalCombats = 6;
    public const int DraftSize = 3;

    // Domaines piochables pour les VAGUES ENNEMIES : Soldat (Pion), Lancier (Tour), Mage (Fou),
    // Archer (Dame) et Cavalier (Cavalier). Comme le recrutement propose les ennemis VAINCUS
    // (voir BuildDraft), tous ces domaines deviennent jouables côté joueur.
    private static readonly Domaine[] Pool =
        { Domaine.Pion, Domaine.Tour, Domaine.Fou, Domaine.Dame, Domaine.Cavalier };

    private readonly List<UnitSpec> _roster = new();
    private readonly List<UnitSpec> _draft = new();

    /// <summary>
    /// Graine de la run, SAUVEGARDÉE. La vague ennemie et le terrain de chaque combat en dérivent de
    /// façon déterministe (cf. <see cref="CombatRng"/>) : « Continuer » régénère donc EXACTEMENT le
    /// même combat (mêmes ennemis, même terrain) qu'avant de quitter.
    /// </summary>
    public int Seed { get; private set; }

    public Run(int? seed = null)
    {
        Seed = seed ?? new Random().Next();
        Reset();
    }

    /// <summary>Inventaire du joueur (commandant inclus).</summary>
    public IReadOnlyList<UnitSpec> Roster => _roster;

    /// <summary>Les 3 options de recrutement (vides hors phase de recrutement).</summary>
    public IReadOnlyList<UnitSpec> Draft => _draft;

    public int CombatNumber { get; private set; }
    public RunPhase Phase { get; private set; }

    public bool IsBossCombat => CombatNumber == TotalCombats;
    public UnitSpec Commander => _roster.First(u => u.Essential);

    /// <summary>(Re)démarre une campagne : commandant + 2 soldats, combat 1.</summary>
    public void Reset()
    {
        _roster.Clear();
        _roster.Add(ToSpec(Commandes.Commander));
        _roster.Add(new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass));
        _roster.Add(new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass));
        _draft.Clear();
        CombatNumber = 1;
        Phase = RunPhase.Placement;
    }

    /// <summary>
    /// Reconstruit une run à partir d'une sauvegarde (inventaire + numéro de combat). La run reprend
    /// en phase de PLACEMENT : la vague ennemie et le terrain sont regénérés au combat courant (la
    /// sauvegarde n'a lieu qu'en placement, donc aucun état de combat / de recrutement à restaurer).
    /// </summary>
    public static Run Restore(IReadOnlyList<UnitSpec> roster, int combatNumber, int seed)
    {
        var run = new Run(seed);
        run._roster.Clear();
        run._roster.AddRange(roster);
        run.CombatNumber = combatNumber;
        run.Phase = RunPhase.Placement;
        run._draft.Clear();
        return run;
    }

    /// <summary>
    /// Terrain du combat courant : herbe + obstacles (eau/montagne) aléatoires dans la zone neutre,
    /// symétriques. Tiré du RNG du run → varie d'un combat à l'autre, reproductible si seed fixé.
    /// </summary>
    public Battlefield BuildBattlefield(int width, int height) =>
        TerrainGenerator.Generate(width, height, CombatRng(0));

    /// <summary>Vague ennemie du combat courant (le placement est assuré par la scène).</summary>
    public List<UnitSpec> BuildEnemyWave()
    {
        var rng = CombatRng(1);   // RNG déterministe propre à la vague de CE combat
        var wave = new List<UnitSpec>();
        if (IsBossCombat)
        {
            wave.Add(ToSpec(Commandes.Boss));
            wave.Add(RandomEnemy(rng));
            wave.Add(RandomEnemy(rng));
        }
        else
        {
            var count = CombatNumber + 1; // combat 1 = 2 ennemis … combat 5 = 6 ennemis
            for (var i = 0; i < count; i++)
                wave.Add(RandomEnemy(rng));
        }
        return wave;
    }

    /// <summary>Fin du placement : on passe au combat.</summary>
    public void StartBattle()
    {
        if (Phase == RunPhase.Placement)
            Phase = RunPhase.Battle;
    }

    /// <summary>
    /// Combat gagné. <paramref name="casualties"/> = gabarits du roster morts pendant le combat
    /// (retirés : permadeath). <paramref name="defeatedEnemies"/> = ennemis vaincus DANS L'ORDRE de
    /// leur mort ; le recrutement propose les 3 derniers (le boss n'y figure jamais). Combat de boss
    /// → victoire ; sinon → recrutement.
    /// </summary>
    public void CompleteCombat(IEnumerable<UnitSpec> casualties, IReadOnlyList<UnitSpec> defeatedEnemies)
    {
        var dead = new HashSet<UnitSpec>(casualties);
        _roster.RemoveAll(u => !u.Essential && dead.Contains(u));

        if (IsBossCombat)
        {
            Phase = RunPhase.Victory;
            return;
        }

        BuildDraft(defeatedEnemies);
        Phase = RunPhase.Recruitment;
    }

    /// <summary>Ajoute l'unité choisie à l'inventaire et lance le placement du combat suivant.</summary>
    public void Recruit(UnitSpec choice)
    {
        if (Phase != RunPhase.Recruitment)
            return;

        _roster.Add(new UnitSpec(choice.Domaine, choice.UnitClass));
        _draft.Clear();
        CombatNumber++;
        Phase = RunPhase.Placement;
    }

    public void Defeat() => Phase = RunPhase.Defeat;

    /// <summary>
    /// Recrutement = les <see cref="DraftSize"/> DERNIERS ennemis vaincus (dans l'ordre de leur mort),
    /// ou moins s'il y en a eu moins. Doublons conservés (reflète les pièces réellement abattues).
    /// Les gabarits sont posés tels quels ; ils s'affichent et se recrutent côté joueur (bleu).
    /// </summary>
    private void BuildDraft(IReadOnlyList<UnitSpec> defeatedEnemies)
    {
        _draft.Clear();
        var start = Math.Max(0, defeatedEnemies.Count - DraftSize);
        for (var i = start; i < defeatedEnemies.Count; i++)
            _draft.Add(defeatedEnemies[i]);
    }

    private static UnitSpec RandomEnemy(Random rng)
    {
        var domaine = Pool[rng.Next(Pool.Length)];
        return new UnitSpec(domaine, Domaines.Of(domaine).BaseClass);
    }

    /// <summary>
    /// RNG DÉTERMINISTE pour le combat courant, dérivé de (<see cref="Seed"/>, <see cref="CombatNumber"/>,
    /// <paramref name="salt"/>) — stable d'une session à l'autre (pas de <c>HashCode.Combine</c> qui
    /// varie par process). <paramref name="salt"/> sépare terrain (0) et vague ennemie (1).
    /// </summary>
    private Random CombatRng(int salt) =>
        new(unchecked(Seed * 6151 + CombatNumber * 1031 + salt));

    /// <summary>Convertit une définition COMMANDE en gabarit essentiel (mouvement = son domaine).</summary>
    private static UnitSpec ToSpec(CommandeDef def) =>
        new(def.Movement, def.BaseClass, essential: true);
}
