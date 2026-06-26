using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// État et règles d'une partie : grille d'unités, tour courant, déplacement et combat,
/// condition de victoire. Domaine pur (aucun rendu).
///
/// Un tour = UNE action : se DÉPLACER vers une case vide (jusqu'à la portée de
/// déplacement) OU ATTAQUER une cible à portée de tir. Les deux suivent les directions
/// du domaine. À l'attaque : si la cible meurt, l'attaquant prend sa place dès lors qu'il
/// POURRAIT s'y déplacer (case libérée atteignable par son mouvement : mêlée, saut, ou ligne
/// dégagée du lancier dans sa portée) ; sinon (hors d'atteinte ou chemin bloqué) il reste.
/// </summary>
public sealed class Match
{
    private readonly Unit?[,] _units;

    // Terrain optionnel : si fourni, l'eau et la montagne bornent déplacements et/ou tirs
    // (null = plateau entièrement traversable, comme avant l'ajout du terrain).
    private readonly Battlefield? _terrain;

    // Unités essentielles posées sur le terrain (commandant joueur / boss ennemi).
    // On garde la référence même après leur mort pour évaluer la condition de victoire.
    private readonly List<Unit> _essential = new();

    // Buffer réutilisé par CanTakePlace (évite d'allouer une liste de coups à chaque kill).
    private readonly List<Cell> _placeBuffer = new();

    public Match(int width, int height, Battlefield? terrain = null)
    {
        Width = width;
        Height = height;
        _units = new Unit?[width, height];
        _terrain = terrain;
    }

    /// <summary>Vrai si la tuile interdit le déplacement (eau ou montagne).</summary>
    private bool BlocksMovement(Cell cell) =>
        _terrain != null && _terrain[cell].Terrain.BlocksMovement();

    /// <summary>Vrai si la tuile arrête une ligne de tir (montagne).</summary>
    private bool BlocksLineOfFire(Cell cell) =>
        _terrain != null && _terrain[cell].Terrain.BlocksLineOfFire();

    public int Width { get; }
    public int Height { get; }
    public Faction CurrentTurn { get; private set; } = Faction.Player;
    public Faction? Winner { get; private set; }
    public bool IsOver => Winner != null;

    public bool InBounds(Cell cell) =>
        cell.Column >= 0 && cell.Column < Width && cell.Row >= 0 && cell.Row < Height;

    public Unit? UnitAt(Cell cell) => InBounds(cell) ? _units[cell.Column, cell.Row] : null;

    public void Place(Cell cell, Unit unit)
    {
        _units[cell.Column, cell.Row] = unit;
        if (unit.IsEssential)
            _essential.Add(unit);
    }

    /// <summary>Retire l'unité d'une case (utilisé en phase de placement).</summary>
    public void Remove(Cell cell)
    {
        var unit = UnitAt(cell);
        if (unit == null)
            return;
        _units[cell.Column, cell.Row] = null;
        _essential.Remove(unit);
    }

