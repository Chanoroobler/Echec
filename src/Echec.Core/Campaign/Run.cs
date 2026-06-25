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

    // ORDRE D'INTRODUCTION des types ennemis : un nouveau type est débloqué à chaque combat —
    // Soldat (Pion), Lancier (Tour), Cavalier (Cavalier), Archer (Dame), Mage (Fou). Le Cavalier
    // arrive TÔT (rapide : move 3 + saut) pour varier la menace ; le Mage (le plus punitif, one-shot
    // à portée 3) est introduit EN DERNIER, le temps que le joueur apprenne. Au combat N (1..5) le
    // pool = les N premiers types, et le N-ième (« frais ») est garanti d'apparaître ; le combat de
    // boss débloque tout le pool. Comme le recrutement propose les ennemis VAINCUS (voir BuildDraft),
    // ces domaines deviennent jouables au fil des déblocages.
    private static readonly Domaine[] IntroOrder =
        { Domaine.Pion, Domaine.Tour, Domaine.Cavalier, Domaine.Dame, Domaine.Fou };

    /// <summary>Nombre d'escortes accompagnant le boss (tirées parmi tous les types débloqués).</summary>
    private const int BossEscorts = 4;

    /// <summary>Taille de la vague ennemie par combat NON-boss (index 0 = combat 1) : 2,3,4,4,5.</summary>
    private static readonly int[] EnemyCounts = { 2, 3, 4, 4, 5 };

    private readonly List<UnitSpec> _roster = new();
    private readonly List<UnitSpec> _draft = new();

    /// <summary>
    /// Graine de la run, SAUVEGARDÉE. La vague ennemie et le terrain de chaque combat en dérivent de
    /// façon déterministe (cf. <see cref="CombatRng"/>) : « Continuer » régénère donc EXACTEMENT le
    /// même combat (mêmes ennemis, même terrain) qu'avant de quitter.
    /// </summary>
    public int Seed { get; private set; }

    /// <summary>
    /// Vrai = TOUTE PREMIÈRE campagne du joueur : déblocage des types ennemis plus doux (combat 1 =
    /// soldats seuls, tout débloqué au combat 5). Faux = campagnes suivantes : départ soldat+lancier,
    /// tout débloqué dès le combat 4. Persisté dans <see cref="RunSave"/> pour qu'une reprise garde le
    /// même rythme. Cf. <see cref="BuildEnemyWave"/>.
    /// </summary>
    public bool FirstRun { get; private set; }

    public Run(int? seed = null, bool firstRun = false)
    {
        Seed = seed ?? new Random().Next();
        FirstRun = firstRun;
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
    public static Run Restore(IReadOnlyList<UnitSpec> roster, int combatNumber, int seed, bool firstRun)
    {
        var run = new Run(seed, firstRun);
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

    /// <summary>
    /// Vague ennemie du combat courant (le placement est assuré par la scène). Déblocage progressif :
    /// un nouveau type par combat, dans l'ordre de <see cref="IntroOrder"/>. La PREMIÈRE campagne
    /// (<see cref="FirstRun"/>) démarre soldat seul (tout débloqué au combat 5) ; les suivantes
    /// démarrent soldat+lancier (tout débloqué dès le combat 4). Le type fraîchement débloqué CE
    /// combat est garanti d'apparaître ; le reste est tiré au hasard parmi le pool débloqué. Le combat
    /// de boss = boss + <see cref="BossEscorts"/> escortes tirées parmi TOUS les types.
    /// </summary>
    public List<UnitSpec> BuildEnemyWave()
    {
        var rng = CombatRng(1);   // RNG déterministe propre à la vague de CE combat
        var wave = new List<UnitSpec>();

        if (IsBossCombat)
        {
            wave.Add(ToSpec(Commandes.Boss));
            for (var i = 0; i < BossEscorts; i++)
                wave.Add(RandomEnemy(rng, IntroOrder.Length));
            return wave;
        }

        // Campagnes suivantes : +1 type d'avance dès le départ (soldat+lancier au combat 1).
        var reach = FirstRun ? CombatNumber : CombatNumber + 1;
        var unlocked = Math.Min(reach, IntroOrder.Length);          // nb de types disponibles
        var freshlyUnlocked = reach <= IntroOrder.Length;           // un type vient d'être débloqué ?
        var count = EnemyCounts[CombatNumber - 1];                  // 2,3,4,4,5 (combats 1..5)

        // Si un type est fraîchement débloqué ce combat, on en garantit une unité (le dernier du pool).
        if (freshlyUnlocked)
        {
            var fresh = IntroOrder[unlocked - 1];
            wave.Add(new UnitSpec(fresh, Domaines.Of(fresh).BaseClass));
        }
        // Le reste : au hasard parmi les types débloqués.
        for (var i = wave.Count; i < count; i++)
            wave.Add(RandomEnemy(rng, unlocked));

        Shuffle(wave, rng);   // pour que le type frais ne soit pas toujours à la même position
        return wave;
    }

    /// <summary>Fin du placement : on passe au combat.</summary>
    public void StartBattle()
    {
        if (Phase == RunPhase.Placement)
            Phase = RunPhase.Battle;
    }

    /// <summary>Repasse en phase de placement SANS avancer le combat (fin du tutoriel → combat 1).</summary>
    public void ReturnToPlacement() => Phase = RunPhase.Placement;

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

    /// <summary>Ennemi au hasard parmi les <paramref name="poolSize"/> premiers types débloqués.</summary>
    private static UnitSpec RandomEnemy(Random rng, int poolSize)
    {
        var domaine = IntroOrder[rng.Next(poolSize)];
        return new UnitSpec(domaine, Domaines.Of(domaine).BaseClass);
    }

    /// <summary>Mélange en place (Fisher-Yates) avec le RNG déterministe du combat.</summary>
    private static void Shuffle(List<UnitSpec> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
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
