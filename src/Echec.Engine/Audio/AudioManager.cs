using Echec.Engine.Settings;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;

namespace Echec.Engine.Audio;

/// <summary>
/// Service audio. Applique les volumes joueur (0..100) à trois niveaux :
///  - <b>Master</b> (global) : multiplie TOUT (effets + musique) ;
///  - <b>Sfx</b> : appliqué aux effets sonores ;
///  - <b>Music</b> : appliqué à la musique (<see cref="MediaPlayer"/>).
/// Effet final : effets = Master×Sfx, musique = Master×Music. Le master est appliqué à la
/// main des deux côtés ; on laisse <see cref="SoundEffect.MasterVolume"/> à 1 pour ne pas
/// le compter deux fois sur les effets. (Modèle porté de CosyFarmer.)
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

    /// <summary>Relit les volumes des réglages et les applique au moteur audio.</summary>
    public void Apply()
    {
        _master = Clamp01(_settings.Master / 100f);
        _music = Clamp01(_settings.Music / 100f);
        _sfx = Clamp01(_settings.Sfx / 100f);

        try
        {
            SoundEffect.MasterVolume = 1f;
            MediaPlayer.Volume = _master * _music;
        }
        catch
        {
            // Pas de périphérique audio : les valeurs restent stockées.
        }
    }

    /// <summary>
    /// Joue un effet au volume effets×master, atténué par <paramref name="gain"/> (0..1).
    /// No-op si <paramref name="sound"/> est null.
    /// </summary>
    public void Play(SoundEffect? sound, float gain = 1f)
    {
        if (sound == null) return;
        try { sound.Play(Clamp01(_master * _sfx * gain), 0f, 0f); }
        catch { /* pas de périphérique audio : silencieux */ }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
