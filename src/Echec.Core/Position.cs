namespace Echec.Core;

/// <summary>
/// Case de l'échiquier en coordonnées colonne (File, 0=a) / rangée (Rank, 0=1).
/// </summary>
public readonly record struct Position(int File, int Rank)
{
    public bool IsOnBoard => File is >= 0 and < 8 && Rank is >= 0 and < 8;

    /// <summary>Notation algébrique, ex. (4, 0) => "e1".</summary>
    public override string ToString() => $"{(char)('a' + File)}{Rank + 1}";
}
