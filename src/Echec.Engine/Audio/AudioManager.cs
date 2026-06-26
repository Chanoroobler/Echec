using Echec.Engine.Settings;
using Microsoft.Xna.Framework.Audio;

namespace Echec.Engine.Audio;

/// <summary>
/// Service audio. Applique les volumes joueur (0..100) à trois niveaux :
///  - <b>Master</b> (global) : multiplie TOUT (effets + musique) ;
///  - <b>Sfx</b> : appliqué aux effets sonores ;
///  - <b>Music</b> : appliqué à la musique.
/// Effet final : effets = Master×Sfx, musique = Master×Music. Le master est appliqué à la
/// main des deux côtés ; on laisse <see cref="SoundEffect.MasterVolume"/> à 1 pour ne pas
/// le compter deux fois sur les effets. (Modèle porté de CosyFarmer.)
///
/// <para>Chaque curseur passe par une <b>courbe perceptive</b> (<see cref="Perceptual"/>, x²) :
/// l'oreille est ~logarithmique, donc une rampe LINÉAIRE d'amplitude donne l'impression que
/// la moitié haute du curseur ne change presque rien (50 % ≈ −6 dB seulement). Le carré étale
/// la sensation de fort sur toute la course (50 % ≈ −12 dB, 100 % = 0 dB).</para>
/// </summary>
public sealed class AudioManager
{
    private readonly AudioSettings _settings;

    private float _master = 1f;
    private float _music = 1f;
    private float _sfx = 1f;

    public AudioManager(AudioSettings settings)
    {
        _settings = settings;
        Apply();
    }

    /// <summary>
    /// Volume effectif de la musique (master×music après courbe perceptive, 0..1), relu en direct par
    /// le <see cref="MusicPlayer"/> qui pilote lui-même ses <see cref="SoundEffectInstance"/>.
    /// </summary>
    public float MusicVolume => Clamp01(Perceptual(_master) * Perceptual(_music));

    /// <summary>Relit les volumes des réglages et les applique au moteur audio.</summary>
    public void Apply()
    {
        _master = Clamp01(_settings.Master / 100f);
        _music = Clamp01(_settings.Music / 100f);
        _sfx = Clamp01(_settings.Sfx / 100f);

        try
        {
            SoundEffect.MasterVolume = 1f;
        }
        catch
        {
            // Pas de périphérique audio : les valeurs restent stockées.
        }
    }

    /// <summary>
    /// Joue un effet au volume effets×master (courbe perceptive), atténué par <paramref name="gain"/>
    /// (0..1, atténuation de conception laissée linéaire). No-op si <paramref name="sound"/> est null.
    /// </summary>
    public void Play(SoundEffect? sound, float gain = 1f)
    {
        if (sound == null) return;
        try { sound.Play(Clamp01(Perceptual(_master) * Perceptual(_sfx) * gain), 0f, 0f); }
        catch { /* pas de périphérique audio : silencieux */ }
    }

    /// <summary>Courbe perceptive du volume : carré du curseur linéaire (oreille ~logarithmique).</summary>
    private static float Perceptual(float v) => v * v;

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
