namespace Echec.Engine.Settings;

/// <summary>
/// Volumes audio en pourcentage (0..100), modèle à trois niveaux comme les options :
/// <see cref="Master"/> multiplie effets ET musique ; <see cref="Music"/> et
/// <see cref="Sfx"/> s'appliquent à leur catégorie.
/// </summary>
public sealed class AudioSettings
{
    /// <summary>Volume global : multiplie effets ET musique, 0..100.</summary>
    public int Master = 80;

    /// <summary>Volume de la musique / ambiance, 0..100.</summary>
    public int Music = 80;

    /// <summary>Volume des effets sonores (feedback UI / actions), 0..100.</summary>
    public int Sfx = 80;
}
