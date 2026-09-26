using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Battle;

/// <summary>Comportement d'IA d'une unité ennemie.</summary>
public enum AiKind
{
    /// <summary>Fonce sur le joueur : attaque à portée, sinon se met à portée, sinon avance. Défaut.</summary>
    Normal,

    /// <summary>
    /// Garde défensif (mission « libérer les paysans ») : n'avance JAMAIS vers le joueur ; attaque tout joueur
    /// déjà à portée ; réagit seulement si un joueur s'approche à <see cref="EnemyAi.AlertRadius"/> cases ;
    /// sinon se repositionne vers/autour de la case gardée (paysan) la plus proche, SANS jamais s'y poser.
    /// </summary>
    Defensif,

    /// <summary>
    /// Assaillant offensif (mission « protéger les paysans ») : AGRESSIF. CAPTURER un paysan à portée (se
    /// poser sur sa case) passe AVANT tout — c'est l'objectif de la mission. À défaut de capture immédiate,
    /// il attaque un joueur à portée, se JETTE sur un joueur atteignable (s'engage même au risque de mourir),
    /// et sinon fonce vers le paysan le plus proche. L'engagement ne prime que sur l'AVANCÉE (paysan lointain),
    /// jamais sur une capture réalisable ce tour-ci.
    /// </summary>
    Offensif,

    /// <summary>
    /// SENTINELLE : postée, elle ne bouge pas. Elle attaque tout joueur à sa portée, mais ne s'engage pas,
    /// n'avance pas, ne se repositionne pas — pas même pour se dégager (elle est exclue du repli anti-blocage).
    /// Sert aux ennemis placés sur un MIRADOR : ils y sont pour la vue, en descendre n'aurait aucun sens.
    ///
    /// UNE exception : en DERNIER CARRÉ — quand le camp ennemi n'a plus QUE des sentinelles — elles quittent
    /// leur poste et se battent comme des <see cref="Normal"/>. Sinon le combat se figerait, personne
    /// n'avançant plus, et il faudrait attendre la limite de tours pour en sortir.
    /// </summary>
    Sentinelle,
}

/// <summary>Action choisie par l'IA : un déplacement ou une attaque.</summary>
public readonly record struct AiAction(Cell From, Cell To, bool IsAttack);

/// <summary>
/// IA du camp ennemi (un tour = UNE action pour tout le camp). Chaque unité agit selon son
/// <see cref="Unit.AiKind"/> ; on agrège les coups candidats de toutes les unités puis on choisit UN
/// coup par priorité :
///   0. CAPTURER un paysan — un OFFENSIF se pose sur une case paysan atteignable ce tour-ci : c'est
///      l'objectif de la mission « protéger », donc ça passe AVANT même une attaque ;
///   1. ATTAQUER une cible à portée (mortelle d'abord, sinon quelconque) — TOUS les comportements ;
///   2. SE METTRE À PORTÉE du joueur — un pion NORMAL (sans mourir) et un OFFENSIF (agressif : même au
///      risque de mourir) ; un DÉFENSIF seulement s'il est alerté (un joueur à ≤ <see cref="AlertRadius"/> cases) ;
///   3. AVANCER vers sa CIBLE — un NORMAL vers le joueur, un OFFENSIF vers le paysan le plus proche (quand il
///      n'est pas encore à portée de s'y poser) ; départage aléatoire entre unités ;
///   4. GARDER — un pion DÉFENSIF se repositionne vers/autour de la case gardée (paysan) la plus proche
///      sans s'y poser : il produit TOUJOURS un coup s'il peut bouger (déjà au contact → il tourne autour) ;
///   5. repli anti-blocage : n'importe quel coup légal (tous comportements).
///
/// EXIGENCE : l'ennemi doit BOUGER quoi qu'il arrive. Un pion ne reste jamais sur place s'il a un
/// déplacement légal ; <see cref="ChooseAction(Match, IReadOnlyCollection{Cell}, Random)"/> ne renvoie
/// <c>null</c> (⇒ tour passé par l'appelant) que si PLUS AUCUN ennemi ne peut bouger, ou s'il n'y a plus
/// de joueur.
///
/// Le tirage aléatoire (mise à portée comme avancée) évite de toujours pousser la même pièce : l'armée
/// progresse en se renouvelant au lieu d'envoyer un éclaireur solitaire.
///
/// MALADRESSE (difficulté) : l'IA ne joue son meilleur coup qu'avec une probabilité
/// <c>accuracy</c> (cf. <see cref="DifficultySettings.AiAccuracy"/>). Sinon elle DESCEND D'UN CRAN de
/// priorité (engagement → avancée, etc.). DEUX exceptions pour que la maladresse reste crédible :
///   • une ATTAQUE NON-LÉTALE en tête est TOUJOURS jouée — un pion qui peut taper sans tuer le fait
///     (le sauter paraîtrait absurde) ;
///   • rater un KILL ne fait pas faire une bêtise au pion tueur : c'est un AUTRE pion (≠ ceux qui
///     pouvaient tuer) qui joue son meilleur coup NON-LÉTAL. Si personne d'autre ne peut agir, le kill se fait.
/// Elle ne descend jamais jusqu'à ne rien faire : s'il n'existe qu'un seul rang de coups, elle le joue.
///
/// SUICIDAIRE en fin de partie : quand il ne reste plus qu'UNE unité ennemie, elle abandonne le filet
/// « sans mourir » et s'engage même si elle doit y rester — sinon elle fuit et le joueur lui court après.
/// </summary>
public static class EnemyAi
{
    /// <summary>Distance (Chebyshev) à laquelle un joueur « réveille » un garde défensif.</summary>
    public const int AlertRadius = 2;

