using System;
using System.Collections.Generic;
using System.Linq;

namespace Echec.Core.Battle;

/// <summary>
/// Registre des BOSS (<see cref="BossDef"/>), chargé depuis units.json ; à défaut, un repli codé sert
/// (tests, fichier manquant). Pendant de <see cref="Commandes"/> pour le rôle boss.
///
/// Porte aussi le TIRAGE d'une run : <see cref="AssignForRun"/> assigne un boss DISTINCT à chacune des
/// phases (un par phase), de façon DÉTERMINISTE (graine de run) — donc rejoué à l'identique après
/// « Continuer ». Si le pool compte moins de boss éligibles que de phases, la répétition est autorisée.
/// </summary>
public static class Bosses
{
    private static IReadOnlyList<BossDef> _all = Defaults();

    /// <summary>Remplace les définitions (depuis le JSON). Ignoré si la liste est vide (garde le repli).</summary>
    public static void Load(IReadOnlyList<BossDef> defs)
    {
        if (defs.Count > 0)
            _all = defs;
    }

    public static IReadOnlyList<BossDef> All => _all;

    /// <summary>
    /// Assigne un boss à chacune des phases <c>1..phaseCount</c> : au maximum des boss DISTINCTS, tirés de
    /// façon déterministe depuis <paramref name="seed"/>. On privilégie, pour chaque phase, un boss ENCORE
    /// non utilisé qui déclare cette phase ; à défaut (pool trop petit) on réutilise un boss qui la déclare ;
    /// en tout dernier recours, n'importe lequel (son profil de repli le plus proche s'appliquera).
    ///
    /// PRIORITÉ DÉBLOCAGE : la DERNIÈRE phase privilégie un boss dont <see cref="BossDef.UnlocksCommander"/>
    /// n'est pas encore dans <paramref name="unlockedCommanders"/> (le joueur affronte en priorité un boss
    /// qui lui débloquerait un commandant) ; les autres phases évitent de « consommer » ces boss.
    /// </summary>
    public static IReadOnlyList<BossDef> AssignForRun(int seed, int phaseCount,
        IReadOnlySet<string>? unlockedCommanders = null) =>
        AssignForRun(_all, seed, phaseCount, unlockedCommanders);

    /// <summary>Variante pure (sur un pool fourni) — sert au repli et aux tests.</summary>
    public static IReadOnlyList<BossDef> AssignForRun(IReadOnlyList<BossDef> pool, int seed, int phaseCount,
        IReadOnlySet<string>? unlockedCommanders = null)
    {
        if (pool.Count == 0)
            pool = Defaults();

        // Permutation déterministe du pool (sel propre au boss, distinct du terrain/vague — cf. Run.CombatRng).
        var rng = new Random(unchecked(seed * 92821 + 5));
        var order = pool.ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // Un boss est « à débloquer » s'il lie un commandant que le joueur n'a pas encore obtenu.
        bool ToUnlock(BossDef b) =>
            b.UnlocksCommander is { } id && !(unlockedCommanders?.Contains(id) ?? false);

        var result = new BossDef[phaseCount];
        var used = new HashSet<BossDef>();
        for (var phase = 1; phase <= phaseCount; phase++)
        {
            var isFinal = phase == phaseCount;
            BossDef? pick = isFinal
                // Phase finale : d'abord un boss « à débloquer » (distinct, puis réutilisable), sinon normal.
                ? order.FirstOrDefault(b => ToUnlock(b) && b.SupportsPhase(phase) && !used.Contains(b))
                  ?? order.FirstOrDefault(b => ToUnlock(b) && b.SupportsPhase(phase))
                  ?? order.FirstOrDefault(b => b.SupportsPhase(phase) && !used.Contains(b))
                  ?? order.FirstOrDefault(b => b.SupportsPhase(phase))
                // Phases précédentes : on RÉSERVE les boss « à débloquer » pour la finale quand c'est possible.
                : order.FirstOrDefault(b => !ToUnlock(b) && b.SupportsPhase(phase) && !used.Contains(b))
                  ?? order.FirstOrDefault(b => b.SupportsPhase(phase) && !used.Contains(b))
                  ?? order.FirstOrDefault(b => b.SupportsPhase(phase));
            pick ??= order[0];   // dernier recours

            result[phase - 1] = pick;
            used.Add(pick);
        }
        return result;
    }

    // Repli codé (doit rester cohérent avec Assets/Config/units.json). Un seul boss, mais un profil par phase
    // pour que le jeu reste jouable sans config (montée en puissance simple, sans trait).
    private static IReadOnlyList<BossDef> Defaults() => new[]
    {
        new BossDef("Boss", "boss", Domaine.Dame, new Dictionary<int, UnitClass>
        {
            [1] = new("Boss", "boss", tier: 1, maxHp: 30, damage: 9,  moveRange: 1, attackRange: 1),
            [2] = new("Boss", "boss", tier: 1, maxHp: 44, damage: 12, moveRange: 1, attackRange: 2),
            [3] = new("Boss", "boss", tier: 1, maxHp: 60, damage: 15, moveRange: 1, attackRange: 2),
        }),
    };
}
