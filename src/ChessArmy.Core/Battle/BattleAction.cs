using System;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Battle;

/// <summary>Nature d'une <see cref="BattleAction"/>. Valeurs FIGÉES (sérialisées) : n'ajouter qu'en fin de liste.</summary>
public enum BattleActionKind : byte
{
    /// <summary>Le camp actif passe son tour (cf. <see cref="Match.PassTurn"/>).</summary>
    Pass = 0,
    Move = 1,
    Attack = 2,
    /// <summary>« Chair à canon » : <see cref="BattleAction.From"/> lance un allié sur <see cref="BattleAction.Target"/>.</summary>
    Throw = 3,
    /// <summary>« Soin » : <see cref="BattleAction.From"/> soigne l'allié de <see cref="BattleAction.Target"/>.</summary>
    Heal = 4,
    /// <summary>« Sacoche aimantée » : geste de CAMPAGNE (la scène applique l'effet), le moteur consomme l'action.</summary>
    SatchelPull = 5,
    /// <summary>Fin de partie : le camp <c>From.Column</c> (0 = Player, 1 = Enemy) perd (temps écoulé, abandon).</summary>
    Resign = 6,
    /// <summary>Fin de partie : égalité acceptée par les deux joueurs.</summary>
    AgreeDraw = 7,
}

/// <summary>
/// UN coup de combat, réduit à des données : le type et deux cases. C'est l'unité d'échange d'un futur mode
/// multijoueur (lockstep : chaque client rejoue les coups de l'autre sur un <see cref="Match"/> construit avec la
/// même graine) et de tout replay/journal de combat. Tout coup passe par <see cref="Match.Apply"/>, jamais par
/// un appel direct à TryMove/TryAttack… depuis la scène, pour qu'aucun chemin n'échappe au journal.
/// </summary>
public readonly record struct BattleAction(BattleActionKind Kind, Cell From, Cell Target)
{
    /// <summary>Taille FIXE d'un coup sérialisé (cf. <see cref="Write"/>) : type + 4 coordonnées sur un octet.</summary>
    public const int SerializedSize = 5;

    public static BattleAction Pass => new(BattleActionKind.Pass, default, default);
    public static BattleAction Move(Cell from, Cell to) => new(BattleActionKind.Move, from, to);
    public static BattleAction Attack(Cell from, Cell target) => new(BattleActionKind.Attack, from, target);
    public static BattleAction Throw(Cell from, Cell target) => new(BattleActionKind.Throw, from, target);
    public static BattleAction Heal(Cell from, Cell target) => new(BattleActionKind.Heal, from, target);
    public static BattleAction SatchelPull(Cell from, Cell satchel) => new(BattleActionKind.SatchelPull, from, satchel);
    public static BattleAction Resign(Faction loser) => new(BattleActionKind.Resign, new Cell((int)loser, 0), default);
    public static BattleAction AgreeDraw => new(BattleActionKind.AgreeDraw, default, default);

    /// <summary>Écrit le coup sur <see cref="SerializedSize"/> octets (sans allocation). Les plateaux tiennent
    /// largement sur un octet par coordonnée.</summary>
    public void Write(Span<byte> dest)
    {
        if (dest.Length < SerializedSize)
            throw new ArgumentException("Tampon trop court pour un BattleAction.", nameof(dest));
        dest[0] = (byte)Kind;
        dest[1] = checked((byte)From.Column);
        dest[2] = checked((byte)From.Row);
        dest[3] = checked((byte)Target.Column);
        dest[4] = checked((byte)Target.Row);
    }

    /// <summary>Relit un coup écrit par <see cref="Write"/>.</summary>
    public static BattleAction Read(ReadOnlySpan<byte> src)
    {
        if (src.Length < SerializedSize)
            throw new ArgumentException("Tampon trop court pour un BattleAction.", nameof(src));
        return new BattleAction((BattleActionKind)src[0], new Cell(src[1], src[2]), new Cell(src[3], src[4]));
    }
}