    /// <summary>Jeu parfait : l'IA prend toujours la meilleure décision disponible. Défaut hors difficulté.</summary>
    public const double PerfectAccuracy = 1.0;

    private static readonly Random SharedRng = new();
    private static readonly IReadOnlyCollection<Cell> NoPaysans = Array.Empty<Cell>();

    public static AiAction? ChooseAction(Match match) => ChooseAction(match, NoPaysans, SharedRng);

    public static AiAction? ChooseAction(
        Match match, IReadOnlyCollection<Cell> paysanCells, double accuracy = PerfectAccuracy) =>
        ChooseAction(match, paysanCells, SharedRng, accuracy);

    /// <param name="paysanCells">Cases des paysans (tuiles recrue non résolues) : GARDÉES par les défensifs,
    /// ASSAILLIES par les offensifs.</param>
    /// <param name="rng">Source d'aléa (injectable pour des tests déterministes).</param>
    /// <param name="accuracy">Probabilité (0..1) de jouer le MEILLEUR coup ; sinon l'IA descend d'un cran de
    /// priorité. Défaut <see cref="PerfectAccuracy"/> : c'est l'appelant (couche Game) qui applique la
    /// difficulté, cf. <see cref="DifficultySettings.Active"/>.</param>
    public static AiAction? ChooseAction(
        Match match,
        IReadOnlyCollection<Cell> paysanCells,
        Random rng,
        double accuracy = PerfectAccuracy)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player).ToList();
        if (players.Count == 0)
            return null;

        var playerCells = players.Select(p => p.Cell).ToList();

        // BOSS ATTAQUÉ : c'est LUI qui répond, et personne d'autre — même si une escorte pourrait frapper.
        if (BossReaction(match, enemies, rng) is { } reaction)
            return reaction;

        // Dernière unité : on fonce. Elle s'engage même vers une case où elle mourra, plutôt que
        // de fuir et de faire courir le joueur après elle jusqu'à la fin de la partie.
        var suicidal = enemies.Count <= 1;

        // SENTINELLES SEULES : si le camp n'a plus QUE des pions postés, ils quittent leur poste et se battent
        // comme des normaux. Sans ça le combat se figerait — plus personne n'avancerait, et il faudrait
        // attendre la limite de tours pour en sortir. Tant qu'un camarade mobile tient encore, chacun reste
        // à son mirador.
        var lastStand = enemies.All(e => e.Unit.AiKind == AiKind.Sentinelle);

        // BOSS EN RETRAIT : tant que son escorte tient, le chef laisse ses troupes aller au contact.
        var bossHoldsBack = BossHoldsBack(match, enemies);

        var capture = new List<AiAction>();             // 0. offensif : se poser sur un paysan (objectif de mission)
        AiAction? bestKill = null;                      // 1. une attaque MORTELLE (jouée telle quelle)
        var killCells = new HashSet<Cell>();            //    cases des pions capables de TUER ce tour (repli de maladresse)
        var attackByUnit = new List<AiAction>();        // 2. une attaque NON-LÉTALE par pion (jamais ratée)
        var engage = new List<AiAction>();              // 3. mise à portée (normal / défensif alerté)
        var advanceByUnit = new List<AiAction>();       // 4. avancée vers la cible (normal → joueur, offensif → paysan)
        var guardByUnit = new List<AiAction>();         // 5. repositionnement des gardes défensifs
        var anyLegalMove = new List<AiAction>();        // 6. repli anti-blocage (tous)

        foreach (var (from, unit) in enemies)
        {
            var defensif = unit.AiKind == AiKind.Defensif;
            var offensif = unit.AiKind == AiKind.Offensif;

            AiAction? unitAttack = null;   // une attaque NON-LÉTALE de CE pion (variété + repli de maladresse sur un kill)
            foreach (var target in match.AttackTargets(from))
            {
                var victim = match.UnitAt(target)!;
                if (unit.Damage >= victim.Hp)
                {
                    bestKill ??= new AiAction(from, target, IsAttack: true);
                    killCells.Add(from);   // ce pion peut tuer ce tour-ci
                }
                else
                    unitAttack ??= new AiAction(from, target, IsAttack: true);
            }
            if (unitAttack is { } ua)
                attackByUnit.Add(ua);

            // SENTINELLE : ses attaques sont relevées ci-dessus, mais elle ne propose AUCUN déplacement — pas
            // même le repli anti-blocage. Elle tient son poste… sauf en DERNIER CARRÉ (cf. lastStand), où elle
            // descend et se bat comme un normal : le reste de la boucle la traite alors comme tel.
            if (unit.AiKind == AiKind.Sentinelle && !lastStand)
                continue;

            // BOSS EN RETRAIT : même traitement qu'une sentinelle. Il frappe tout ce qui entre à sa portée
            // (ses attaques sont relevées ci-dessus, kill compris), mais ne s'engage pas, n'avance pas, et ne
            // sert pas de repli anti-blocage — c'est l'escorte qui joue. Cf. BossHoldsBack.
            if (bossHoldsBack && unit.IsEssential)
                continue;

            // S'ENGAGER = se mettre à portée du joueur. Un normal ET un OFFENSIF le font (l'offensif est
            // AGRESSIF : il attaque vite et prend des risques, cf. le filet levé plus bas). Un garde défensif
            // seulement s'il est ALERTÉ (un joueur à ≤ AlertRadius). Priorité globale : engager passe AVANT
            // l'avancée → un offensif se jette sur un joueur atteignable plutôt que de filer vers le paysan.
            var canEngage = !defensif || playerCells.Any(p => Chebyshev(from, p) <= AlertRadius);

            // Cases vers lesquelles l'unité AVANCE (null pour un défensif, qui garde). Un offensif vise le
            // paysan le plus proche (et peut S'Y POSER pour le capturer), à défaut le joueur ; un normal vise le joueur.
            IReadOnlyList<Cell>? advanceTargets =
                defensif ? null
                : offensif ? (paysanCells.Count > 0 ? paysanCells.ToList() : playerCells)
                : playerCells;

            AiAction? bestAdvanceForUnit = null;
            var bestAdvanceDistance = advanceTargets is null ? 0 : advanceTargets.Min(t => Chebyshev(from, t));

            // Case gardée la plus proche (défensif) : on vise le coup qui en RESTE le plus près, sans s'y poser.
            // Départ à MAX (et non la distance courante) : le garde produit TOUJOURS un coup s'il peut bouger.
            var guard = defensif ? NearestCell(from, paysanCells) : (Cell?)null;
            AiAction? bestGuardForUnit = null;
            var bestGuardDistance = int.MaxValue;

            foreach (var to in match.LegalMoves(from))
            {
                anyLegalMove.Add(new AiAction(from, to, IsAttack: false));   // repli : garantit que l'ennemi bouge

                // CAPTURE : un offensif qui peut se POSER sur un paysan ce tour-ci le fait en priorité absolue
                // (l'objectif de la mission « protéger » côté ennemi). Passe avant l'attaque et l'engagement.
                if (offensif && paysanCells.Contains(to))
                    capture.Add(new AiAction(from, to, IsAttack: false));

                // L'offensif PREND DES RISQUES : il s'engage même vers une case où il pourrait se faire tuer
                // (comme la dernière unité « suicidaire »). Le normal, lui, évite les cases mortelles.
                if (canEngage
                    && match.TargetsAfterMove(from, to).Count > 0
                    && (suicidal || offensif || !WouldBeKilledAt(match, to, unit, players)))
                    engage.Add(new AiAction(from, to, IsAttack: false));

                if (advanceTargets is not null)
                {
                    var distance = advanceTargets.Min(t => Chebyshev(to, t));
                    if (distance < bestAdvanceDistance)
                    {
                        bestAdvanceDistance = distance;
                        bestAdvanceForUnit = new AiAction(from, to, IsAttack: false);
                    }
                }
                else if (guard is { } g
                    && !paysanCells.Contains(to)                             // le garde ne se pose JAMAIS sur un paysan
                    && (suicidal || !WouldBeKilledAt(match, to, unit, players)))
                {
                    var gd = Chebyshev(to, g);
                    if (gd < bestGuardDistance)
                    {
                        bestGuardDistance = gd;
                        bestGuardForUnit = new AiAction(from, to, IsAttack: false);
                    }
                }
            }

            if (bestAdvanceForUnit is { } adv)
                advanceByUnit.Add(adv);
            if (bestGuardForUnit is { } grd)
                guardByUnit.Add(grd);
        }

        // Rangs de priorité effectivement disponibles ce tour-ci (les vides sont ignorés).
        var ranks = new List<IReadOnlyList<AiAction>>(7);
        void Rank(IReadOnlyList<AiAction> actions)
        {
            if (actions.Count > 0)
                ranks.Add(actions);
        }

        Rank(capture);
        if (bestKill is { } kill) ranks.Add(new[] { kill });
        Rank(attackByUnit);
        Rank(engage);
        Rank(advanceByUnit);
        Rank(guardByUnit);
        Rank(anyLegalMove);

        if (ranks.Count == 0)
            return null;

        AiAction Pick(IReadOnlyList<AiAction> r) => r[rng.Next(r.Count)];

        // Nature du rang du DESSUS : elle conditionne la maladresse.
        var topIsAttack = capture.Count == 0 && bestKill is null && attackByUnit.Count > 0;
        var topIsKill = capture.Count == 0 && bestKill is not null;

        // RÈGLE 1 : une ATTAQUE NON-LÉTALE en tête est TOUJOURS jouée — la maladresse ne la saute jamais
        // (un pion qui pourrait taper sans tuer le fait, sinon ça paraît trop bête).
        if (topIsAttack)
            return Pick(ranks[0]);

        // MALADRESSE : avec la probabilité (1 - accuracy) l'IA rate sa décision. Jamais jusqu'à ne rien faire —
        // s'il n'y a qu'un rang, elle le joue (l'ennemi doit BOUGER quoi qu'il arrive).
        if (ranks.Count == 1 || rng.NextDouble() < accuracy)
            return Pick(ranks[0]);   // pas de maladresse : meilleur coup

        // RÈGLE 2 : rater un KILL ne fait pas faire une bêtise au pion tueur — c'est un AUTRE pion (≠ ceux
        // qui pouvaient tuer) qui joue son meilleur coup NON-LÉTAL, selon la même priorité. Si personne
        // d'autre ne peut agir, le kill se fait quand même.
        if (topIsKill)
        {
            foreach (var rank in new[] { attackByUnit, engage, advanceByUnit, guardByUnit, anyLegalMove })
            {
                var others = rank.Where(a => !killCells.Contains(a.From)).ToList();
                if (others.Count > 0)
                    return Pick(others);
            }
            return bestKill!.Value;   // topIsKill ⇒ non-null ; aucun autre pion ne peut agir → le kill se fait
        }

        // LE RESTE (capture, engagement, avancée…) : on descend d'un cran, comme avant.
        return Pick(ranks[1]);
    }

    /// <summary>Case de <paramref name="cells"/> la plus proche de <paramref name="from"/> (Chebyshev), ou null si aucune.</summary>
    private static Cell? NearestCell(Cell from, IReadOnlyCollection<Cell> cells)
    {
        Cell? best = null;
        var bestDist = int.MaxValue;
        foreach (var c in cells)
        {
            var d = Chebyshev(from, c);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// RÉACTION DU BOSS : il vient d'encaisser un coup (depuis le dernier tour ennemi, cf.
    /// <see cref="Unit.HitSinceOwnTurn"/>), donc le tour lui revient À LUI SEUL — aucune escorte ne joue, même
    /// si l'une d'elles pourrait frapper. Il ATTAQUE s'il a une cible (un coup mortel d'abord), sinon il SE
    /// CACHE : il file sur la case la MOINS menacée de sa portée de déplacement (même carte de menace que
    /// l'« Esquive », tirage au sort entre ex aequo).
    ///
    /// Décision FORCÉE : la maladresse de la difficulté ne s'y applique pas. <c>null</c> — la sélection
    /// habituelle reprend — si le boss n'a pas été touché, ou s'il ne peut ni frapper ni bouger (sinon le camp
    /// ennemi resterait figé pour rien).
    /// </summary>
    private static AiAction? BossReaction(Match match, List<(Cell Cell, Unit Unit)> enemies, Random rng)
    {
        var (bossCell, boss) = enemies.FirstOrDefault(e => e.Unit.IsEssential && e.Unit.HitSinceOwnTurn);
        if (boss == null)
            return null;

        // 1. ATTAQUER : le coup mortel d'abord, sinon la première cible à portée.
        var targets = match.AttackTargets(bossCell);
        if (targets.Count > 0)
        {
            foreach (var t in targets)
                if (match.UnitAt(t) is { } victim && boss.Damage >= victim.Hp)
                    return new AiAction(bossCell, t, IsAttack: true);
            return new AiAction(bossCell, targets[0], IsAttack: true);
        }

        // 2. SE CACHER : la case la moins menacée par le camp adverse.
        var moves = match.LegalMoves(bossCell);
        if (moves.Count == 0)
            return null;

        var threat = new Dictionary<Cell, int>();
        foreach (var (cell, other) in match.Units())
        {
            if (other.Faction == boss.Faction)
                continue;
            foreach (var covered in match.ThreatenedCells(cell))
                threat[covered] = threat.TryGetValue(covered, out var n) ? n + 1 : 1;
        }

        var best = int.MaxValue;
        var pool = new List<Cell>();
        foreach (var to in moves)
        {
            var count = threat.TryGetValue(to, out var n) ? n : 0;
            if (count > best)
                continue;
            if (count < best)
            {
                best = count;
                pool.Clear();
            }
            pool.Add(to);
        }
        return new AiAction(bossCell, pool[rng.Next(pool.Count)], IsAttack: false);
    }

    /// <summary>
    /// Part de l'escorte INITIALE qui doit encore tenir pour que le boss reste en retrait : à 0,5, il passe
    /// à l'offensive dès que plus de la moitié de ses troupes sont tombées.
    /// </summary>
    public const double BossRetreatEscortRatio = 0.5;

    /// <summary>
    /// Vrai si le BOSS doit rester EN RETRAIT ce tour-ci : il lui reste une escorte, et au moins
    /// <see cref="BossRetreatEscortRatio"/> de celle du début du combat. Sans cette règle, le boss avançait
    /// comme n'importe quel pion — l'IA ne joue qu'un pion par tour, tiré parmi tous ceux qui avancent — et
    /// il lui arrivait de venir mourir dès les premiers tours, alors que c'est lui que le joueur doit aller
    /// chercher. En retrait, il frappe ce qui entre à sa portée ; il se met en marche quand ses troupes cèdent.
    ///
    /// L'escorte initiale se RECONSTITUE sans rien mémoriser : les escortes debout plus celles tombées au
    /// journal des morts du match. L'IA reste ainsi sans état, comme le reste de ce fichier. Un combat sans
    /// escorte (ou un boss qui n'en a plus) n'est jamais en retrait.
    ///
    /// TOUCHÉ, il sort de sa réserve pour de bon, escorte ou pas : sinon un tireur posté hors de sa portée
    /// pouvait le cribler sans qu'il bouge. <see cref="Unit.TimesHit"/> ne compte que les coups réellement
    /// encaissés (un coup réduit à 0 ne le réveille pas) et repart de zéro à chaque combat.
    /// </summary>
    private static bool BossHoldsBack(Match match, List<(Cell Cell, Unit Unit)> enemies)
    {
        var escortsAlive = enemies.Count(e => !e.Unit.IsEssential);
        var boss = enemies.FirstOrDefault(e => e.Unit.IsEssential).Unit;
        if (escortsAlive == 0 || boss == null || boss.TimesHit > 0)
            return false;
        var escortsFallen = match.DeathLog.Count(d => d.Unit.Faction == Faction.Enemy && !d.Unit.IsEssential);
        return escortsAlive >= (escortsAlive + escortsFallen) * BossRetreatEscortRatio;
    }

    /// <summary>
    /// Vrai si un joueur menace déjà <paramref name="cell"/> d'une attaque qui TUERAIT
    /// <paramref name="unit"/> (dégâts ≥ PV). Approximation : menace depuis les positions
    /// actuelles des joueurs (sans anticiper leurs propres déplacements).
    /// </summary>
    private static bool WouldBeKilledAt(Match match, Cell cell, Unit unit, List<(Cell Cell, Unit Unit)> players)
    {
        foreach (var (pc, pu) in players)
        {
            if (pu.Damage < unit.Hp)
                continue;   // ne pourrait pas le tuer d'un coup
            if (match.ThreatenedCells(pc).Contains(cell))
                return true;
        }
        return false;
    }

    private static int Chebyshev(Cell a, Cell b) =>
        Math.Max(Math.Abs(a.Column - b.Column), Math.Abs(a.Row - b.Row));
}