    public IEnumerable<(Cell Cell, Unit Unit)> Units()
    {
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width; column++)
            {
                var unit = _units[column, row];
                if (unit != null)
                    yield return (new Cell(column, row), unit);
            }
    }

    /// <summary>Cases VIDES atteignables en déplacement (le long des directions, bloqué par toute unité).</summary>
    public List<Cell> LegalMoves(Cell from)
    {
        var result = new List<Cell>();
        LegalMoves(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void LegalMoves(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = ActiveUnitAt(from);
        if (unit == null)
            return;

        var vectors = Movement.Vectors(unit.Domaine);

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (InBounds(to) && _units[to.Column, to.Row] == null && !BlocksMovement(to))
                    result.Add(to);
            }
            return;
        }

        var phases = unit.HasTrait(Trait.Franchissement);   // se déplace au travers des unités (alliées/ennemies)
        foreach (var dir in vectors)
        {
            for (var step = 1; step <= unit.MoveRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksMovement(to))
                    break; // hors plateau ou obstacle (eau/montagne) : on s'arrête
                if (_units[to.Column, to.Row] != null)
                {
                    if (phases) continue;   // Franchissement : on enjambe l'unité (sans pouvoir s'y poser)
                    break;                  // sinon une unité borne le déplacement
                }
                result.Add(to);
            }
        }
    }

    /// <summary>Cases ennemies à portée de TIR (première unité rencontrée dans chaque direction).</summary>
    public List<Cell> AttackTargets(Cell from)
    {
        var result = new List<Cell>();
        AttackTargets(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void AttackTargets(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = ActiveUnitAt(from);
        if (unit == null)
            return;

        var vectors = Movement.Vectors(unit.Domaine);

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (UnitAt(to) is { } target && target.Faction != unit.Faction)
                    result.Add(to);
            }
            return;
        }

        var piercesAllies = unit.Class.PiercesAllies;
        foreach (var dir in vectors)
        {
            // Zone morte (portée min) UNIQUEMENT en ligne droite : en diagonale on peut tirer dès la
            // distance 1 (le contact « corps à corps » n'est interdit qu'en face/côté).
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.Class.MinAttackRange;
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksLineOfFire(to))
                    break; // hors plateau ou montagne : la ligne de tir s'arrête (l'eau, elle, laisse passer)

                var target = _units[to.Column, to.Row];
                if (target == null)
                    continue; // case vide (ou eau) : la ligne de tir continue

                if (target.Faction != unit.Faction)
                {
                    // Premier ennemi en vue : cible SI au-delà de la zone morte de cette direction.
                    // Dans tous les cas son corps borne la ligne (pas de tir au travers).
                    if (step >= minStep)
                        result.Add(to);
                    break;
                }

                // Allié : le LANCIER le traverse sans le toucher (ne borne pas) ; sinon il bloque.
                if (!piercesAllies)
                    break;
            }
        }
    }

    /// <summary>
    /// Cases MENACÉES par l'unité en <paramref name="from"/> : toutes les cases atteignables le
    /// long de ses directions de tir jusqu'à sa portée, en s'arrêtant à la première unité
    /// rencontrée (incluse, car elle subirait l'attaque). INDÉPENDANT du tour courant — sert à
    /// prévisualiser la menace d'un ennemi au survol. Liste vide si la case est inoccupée.
    /// </summary>
    public List<Cell> ThreatenedCells(Cell from)
    {
        var result = new List<Cell>();
        ThreatenedCells(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void ThreatenedCells(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = UnitAt(from);
        if (unit == null)
            return;

        var vectors = Movement.Vectors(unit.Domaine);

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (InBounds(to))
                    result.Add(to);
            }
            return;
        }

        var piercesAllies = unit.Class.PiercesAllies;
        foreach (var dir in vectors)
        {
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.Class.MinAttackRange;
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksLineOfFire(to))
                    break; // hors plateau ou montagne : la menace ne porte pas au-delà (l'eau laisse passer)

                var occupant = _units[to.Column, to.Row];
                if (occupant != null && occupant.Faction == unit.Faction && piercesAllies)
                    continue; // lancier : traverse l'allié sans le menacer, la ligne continue

                if (step >= minStep)
                    result.Add(to); // hors zone morte (diagonale = dès 1) : case réellement menacée
                if (occupant != null)
                    break; // un ennemi (ou un allié non traversé) borne la ligne de tir au-delà
            }
        }
    }

    /// <summary>Déplace l'unité vers une case vide légale. Passe le tour en cas de succès.</summary>
    public MoveKind TryMove(Cell from, Cell to)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !LegalMoves(from).Contains(to))
            return MoveKind.Invalid;

        MoveUnit(from, to);
        TriggerInterceptions(to, unit);   // ennemis avec « Interception » dont la portée couvre la case d'arrivée
        EndTurn();
        return MoveKind.Moved;
    }

    /// <summary>Attaque une cible ennemie à portée de tir. Passe le tour en cas de succès.</summary>
    public MoveKind TryAttack(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !AttackTargets(from).Contains(target))
            return MoveKind.Invalid;

        var victim = _units[target.Column, target.Row]!;
        ApplyDamage(target, victim, EffectiveDamage(unit, from, victim, target));

        // Dégâts de zone : éclaboussure (mêmes dégâts effectifs) sur les ennemis autour de la cible.
        if (unit.HasTrait(Trait.DegatsDeZone))
            SplashAround(target, unit, from);

        // Transpercement : l'unité juste DERRIÈRE la cible (même direction) est aussi touchée.
        if (unit.HasTrait(Trait.Transpercement))
            PierceBehind(from, target, unit);

        MoveKind kind;
        if (!victim.IsAlive)
        {
            _units[target.Column, target.Row] = null;   // case libérée AVANT de tester l'accès
            if (CanTakePlace(from, target))
                MoveUnit(from, target);
            kind = MoveKind.Killed;
        }
        else
        {
            // Riposte : la victime survivante contre-attaque en mêlée (attaquant resté au contact).
            if (victim.HasTrait(Trait.Riposte) && ChebyshevDistance(from, target) == 1
                && UnitAt(from) is { } attacker && ReferenceEquals(attacker, unit))
            {
                ApplyDamage(from, attacker, EffectiveDamage(victim, target, attacker, from));
                RemoveDeadAt(from);
            }
            kind = MoveKind.Attacked; // l'attaquant reste sur place
        }

        EndTurn();
        return kind;
    }

    // ─── TRAITS : dégâts effectifs, formes d'attaque, réactions ───────────────────────────────────

    private const int RempartReduction = 4;     // -4 dégâts d'une attaque à distance (>= 2)
    private const int DuellisteReduction = 4;    // -4 dégâts d'une attaque au corps à corps
    private const int RageBonus = 6;             // +6 puissance quand l'attaquant est sous le seuil PV
    private const int RageHpThreshold = 10;      // seuil de PV de Rage
    private const int BenedictionBonus = 5;      // +5 puissance offerte par un allié « Bénédiction » adjacent

    private static readonly (int Dc, int Dr)[] Neighbors8 =
        { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    private static int ChebyshevDistance(Cell a, Cell b) =>
        System.Math.Max(System.Math.Abs(a.Column - b.Column), System.Math.Abs(a.Row - b.Row));

    /// <summary>Vrai si une case adjacente porte un allié de <paramref name="faction"/> avec ce trait.</summary>
    private bool HasAdjacentAlly(Cell cell, Faction faction, string trait)
    {
        foreach (var (dc, dr) in Neighbors8)
            if (UnitAt(new Cell(cell.Column + dc, cell.Row + dr)) is { } u
                && u.Faction == faction && u.HasTrait(trait))
                return true;
        return false;
    }

    /// <summary>
    /// Dégâts EFFECTIFS d'une attaque, traits inclus : Rage / Bénédiction (offensifs), Rempart / Aura de
    /// rempart (à distance ≥ 2) et Duelliste (corps à corps) en réduction. Borné à 0.
    /// </summary>
    private int EffectiveDamage(Unit attacker, Cell attackerCell, Unit victim, Cell victimCell)
    {
        var dmg = attacker.Damage;
        if (attacker.HasTrait(Trait.Rage) && attacker.Hp < RageHpThreshold)
            dmg += RageBonus;
        if (HasAdjacentAlly(attackerCell, attacker.Faction, Trait.Benediction))
            dmg += BenedictionBonus;

        var distance = ChebyshevDistance(attackerCell, victimCell);
        var shielded = victim.HasTrait(Trait.Rempart)
            || HasAdjacentAlly(victimCell, victim.Faction, Trait.AuraDeRempart);
        if (distance >= 2 && shielded)
            dmg -= RempartReduction;
        if (distance == 1 && victim.HasTrait(Trait.Duelliste))
            dmg -= DuellisteReduction;

        return System.Math.Max(0, dmg);
    }

    /// <summary>Applique des dégâts ; un allié adjacent « Bouclier divin » empêche la mort (PV ≥ 1).</summary>
    private void ApplyDamage(Cell cell, Unit unit, int amount)
    {
        if (amount <= 0)
            return;
        if (amount >= unit.Hp && HasAdjacentAlly(cell, unit.Faction, Trait.BouclierDivin))
            amount = unit.Hp - 1;   // laisse 1 PV : l'attaque n'est jamais mortelle
        if (amount > 0)
            unit.TakeDamage(amount);
    }

    /// <summary>Dégâts EFFECTIFS qu'infligerait l'attaque de <paramref name="from"/> sur
    /// <paramref name="target"/> (traits inclus), bornés aux PV de la cible — pour l'affichage.</summary>
    public int PreviewDamage(Cell from, Cell target)
    {
        var attacker = UnitAt(from);
        var victim = UnitAt(target);
        if (attacker == null || victim == null)
            return 0;
        return System.Math.Min(EffectiveDamage(attacker, from, victim, target), victim.Hp);
    }

    /// <summary>« Dégâts de zone » : touche les ennemis des 8 cases autour de la cible (mêmes dégâts effectifs).</summary>
    private void SplashAround(Cell center, Unit attacker, Cell attackerCell)
    {
        foreach (var (dc, dr) in Neighbors8)
        {
            var c = new Cell(center.Column + dc, center.Row + dr);
            if (UnitAt(c) is not { } u || u.Faction == attacker.Faction)
                continue;
            ApplyDamage(c, u, EffectiveDamage(attacker, attackerCell, u, c));
            RemoveDeadAt(c);
        }
    }

    /// <summary>« Transpercement » : touche l'ennemi situé une case derrière la cible (même direction).</summary>
    private void PierceBehind(Cell from, Cell target, Unit attacker)
    {
        var dc = System.Math.Sign(target.Column - from.Column);
        var dr = System.Math.Sign(target.Row - from.Row);
        var behind = new Cell(target.Column + dc, target.Row + dr);
        if (UnitAt(behind) is not { } u || u.Faction == attacker.Faction)
            return;
        ApplyDamage(behind, u, EffectiveDamage(attacker, from, u, behind));
        RemoveDeadAt(behind);
    }

    /// <summary>« Interception » : chaque ennemi du mobile dont la portée couvre la case d'arrivée le frappe.</summary>
    private void TriggerInterceptions(Cell movedTo, Unit mover)
    {
        foreach (var (cell, unit) in Units())
        {
            if (unit.Faction == mover.Faction || !unit.HasTrait(Trait.Interception))
                continue;
            if (!ThreatenedCells(cell).Contains(movedTo))
                continue;
            ApplyDamage(movedTo, mover, EffectiveDamage(unit, cell, mover, movedTo));
            if (!mover.IsAlive)
            {
                RemoveDeadAt(movedTo);
                return;   // mobile abattu : plus rien à intercepter
            }
        }
    }

    /// <summary>Retire de la grille l'unité morte d'une case (l'essentiel reste suivi pour la victoire).</summary>
    private void RemoveDeadAt(Cell cell)
    {
        if (UnitAt(cell) is { IsAlive: false })
            _units[cell.Column, cell.Row] = null;
    }

    /// <summary>Alliés BLESSÉS à portée qu'un soigneur (trait « Soin ») peut cibler.</summary>
    public List<Cell> HealTargets(Cell from)
    {
        var result = new List<Cell>();
        HealTargets(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation de <see cref="HealTargets(Cell)"/>.</summary>
    public void HealTargets(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = ActiveUnitAt(from);
        if (unit == null || !unit.HasTrait(Trait.Soin))
            return;

        var vectors = Movement.Vectors(unit.Domaine);
        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var off in vectors)
            {
                var to = new Cell(from.Column + off.Column, from.Row + off.Row);
                if (UnitAt(to) is { } a && a.Faction == unit.Faction && a.Hp < a.MaxHp)
                    result.Add(to);
            }
            return;
        }

        foreach (var dir in vectors)
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksLineOfFire(to))
                    break;
                var occ = _units[to.Column, to.Row];
                if (occ == null)
                    continue;
                if (occ.Faction == unit.Faction && occ.Hp < occ.MaxHp)
                    result.Add(to);   // premier allié blessé en vue
                break;                // toute unité borne la ligne
            }
    }

    /// <summary>« Soin » : soigne un allié ciblé (montant = puissance du soigneur). Passe le tour.</summary>
    public MoveKind TryHeal(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !HealTargets(from).Contains(target))
            return MoveKind.Invalid;

        UnitAt(target)!.Heal(unit.Damage);
        EndTurn();
        return MoveKind.Moved;   // action de soutien : tour consommé
    }

    /// <summary>Passe le tour sans agir (ennemi passif en tutoriel). Sans effet si la partie est finie.</summary>
    public void PassTurn()
    {
        if (!IsOver)
            CurrentTurn = CurrentTurn.Opponent();
    }

    /// <summary>
    /// Cibles qu'aurait l'unité de <paramref name="from"/> si elle se déplaçait en <paramref name="to"/>
    /// (plateau SIMULÉ puis restauré, tour inchangé). Outil d'IA pour repérer un coup qui amène à
    /// portée d'attaque. Renvoie une nouvelle liste (vide si <paramref name="from"/> est vide).
    /// </summary>
    public List<Cell> TargetsAfterMove(Cell from, Cell to)
    {
        var unit = UnitAt(from);
        if (unit == null)
            return new List<Cell>();

        var occupant = _units[to.Column, to.Row];
        _units[from.Column, from.Row] = null;
        _units[to.Column, to.Row] = unit;
        try
        {
            return AttackTargets(to);
        }
        finally
        {
            _units[to.Column, to.Row] = occupant;
            _units[from.Column, from.Row] = unit;
        }
    }

    /// <summary>
    /// L'attaquant prend la place de la cible tuée s'il POURRAIT s'y déplacer : case désormais
    /// libre ET atteignable par son mouvement (mêlée adjacente, saut du cavalier, ou ligne dégagée
    /// du lancier dans sa portée de déplacement). Bloqué par un allié ou hors d'atteinte → reste.
    /// </summary>
    private bool CanTakePlace(Cell from, Cell target)
    {
        LegalMoves(from, _placeBuffer);
        return _placeBuffer.Contains(target);
    }

    private Unit? ActiveUnitAt(Cell cell)
    {
        if (IsOver)
            return null;
        var unit = UnitAt(cell);
        return unit != null && unit.Faction == CurrentTurn ? unit : null;
    }

    private void MoveUnit(Cell from, Cell to)
    {
        _units[to.Column, to.Row] = _units[from.Column, from.Row];
        _units[from.Column, from.Row] = null;
    }

    private void EndTurn()
    {
        UpdateWinner();
        if (!IsOver)
            CurrentTurn = CurrentTurn.Opponent();
    }

    private void UpdateWinner()
    {
        bool hasPlayer = false, hasEnemy = false;
        foreach (var (_, unit) in Units())
        {
            if (unit.Faction == Faction.Player) hasPlayer = true;
            else hasEnemy = true;
        }

        // Une unité essentielle morte décide la partie, même si son camp a d'autres unités :
        // commandant tombé = défaite ; boss tué = victoire (combat de boss).
        bool playerLeaderDown = false, enemyLeaderDown = false;
        foreach (var unit in _essential)
        {
            if (unit.IsAlive) continue;
            if (unit.Faction == Faction.Player) playerLeaderDown = true;
            else enemyLeaderDown = true;
        }

        if (!hasPlayer || playerLeaderDown) Winner = Faction.Enemy;
        else if (!hasEnemy || enemyLeaderDown) Winner = Faction.Player;
    }
}
