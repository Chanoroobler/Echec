namespace Echec.Core.Battle;

/// <summary>
/// Domaine = motif de déplacement, nommé d'après la pièce d'échecs correspondante. La DAME donne les
/// 8 directions (base des unités de troupe : Soldat & Cie) ; le Fou les diagonales, la Tour les lignes
/// orthogonales, le Cavalier le saut en L. Le domaine ne porte QUE les directions et le type de
/// déplacement ; la distance (portée) vient de la classe.
/// </summary>
public enum Domaine
{
    Fou,
    Cavalier,
    Tour,
    Dame
}

/// <summary>Type de déplacement : glissé (s'arrête sur obstacle) ou sauté (par-dessus).</summary>
public enum MovementKind
{
    Slide,
    Jump
}
