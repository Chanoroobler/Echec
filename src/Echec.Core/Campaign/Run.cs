using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Equip;
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

    /// <summary>Taille de la vague ennemie par combat NON-boss (index 0 = combat 1) : 2,3,3,4,4.</summary>
    private static readonly int[] EnemyCounts = { 2, 3, 3, 4, 4 };

    private readonly List<UnitSpec> _roster = new();
    private readonly List<UnitSpec> _draft = new();

    /// <summary>
    /// Équipements POSSÉDÉS mais NON équipés (inventaire de la run). Les équipements équipés vivent sur
    /// leur <see cref="UnitSpec"/> (collés au pion). Alimenté par les coffres, vidé en posant un équipement.
    /// </summary>
    private readonly List<Equipment> _equipment = new();

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

    /// <summary>Équipements possédés mais non équipés (inventaire). Posables sur les pions en phase Équipement.</summary>
    public IReadOnlyList<Equipment> EquipmentInventory => _equipment;

    /// <summary>
    /// Vrai si le joueur possède au moins un équipement — en inventaire OU déjà posé sur un pion (sinon
    /// la phase Équipement est sautée). On l'ouvre même si tout est équipé, pour pouvoir réagencer/retirer.
    /// </summary>
    public bool HasEquipment => _equipment.Count > 0 || _roster.Any(u => u.Equipment != null);

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
        _equipment.Clear();
        CombatNumber = 1;
        Phase = RunPhase.Placement;
    }

    /// <summary>
    /// Reconstruit une run à partir d'une sauvegarde (inventaire + numéro de combat). La run reprend
    /// en phase de PLACEMENT : la vague ennemie et le terrain sont regénérés au combat courant (la
    /// sauvegarde n'a lieu qu'en placement, donc aucun état de combat / de recrutement à restaurer).
    /// </summary>
    public static Run Restore(IReadOnlyList<UnitSpec> roster, int combatNumber, int seed, bool firstRun,
        IReadOnlyList<Equipment>? inventory = null)
    {
        var run = new Run(seed, firstRun);
        run._roster.Clear();
        run._roster.AddRange(roster);
        run._equipment.Clear();
        if (inventory != null)
            run._equipment.AddRange(inventory);
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
    /// <summary>
    /// Tire un pion tier 1 ALÉATOIRE parmi les domaines dont la classe de base a DÉJÀ ÉTÉ VUE
    /// (<paramref name="isSeen"/> = méta-progression : asset déjà rencontré dans une run), SANS l'ajouter à
    /// l'armée — l'appelant l'ajoute via <see cref="AddUnit"/> (ex. tuile recrue). AUCUN gating par combat :
    /// n'importe quel tier 1 déjà vu peut sortir à tout moment. Profil neuf (rien de vu) : repli sur le Pion.
    /// (Tier 2+ selon la progression : à venir.)
    /// </summary>
    public UnitSpec RollSeenTier1(Random rng, Func<string, bool> isSeen)
    {
        var pool = IntroOrder.Where(d => isSeen(Domaines.Of(d).BaseClass.Asset)).ToList();
        var domaine = pool.Count > 0 ? pool[rng.Next(pool.Count)] : Domaine.Pion;
        return new UnitSpec(domaine, Domaines.Of(domaine).BaseClass);
    }

    /// <summary>
    /// Assets des classes de base tier 1 DÉBLOQUÉES au combat courant : à marquer « vues » (méta-progression)
    /// quand la vague apparaît, pour que la tuile recrue puisse les proposer ensuite à tout moment.
    /// </summary>
    public IEnumerable<string> UnlockedTier1Assets()
    {
        var reach = FirstRun ? CombatNumber : CombatNumber + 1;
        var unlocked = Math.Min(reach, IntroOrder.Length);
        for (var i = 0; i < unlocked; i++)
            yield return Domaines.Of(IntroOrder[i]).BaseClass.Asset;
    }

    /// <summary>Ajoute un pion à l'armée (réserve). Utilisé hors recrutement (ex. récompense d'une tuile recrue).</summary>
    public void AddUnit(UnitSpec spec) => _roster.Add(spec);

    // ─── ÉQUIPEMENT ──────────────────────────────────────────────────────────────────────────────
    // Un équipement est « collé au pion » : posé sur un UnitSpec, il le suit d'un combat à l'autre et
    // disparaît avec lui (permadeath — voir CompleteCombat). L'inventaire (_equipment) ne contient que les
    // équipements NON équipés. La fusion rend les équipements des 3 pions à l'inventaire (l'évolution sort nue).

    /// <summary>Ajoute un équipement à l'inventaire (ex. butin d'un coffre).</summary>
    public void AddEquipment(Equipment equipment) => _equipment.Add(equipment);

    /// <summary>Retire un exemplaire d'équipement de l'inventaire (faux s'il n'y en a aucun).</summary>
    public bool RemoveEquipment(Equipment equipment) => _equipment.Remove(equipment);

    /// <summary>
    /// Vrai si <paramref name="spec"/> peut recevoir <paramref name="equipment"/> : pion non essentiel
    /// (le commandant ne s'équipe jamais) et — pour un équipement de TRAIT — un pion dont la CLASSE ne
    /// possède PAS déjà ce trait (pas de doublon de trait). Restrictions du domaine Cavalier (monté) :
    /// objet de PORTÉE refusé aux cavaliers de mêlée (sauf archer monté), objet de MOUVEMENT refusé à
    /// TOUS les cavaliers. Les autres équipements de stat passent toujours.
    /// </summary>
    public bool CanEquip(UnitSpec spec, Equipment equipment)
    {
        if (spec.Essential)
            return false;
        if (equipment.Kind == EquipmentKind.Trait && equipment.Trait is { } t && ClassHasTrait(spec.UnitClass, t))
            return false;

        // Le domaine Cavalier (monté) refuse deux familles d'objets :
        if (spec.Domaine == Domaine.Cavalier)
        {
            // • PORTÉE (arc) : aucun sens sur un cavalier de mêlée (lance/épée à cheval) — mais OK pour
            //   l'archer monté, déjà un tireur, repéré par sa zone morte de près (MinAttackRange > 1).
            if (equipment.BonusFor(EquipStat.AttackRange) > 0 && spec.UnitClass.MinAttackRange <= 1)
                return false;
            // • MOUVEMENT (bottes) : la monture donne déjà la mobilité — interdit à TOUS les cavaliers,
            //   sans exception (l'archer monté non plus).
            if (equipment.BonusFor(EquipStat.MoveRange) > 0)
                return false;
        }
        return true;
    }

    /// <summary>Vrai si la classe possède NATIVEMENT ce trait (liste de traits, ou PiercesAllies pour « Traverse allié »).</summary>
    private static bool ClassHasTrait(UnitClass cls, string trait)
    {
        if (trait == Trait.TraverseAllie)
            return cls.PiercesAllies;
        return cls.Traits.Contains(trait);
    }

    /// <summary>
    /// Équipe <paramref name="spec"/> avec <paramref name="equipment"/> (pris dans l'inventaire) pendant
    /// le placement. Un seul équipement par pion ; le commandant n'en porte jamais ; un trait déjà présent
    /// sur la classe est refusé (cf. <see cref="CanEquip"/>). Si le pion en portait déjà un, l'ancien
    /// retourne à l'inventaire. Renvoie faux si la phase / le pion / l'item l'interdit.
    /// </summary>
    public bool Equip(UnitSpec spec, Equipment equipment)
    {
        if (Phase != RunPhase.Placement || !CanEquip(spec, equipment))
            return false;
        if (!_equipment.Remove(equipment))
            return false;
        if (spec.Equipment is { } old)
            _equipment.Add(old);
        spec.Equipment = equipment;
        return true;
    }

    /// <summary>Retire l'équipement de <paramref name="spec"/> et le rend à l'inventaire (sans effet s'il n'en a pas).</summary>
    public void Unequip(UnitSpec spec)
    {
        if (spec.Equipment is { } e)
        {
            _equipment.Add(e);
            spec.Equipment = null;
        }
    }

    public List<UnitSpec> BuildEnemyWave()
    {
        var rng = CombatRng(1);   // RNG déterministe propre à la vague de CE combat
        var wave = new List<UnitSpec>();

        if (IsBossCombat)
        {
            wave.Add(ToSpec(Commandes.Boss));
            // Escortes tirées entre elles avec plafond anti-triplon (le boss, pièce unique, ne compte pas).
            var escorts = new List<UnitSpec>();
            for (var i = 0; i < BossEscorts; i++)
                escorts.Add(RandomEnemy(rng, IntroOrder.Length, escorts));
            wave.AddRange(escorts);
            return wave;
        }

        // Campagnes suivantes : +1 type d'avance dès le départ (soldat+lancier au combat 1).
        var reach = FirstRun ? CombatNumber : CombatNumber + 1;
        var unlocked = Math.Min(reach, IntroOrder.Length);          // nb de types disponibles
        var freshlyUnlocked = reach <= IntroOrder.Length;           // un type vient d'être débloqué ?
        var count = EnemyCounts[CombatNumber - 1];                  // 2,3,3,4,4 (combats 1..5)

        // Si un type est fraîchement débloqué ce combat, on en garantit une unité (le dernier du pool).
        if (freshlyUnlocked)
        {
            var fresh = IntroOrder[unlocked - 1];
            wave.Add(new UnitSpec(fresh, Domaines.Of(fresh).BaseClass));
        }
        // Le reste : au hasard parmi les types débloqués, en évitant un 3e exemplaire d'un même type.
        for (var i = wave.Count; i < count; i++)
            wave.Add(RandomEnemy(rng, unlocked, wave));

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

    // ─── FUSION ────────────────────────────────────────────────────────────────────────────────
    // Pendant le PLACEMENT, fusionner FusionSize exemplaires d'une MÊME classe (même domaine + même
    // UnitClass) en 1 unité évoluée, choisie parmi les 2 évolutions de l'arbre. La fusion mute le
    // roster EN MÉMOIRE ; elle n'est PAS resauvegardée ici. Comme la progression n'est persistée
    // qu'au début de chaque phase de placement (côté scène), quitter avant de lancer le combat
    // annule la fusion (on revient au début du placement) ; lancer le combat la verrouille (elle
    // sera sauvegardée au placement du combat suivant). Permadeath : l'unité évoluée morte = les 3
    // exemplaires perdus. Les meneurs (commandant/boss, essentiels) ne fusionnent jamais. Une unité
    // déjà au sommet de son arbre (feuille) ne peut pas fusionner — l'arbre étant récursif, un futur
    // tier 3 réactiverait automatiquement la fusion une fois les évolutions ajoutées au JSON.

    /// <summary>Nombre d'exemplaires d'une même classe requis pour fusionner.</summary>
    public const int FusionSize = 3;

    /// <summary>
    /// Deux gabarits sont de la MÊME classe (donc fusionnables ensemble) s'ils partagent domaine et
    /// classe. Source unique de la règle d'« identité » (réutilisée par l'UI réserve/plateau).
    /// </summary>
    public static bool SameClass(UnitSpec a, UnitSpec b) =>
        a.Domaine == b.Domaine && a.UnitClass == b.UnitClass;

    /// <summary>Nombre d'exemplaires non-essentiels de la classe de <paramref name="spec"/> dans le roster.</summary>
    public int CountFusable(UnitSpec spec) =>
        _roster.Count(u => !u.Essential && SameClass(u, spec));

    /// <summary>
    /// Vrai si <paramref name="spec"/> peut amorcer une fusion : en placement, non essentiel, classe
    /// non-feuille (évolutions disponibles) et au moins <see cref="FusionSize"/> exemplaires en roster.
    /// </summary>
    public bool CanFuse(UnitSpec spec) =>
        Phase == RunPhase.Placement
        && !spec.Essential
        && !spec.UnitClass.IsLeaf
        && CountFusable(spec) >= FusionSize;

    /// <summary>Les évolutions proposées au choix pour fusionner <paramref name="spec"/> (vide si impossible).</summary>
    public IReadOnlyList<UnitClass> FusionOptions(UnitSpec spec) =>
        CanFuse(spec) ? spec.UnitClass.Evolutions : System.Array.Empty<UnitClass>();

    /// <summary>
    /// Réalise la fusion : retire <see cref="FusionSize"/> exemplaires de la classe de
    /// <paramref name="spec"/> et ajoute 1 unité de la classe <paramref name="evolution"/> choisie.
    /// Renvoie le nouveau gabarit, ou <c>null</c> si la fusion est invalide (mauvaise phase, classe
    /// feuille/essentielle, pas assez d'exemplaires, ou évolution étrangère à l'arbre de la classe).
    /// </summary>
    public UnitSpec? Fuse(UnitSpec spec, UnitClass evolution)
    {
        if (!CanFuse(spec))
            return null;
        // Retire FusionSize exemplaires (n'importe lesquels : ils sont identiques).
        var group = _roster.Where(u => !u.Essential && SameClass(u, spec)).Take(FusionSize).ToList();
        return Fuse(group, evolution);
    }

    /// <summary>
    /// Variante EXPLICITE : fusionne précisément les <see cref="FusionSize"/> gabarits donnés (instances
    /// réellement présentes au roster, de même classe non-feuille/non-essentielle). Le caller choisit donc
    /// quelles instances sont consommées — indispensable côté scène, où roster, réserve et pièces posées
    /// partagent les mêmes instances <see cref="UnitSpec"/> : retirer les bonnes évite de désynchroniser
    /// la vue. Renvoie le nouveau gabarit (ajouté au roster), ou <c>null</c> si le groupe est invalide.
    /// </summary>
    public UnitSpec? Fuse(IReadOnlyList<UnitSpec> group, UnitClass evolution)
    {
        if (Phase != RunPhase.Placement || group.Count != FusionSize)
            return null;

        var first = group[0];
        if (first.Essential || first.UnitClass.IsLeaf || !first.UnitClass.Evolutions.Contains(evolution))
            return null;
        if (group.Distinct().Count() != FusionSize)                        // FusionSize instances DISTINCTES
            return null;
        if (group.Any(u => !SameClass(u, first) || !_roster.Contains(u)))  // même classe + réellement au roster
            return null;

        foreach (var u in group)
        {
            if (u.Equipment is { } e)   // fusion : les équipements des 3 pions reviennent à l'inventaire
            {
                _equipment.Add(e);
                u.Equipment = null;
            }
            _roster.Remove(u);
        }

        var fused = new UnitSpec(first.Domaine, evolution);   // l'unité évoluée sort nue
        _roster.Add(fused);
        return fused;
    }

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

    /// <summary>Au-delà de ce nombre d'exemplaires d'un même type dans une vague, on évite d'en rajouter.</summary>
    private const int SameTypeCap = 2;

    /// <summary>
    /// Ennemi au hasard parmi les <paramref name="poolSize"/> premiers types débloqués, en ÉVITANT un
    /// type déjà présent <see cref="SameTypeCap"/> fois (≥3 exemplaires) dans <paramref name="soFar"/>.
    /// Si tous les types disponibles ont atteint le plafond (pool trop petit pour l'effectif), on autorise
    /// quand même n'importe quel type plutôt que de boucler.
    /// </summary>
    private static UnitSpec RandomEnemy(Random rng, int poolSize, List<UnitSpec> soFar)
    {
        var room = new List<Domaine>();
        for (var i = 0; i < poolSize; i++)
        {
            var d = IntroOrder[i];
            if (soFar.Count(u => u.Domaine == d) < SameTypeCap)
                room.Add(d);
        }

        var domaine = room.Count > 0 ? room[rng.Next(room.Count)] : IntroOrder[rng.Next(poolSize)];
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
    /// Mélange en place une liste avec le RNG DÉTERMINISTE du combat (stable d'une session à l'autre,
    /// donc même tirage à la reprise d'une sauvegarde). Sert ex. à placer la vague ennemie sur des
    /// cases tirées au hasard parmi celles proposées par la map. <paramref name="salt"/> par défaut 2
    /// (≠ terrain=0, vague=1).
    /// </summary>
    public void ShuffleForCombat<T>(IList<T> list, int salt = 2)
    {
        var rng = CombatRng(salt);
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
