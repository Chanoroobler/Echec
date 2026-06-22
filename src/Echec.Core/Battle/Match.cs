using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// État et règles d'une partie : grille d'unités, tour courant, déplacement et combat,
/// condition de victoire. Domaine pur (aucun rendu).
///
/// Un tour = UNE action : se DÉPLACER vers une case vide (jusqu'à la portée de
/// déplacement) OU ATTAQUER une cible à portée de tir. Les deux suivent les directions
/// du domaine. À l'attaque : si la cible meurt et que la portée de tir vaut 1 (mêlée,
/// ou saut du cavalier), l'attaquant prend sa place ; sinon il reste sur place.
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

        foreach (var dir in vectors)
        {
            for (var step = 1; step <= unit.MoveRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || _units[to.Column, to.Row] != null || BlocksMovement(to))
                    break; // hors plateau, unité, ou obstacle (eau/montagne) : on ne passe pas à travers
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
                    result.Add(to); // premier ennemi en vue : cible, et borne la ligne
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
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksLineOfFire(to))
                    break; // hors plateau ou montagne : la menace ne porte pas au-delà (l'eau laisse passer)

                var occupant = _units[to.Column, to.Row];
                if (occupant != null && occupant.Faction == unit.Faction && piercesAllies)
                    continue; // lancier : traverse l'allié sans le menacer, la ligne continue

                result.Add(to);
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
        victim.TakeDamage(unit.Damage);

        MoveKind kind;
        if (!victim.IsAlive)
        {
            _units[target.Column, target.Row] = null;
            if (TakesPlaceOnKill(unit))
                MoveUnit(from, target);
            kind = MoveKind.Killed;
        }
        else
        {
            kind = MoveKind.Attacked; // l'attaquant reste sur place
        }

        EndTurn();
        return kind;
    }

    /// <summary>Mêlée (tir 1) ou saut du cavalier : l'attaquant avance sur la case libérée.</summary>
    private static bool TakesPlaceOnKill(Unit unit) =>
        Movement.Kind(unit.Domaine) == MovementKind.Jump || unit.AttackRange == 1;

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
