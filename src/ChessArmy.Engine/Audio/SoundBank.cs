using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework.Audio;

namespace ChessArmy.Engine.Audio;

/// <summary>
/// Banque d'effets sonores pilotée par un fichier de configuration (sounds.json) qui
/// associe une <b>clé d'action</b> (ex. "unit_move") à un <b>fichier WAV</b>. Permet de
/// remplacer n'importe quel son en éditant le JSON, sans toucher au code.
///
/// Les WAV sont chargés via <see cref="SoundEffect.FromStream"/> (PCM 16 bits uniquement),
/// dans le même esprit que les sprites chargés par <c>Texture2D.FromStream</c>. Tout est en
/// repli silencieux : fichier de config absent, clé inconnue ou WAV illisible → no-op.
///
/// Une clé peut pointer vers UN fichier ("clé": "a.wav") ou une LISTE de variantes
/// ("clé": ["a.wav", "b.wav"]) : <see cref="Play"/> en tire alors une au hasard, jamais la même
/// deux fois de suite. Un même WAV partagé par plusieurs clés n'est chargé qu'une fois.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Variantes d'une clé + dernière jouée (pour ne pas répéter la même deux fois de suite).</summary>
    private sealed class Entry
    {
        public required SoundEffect[] Variants;
        public int Last = -1;
    }

    private readonly AudioManager _audio;
    private readonly Dictionary<string, Entry> _sounds = new();
    private readonly Dictionary<string, SoundEffect> _loaded = new(StringComparer.OrdinalIgnoreCase);   // chemin → WAV (dédoublonnage)
    private readonly Random _rng = new();

    public SoundBank(AudioManager audio) => _audio = audio;

    /// <summary>
    /// Charge la table action→fichier depuis <paramref name="configPath"/> ; les chemins de
    /// fichiers y sont relatifs à <paramref name="soundsRoot"/>. Silencieux si absent/illisible.
    /// </summary>
    public void Load(string configPath, string soundsRoot)
    {
        if (!File.Exists(configPath))
            return;

        Dictionary<string, JsonElement>? map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(configPath), Options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"sounds.json ignoré (invalide) : {ex.Message}");
            return;
        }

        if (map == null)
            return;

        var variants = new List<SoundEffect>();
        foreach (var (key, value) in map)
        {
            variants.Clear();
            if (value.ValueKind == JsonValueKind.String)
                AddVariant(variants, value.GetString(), soundsRoot);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        AddVariant(variants, item.GetString(), soundsRoot);

            if (variants.Count > 0)
                _sounds[key] = new Entry { Variants = variants.ToArray() };
        }
    }

    private void AddVariant(List<SoundEffect> variants, string? relative, string soundsRoot)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return;
        var path = Path.GetFullPath(Path.Combine(soundsRoot, relative));
        if (!_loaded.TryGetValue(path, out var sound))
        {
            sound = LoadWavOrNull(path);
            if (sound == null)
                return;
            _loaded[path] = sound;
        }
        variants.Add(sound);
    }

    /// <summary>Joue le son associé à <paramref name="key"/> (no-op si la clé est inconnue/non chargée). Clé à
    /// plusieurs variantes : une au hasard, différente de la précédente.</summary>
    public void Play(string key, float gain = 1f)
    {
        if (!_sounds.TryGetValue(key, out var entry))
            return;
        var n = entry.Variants.Length;
        var i = 0;
        if (n > 1)
        {
            // Tirage parmi les n-1 autres : décalage depuis la dernière jouée → jamais deux fois la même.
            i = entry.Last < 0 ? _rng.Next(n) : (entry.Last + 1 + _rng.Next(n - 1)) % n;
            entry.Last = i;
        }
        _audio.Play(entry.Variants[i], gain);
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
        foreach (var sound in _loaded.Values)   // chaque WAV une seule fois, même partagé par plusieurs clés
            sound.Dispose();
        _loaded.Clear();
        _sounds.Clear();
    }
}
