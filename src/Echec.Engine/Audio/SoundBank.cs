using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework.Audio;

namespace Echec.Engine.Audio;

/// <summary>
/// Banque d'effets sonores pilotée par un fichier de configuration (sounds.json) qui
/// associe une <b>clé d'action</b> (ex. "unit_move") à un <b>fichier WAV</b>. Permet de
/// remplacer n'importe quel son en éditant le JSON, sans toucher au code.
///
/// Les WAV sont chargés via <see cref="SoundEffect.FromStream"/> (PCM 16 bits uniquement),
/// dans le même esprit que les sprites chargés par <c>Texture2D.FromStream</c>. Tout est en
/// repli silencieux : fichier de config absent, clé inconnue ou WAV illisible → no-op.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AudioManager _audio;
    private readonly Dictionary<string, SoundEffect> _sounds = new();

    public SoundBank(AudioManager audio) => _audio = audio;

    /// <summary>
    /// Charge la table action→fichier depuis <paramref name="configPath"/> ; les chemins de
    /// fichiers y sont relatifs à <paramref name="soundsRoot"/>. Silencieux si absent/illisible.
    /// </summary>
    public void Load(string configPath, string soundsRoot)
    {
        if (!File.Exists(configPath))
            return;

        Dictionary<string, string>? map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath), Options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"sounds.json ignoré (invalide) : {ex.Message}");
            return;
        }

        if (map == null)
            return;

        foreach (var (key, relative) in map)
        {
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            var sound = LoadWavOrNull(Path.Combine(soundsRoot, relative));
            if (sound != null)
                _sounds[key] = sound;
        }
    }

    /// <summary>Joue le son associé à <paramref name="key"/> (no-op si la clé est inconnue/non chargée).</summary>
    public void Play(string key, float gain = 1f)
    {
        if (_sounds.TryGetValue(key, out var sound))
            _audio.Play(sound, gain);
    }

    /// <summary>Charge un WAV (PCM) depuis le disque, ou <c>null</c> s'il est absent/illisible.</summary>
    private static SoundEffect? LoadWavOrNull(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return SoundEffect.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var sound in _sounds.Values)
            sound.Dispose();
        _sounds.Clear();
    }
}
