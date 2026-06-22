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

    // Domaines piochables pour les VAGUES ENNEMIES. Limité à pion (Soldat) + lancier (Tour)
    // pour l'instant (réglage de difficulté temporaire). Le recrutement, lui, propose
    // désormais les ennemis VAINCUS (voir BuildDraft), plus cette pioche.
    private static readonly Domaine[] Pool = { Domaine.Pion, Domaine.Tour };

    private readonly Random _rng;
    private readonly List<UnitSpec> _roster = new();
    private readonly List<UnitSpec> _draft = new();

    public Run(int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
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
    /// Terrain du combat courant : herbe + obstacles (eau/montagne) aléatoires dans la zone neutre,
    /// symétriques. Tiré du RNG du run → varie d'un combat à l'autre, reproductible si seed fixé.
    /// </summary>
    public Battlefield BuildBattlefield(int width, int height) =>
        TerrainGenerator.Generate(width, height, _rng);

    /// <summary>Vague ennemie du combat courant (le placement est assuré par la scène).</summary>
    public List<UnitSpec> BuildEnemyWave()
    {
        var wave = new List<UnitSpec>();
        if (IsBossCombat)
        {
            wave.Add(ToSpec(Commandes.Boss));
            wave.Add(RandomEnemy());
            wave.Add(RandomEnemy());
        }
        else
        {
            var count = CombatNumber + 1; // combat 1 = 2 ennemis … combat 5 = 6 ennemis
            for (var i = 0; i < count; i++)
                wave.Add(RandomEnemy());
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

    private UnitSpec RandomEnemy()
    {
        var domaine = Pool[_rng.Next(Pool.Length)];
        return new UnitSpec(domaine, Domaines.Of(domaine).BaseClass);
    }

    /// <summary>Convertit une définition COMMANDE en gabarit essentiel (mouvement = son domaine).</summary>
    private static UnitSpec ToSpec(CommandeDef def) =>
        new(def.Movement, def.BaseClass, essential: true);
}
