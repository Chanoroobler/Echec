using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

/// <summary>
/// État et règles d'une partie : grille d'unités, tour courant, résolution des
/// déplacements et du combat, condition de victoire. Domaine pur (aucun rendu).
///
/// Combat : se déplacer sur une case ennemie inflige les dégâts de l'attaquant.
/// Si la cible meurt, l'attaquant prend sa place ; sinon il reste sur place.
/// Un seul déplacement par tour, puis la main passe au camp adverse.
/// </summary>
public sealed class Match
{
    private readonly Unit?[,] _units;

    public Match(int width, int height)
    {
        Width = width;
        Height = height;
        _units = new Unit?[width, height];
    }

    public int Width { get; }
    public int Height { get; }
    public Faction CurrentTurn { get; private set; } = Faction.Player;
    public Faction? Winner { get; private set; }
    public bool IsOver => Winner != null;

    public bool InBounds(Cell cell) =>
        cell.Column >= 0 && cell.Column < Width && cell.Row >= 0 && cell.Row < Height;

    public Unit? UnitAt(Cell cell) => InBounds(cell) ? _units[cell.Column, cell.Row] : null;

    public void Place(Cell cell, Unit unit) => _units[cell.Column, cell.Row] = unit;

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

    /// <summary>Cases atteignables (vides ou occupées par un ennemi) pour l'unité en <paramref name="from"/>.</summary>
    public List<Cell> LegalMoves(Cell from)
    {
        var result = new List<Cell>();
        var unit = UnitAt(from);
        if (unit == null || unit.Faction != CurrentTurn || IsOver)
            return result;

        foreach (var offset in MovementRules.Offsets(unit.Type))
        {
            var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
            if (!InBounds(to))
                continue;

            var target = _units[to.Column, to.Row];
            if (target == null || target.Faction != unit.Faction)
                result.Add(to); // case vide ou ennemi (attaque)
        }

        return result;
    }

    /// <summary>
    /// Tente le déplacement <paramref name="from"/> → <paramref name="to"/> pour le camp
    /// courant. Résout le combat, fait passer le tour en cas de succès.
    /// </summary>
    public MoveKind TryMove(Cell from, Cell to)
    {
        if (IsOver)
            return MoveKind.Invalid;

        var unit = UnitAt(from);
        if (unit == null || unit.Faction != CurrentTurn)
            return MoveKind.Invalid;

        if (!LegalMoves(from).Contains(to))
            return MoveKind.Invalid;

        var target = _units[to.Column, to.Row];
        MoveKind kind;

        if (target == null)
        {
            MoveUnit(from, to);
            kind = MoveKind.Moved;
        }
        else
        {
            target.TakeDamage(unit.Damage);
            if (!target.IsAlive)
            {
                MoveUnit(from, to);
                kind = MoveKind.Killed;
            }
            else
            {
                kind = MoveKind.Attacked; // l'attaquant reste sur place
            }
        }

        EndTurn();
        return kind;
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

        if (!hasPlayer) Winner = Faction.Enemy;
        else if (!hasEnemy) Winner = Faction.Player;
    }
}
