using System;
using System.Collections.Generic;
using System.Linq;
using Echec.Core.Map;

namespace Echec.Core.Battle;

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
/// SUICIDAIRE en fin de partie : quand il ne reste plus qu'UNE unité ennemie, elle abandonne le filet
/// « sans mourir » et s'engage même si elle doit y rester — sinon elle fuit et le joueur lui court après.
/// </summary>
public static class EnemyAi
{
    /// <summary>Distance (Chebyshev) à laquelle un joueur « réveille » un garde défensif.</summary>
    public const int AlertRadius = 2;

    private static readonly Random SharedRng = new();
    private static readonly IReadOnlyCollection<Cell> NoPaysans = Array.Empty<Cell>();

    public static AiAction? ChooseAction(Match match) => ChooseAction(match, NoPaysans, SharedRng);

    public static AiAction? ChooseAction(Match match, IReadOnlyCollection<Cell> paysanCells) =>
        ChooseAction(match, paysanCells, SharedRng);

    /// <param name="paysanCells">Cases des paysans (tuiles recrue non résolues) : GARDÉES par les défensifs,
    /// ASSAILLIES par les offensifs.</param>
    /// <param name="rng">Source d'aléa (injectable pour des tests déterministes).</param>
    public static AiAction? ChooseAction(Match match, IReadOnlyCollection<Cell> paysanCells, Random rng)
    {
        if (match.IsOver || match.CurrentTurn != Faction.Enemy)
            return null;

        var enemies = match.Units().Where(x => x.Unit.Faction == Faction.Enemy).ToList();
        var players = match.Units().Where(x => x.Unit.Faction == Faction.Player).ToList();
        if (players.Count == 0)
            return null;

        var playerCells = players.Select(p => p.Cell).ToList();

        // Dernière unité : on fonce. Elle s'engage même vers une case où elle mourra, plutôt que
        // de fuir et de faire courir le joueur après elle jusqu'à la fin de la partie.
        var suicidal = enemies.Count <= 1;

        var capture = new List<AiAction>();             // 0. offensif : se poser sur un paysan (objectif de mission)
        AiAction? bestKill = null, bestAttack = null;   // 1. attaques (mortelle prioritaire)
        var engage = new List<AiAction>();              // 2. mise à portée (normal / défensif alerté)
        var advanceByUnit = new List<AiAction>();       // 3. avancée vers la cible (normal → joueur, offensif → paysan)
        var guardByUnit = new List<AiAction>();         // 4. repositionnement des gardes défensifs
        var anyLegalMove = new List<AiAction>();        // 5. repli anti-blocage (tous)

        foreach (var (from, unit) in enemies)
        {
            var defensif = unit.AiKind == AiKind.Defensif;
            var offensif = unit.AiKind == AiKind.Offensif;

            foreach (var target in match.AttackTargets(from))
            {
                var victim = match.UnitAt(target)!;
                if (unit.Damage >= victim.Hp)
                    bestKill ??= new AiAction(from, target, IsAttack: true);
                else
                    bestAttack ??= new AiAction(from, target, IsAttack: true);
            }

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

        if (capture.Count > 0) return capture[rng.Next(capture.Count)];
        if (bestKill is { } kill) return kill;
        if (bestAttack is { } attack) return attack;
        if (engage.Count > 0) return engage[rng.Next(engage.Count)];
        if (advanceByUnit.Count > 0) return advanceByUnit[rng.Next(advanceByUnit.Count)];
        if (guardByUnit.Count > 0) return guardByUnit[rng.Next(guardByUnit.Count)];
        if (anyLegalMove.Count > 0) return anyLegalMove[rng.Next(anyLegalMove.Count)];
        return null;
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
