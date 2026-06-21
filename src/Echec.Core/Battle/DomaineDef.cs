namespace Echec.Core.Battle;

/// <summary>
/// Paramétrage d'un domaine : sa classe de base (asset + stats). Le motif de
/// déplacement et le type (glissé/sauté) sont déterminés par le <see cref="Domaine"/>
/// lui-même (voir <see cref="Movement"/>). L'arbre de classes viendra plus tard ;
/// pour l'instant un domaine = une classe de base.
/// </summary>
public sealed class DomaineDef
{
    public DomaineDef(Domaine id, UnitClass baseClass)
    {
        Id = id;
        BaseClass = baseClass;
    }

    public Domaine Id { get; }
    public string Name => Id.ToString();
    public MovementKind MovementKind => Movement.Kind(Id);
    public UnitClass BaseClass { get; }
}
