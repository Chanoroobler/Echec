namespace Echec.Engine.Persistence;

/// <summary>
/// État de profil GLOBAL (un fichier <c>profile.json</c>, indépendant des slots de progression).
/// Sert à savoir si le joueur a déjà entamé une campagne : la toute PREMIÈRE bénéficie d'un
/// déblocage progressif plus doux des types ennemis (cf. <c>Run.FirstRun</c>).
/// </summary>
public sealed class ProfileDto
{
    /// <summary>Vrai dès que le joueur a démarré sa première campagne.</summary>
    public bool HasPlayedBefore { get; set; }
}
