using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace Echec.Engine.Audio;

/// <summary>Contexte musical demandé par le jeu ; le <see cref="MusicPlayer"/> choisit la piste.</summary>
public enum MusicScene
{
    /// <summary>Aucune musique (fondu vers le silence).</summary>
    None,
    /// <summary>Menu principal + placement : piste « Relaxed » en boucle.</summary>
    Calm,
    /// <summary>Combat normal + recrutement / fin : la playlist qui tourne.</summary>
    Combat,
    /// <summary>Combat de boss : piste « Fight 2 » en boucle.</summary>
    Boss,
}

/// <summary>
/// Lecteur de musique de fond. Les pistes (gros WAV) sont lues à la demande sur un thread
/// d'arrière-plan, jouées via des <see cref="SoundEffectInstance"/> et enchaînées EN FONDU
/// (jamais de coupure sèche). Trois contextes : <see cref="MusicScene.Calm"/> (boucle Relaxed),
/// <see cref="MusicScene.Combat"/> (playlist qui tourne) et <see cref="MusicScene.Boss"/>
/// (boucle Fight 2). Le mapping contexte → fichier(s) est piloté par <c>music.json</c>.
///
/// Modèle aligné sur <see cref="SoundBank"/> : WAV brut sans pipeline de contenu, repli silencieux
/// total (config absente / WAV illisible / pas de périphérique audio). Le PCM est extrait à la main
/// (chunks parasites JUNK/LIST sautés) puis confié au constructeur de <see cref="SoundEffect"/>.
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    // Durées de fondu (s) : enchaînement de CONTEXTE vs piste suivante de la PLAYLIST.
    private const float SceneFadeSeconds = 1.5f;
    private const float TrackFadeSeconds = 1.2f;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AudioManager _audio;
    private readonly Random _rng = new();
    private string _root = "";

    // Pistes mappées par music.json.
    private string? _calm;
    private string? _boss;
    private readonly List<string> _playlist = new();
    private int _playlistIndex;

    // Canaux de lecture (plusieurs en même temps seulement le temps d'un fondu enchaîné).
    private readonly List<Channel> _channels = new();
    private Channel? _lead;   // canal « montant » : celui qui doit s'entendre à plein volume

    // Piste de playlist MISE EN PAUSE en quittant le combat : conservée pour la reprendre EXACTEMENT
    // à sa position au retour en combat (au lieu de la rejouer depuis le début). Au plus une à la fois.
    private Channel? _suspended;

    private MusicScene _scene = MusicScene.None;

    // Chargement asynchrone d'UNE piste à la fois (lecture + décodage WAV hors thread principal) :
    // tant qu'elle n'est pas prête, la piste courante continue → aucun blanc à l'enchaînement.
    private string? _pendingPath;
    private bool _pendingLoop;
    private Task<WavData?>? _pendingLoad;

    public MusicPlayer(AudioManager audio) => _audio = audio;

    /// <summary>
    /// Charge le mapping contexte → fichier(s) depuis <paramref name="configPath"/> (chemins relatifs
    /// à <paramref name="soundsRoot"/>). La playlist est mélangée une fois. Silencieux si absent/invalide.
    /// </summary>
    public void Load(string configPath, string soundsRoot)
    {
        _root = soundsRoot;
        if (!File.Exists(configPath))
            return;

        try
        {
            var cfg = JsonSerializer.Deserialize<MusicConfig>(File.ReadAllText(configPath), Options);
            if (cfg == null)
                return;

            _calm = NullIfBlank(cfg.Calm);
            _boss = NullIfBlank(cfg.Boss);
            if (cfg.Playlist != null)
                foreach (var p in cfg.Playlist)
                    if (!string.IsNullOrWhiteSpace(p))
                        _playlist.Add(p);

            Shuffle(_playlist);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"music.json ignoré (invalide) : {ex.Message}");
        }
    }

    /// <summary>
    /// Demande un contexte musical. IDEMPOTENT : redemander le contexte déjà en cours ne coupe ni ne
    /// relance rien (la playlist garde sa place). Le changement se fait toujours en fondu.
    /// </summary>
    public void Play(MusicScene scene)
    {
        if (scene == _scene)
            return;
        _scene = scene;

        switch (scene)
        {
            case MusicScene.Calm: Request(_calm, loop: true); break;
            case MusicScene.Boss: Request(_boss, loop: true); break;
            case MusicScene.Combat:
                if (_playlist.Count == 0)
                {
                    Request(null, loop: false);
                }
                else
                {
                    // On reprend la piste suspendue à sa position si possible, sinon on la (re)charge.
                    var path = _playlist[_playlistIndex % _playlist.Count];
                    if (!TryResumeCombat(path))
                        Request(path, loop: false);
                }
                break;
            default: Request(null, loop: false); break;   // None
        }
    }

    /// <summary>Fait avancer les fondus, démarre la piste prête à jouer et enchaîne la playlist.</summary>
    public void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // 1) Piste en attente chargée → on la démarre (montée en fondu, les autres descendent).
        if (_pendingLoad is { IsCompleted: true })
        {
            var data = SafeResult(_pendingLoad);
            var path = _pendingPath;
            var loop = _pendingLoop;
            _pendingLoad = null;
            _pendingPath = null;
            if (data is { } wav && path != null)
                StartChannel(wav, path, loop);
            // Lecture échouée (data null) : on laisse jouer l'existant, sans planter.
        }

        // 2) Avance des fondus + volume effectif (master×music, relu en direct pour suivre les options).
        var music = _audio.MusicVolume;
        for (var i = _channels.Count - 1; i >= 0; i--)
        {
            var ch = _channels[i];
            ch.Level = Approach(ch.Level, ch.Target, ch.FadeSpeed * dt);

            var stopped = !ch.Loop && SafeState(ch.Instance) == SoundState.Stopped;
            var fadedOut = ch.Target <= 0f && ch.Level <= 0f;
            // Retiré quand il a fini de descendre, ou quand une piste non bouclée s'est terminée (hors tête).
            if (fadedOut || (stopped && ch != _lead))
            {
                if (ch == _lead) _lead = null;
                _channels.RemoveAt(i);
                // Piste de playlist quittée EN COURS de lecture (pas terminée) hors du combat → mise en
                // pause pour reprendre au même endroit ; sinon (terminée / piste calme) on la détruit.
                if (fadedOut && ch.Suspendable && !stopped && _scene != MusicScene.Combat)
                    Suspend(ch);
                else
                    ch.Dispose();
                continue;
            }

            try { ch.Instance.Volume = Clamp01(ch.Level * music); } catch { /* périphérique perdu */ }
        }

        // 3) Playlist : la piste de tête s'est terminée → enchaîner la suivante (en fondu).
        if (_scene == MusicScene.Combat && _pendingLoad == null && _playlist.Count > 0
            && _lead is { Loop: false } lead && SafeState(lead.Instance) == SoundState.Stopped)
        {
            _playlistIndex = (_playlistIndex + 1) % _playlist.Count;
            Request(_playlist[_playlistIndex], loop: false);
        }
    }

    public void Dispose()
    {
        foreach (var ch in _channels)
            ch.Dispose();
        _channels.Clear();
        _suspended?.Dispose();
        _suspended = null;
        _lead = null;
    }

    // ── Interne ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Programme <paramref name="path"/> comme nouvelle piste « montante » : son WAV est lu en fond ;
    /// la piste courante continue tant qu'il n'est pas prêt. <paramref name="path"/> null = silence.
    /// </summary>
    private void Request(string? path, bool loop)
    {
        if (path == null)
        {
            _pendingPath = null;
            _pendingLoad = null;
            FadeOutAll();
            _lead = null;
            return;
        }

        // Déjà la piste montante (en cours de chargement ou déjà de tête) → rien à faire.
        if (_pendingLoad != null && _pendingPath == path) return;
        if (_pendingLoad == null && _lead is { } l && l.Path == path && l.Target > 0f) return;

        _pendingPath = path;
        _pendingLoop = loop;
        var full = Path.Combine(_root, path);
        _pendingLoad = Task.Run(() => ReadWav(full));
    }

    /// <summary>Crée le canal de la piste chargée, le fait monter, et fait descendre tous les autres.</summary>
    private void StartChannel(WavData wav, string path, bool loop)
    {
        SoundEffect sound;
        SoundEffectInstance instance;
        try
        {
            sound = new SoundEffect(wav.Pcm, wav.SampleRate, wav.Channels);
            instance = sound.CreateInstance();
            instance.IsLooped = loop;
            instance.Volume = 0f;
            instance.Play();
        }
        catch
        {
            return;   // pas de périphérique audio / création impossible : silencieux
        }

        var lead = new Channel
        {
            Sound = sound,
            Instance = instance,
            Path = path,
            Level = 0f,
            Loop = loop,
            Suspendable = !loop,   // seules les pistes de playlist (non bouclées) se reprennent à la position
        };
        _channels.Add(lead);
        Promote(lead, _scene == MusicScene.Combat && !loop ? TrackFadeSeconds : SceneFadeSeconds);
    }

    /// <summary>
    /// Reprend la piste de combat à sa POSITION (mise en pause, ou encore en train de descendre) au lieu
    /// de la recharger depuis le début. Renvoie faux s'il n'y a rien à reprendre (→ chargement normal).
    /// </summary>
    private bool TryResumeCombat(string path)
    {
        // 1) Piste mise en pause en quittant le combat : on la relance là où elle s'était arrêtée.
        if (_suspended is { } s)
        {
            if (s.Path == path)
            {
                _suspended = null;
                CancelPending();
                try { s.Instance.Resume(); } catch { /* périphérique perdu */ }
                _channels.Add(s);
                Promote(s, TrackFadeSeconds);
                return true;
            }
            s.Dispose();        // piste suspendue obsolète (ne devrait pas arriver) : on la jette
            _suspended = null;
        }

        // 2) Retour TRÈS rapide : la piste descend encore (jamais suspendue) → on la re-promeut telle quelle.
        foreach (var ch in _channels)
            if (ch.Suspendable && ch.Path == path && SafeState(ch.Instance) != SoundState.Stopped)
            {
                CancelPending();
                Promote(ch, TrackFadeSeconds);
                return true;
            }

        return false;
    }

    /// <summary>Fait monter <paramref name="ch"/> (nouvelle tête) en <paramref name="fadeIn"/> s ; les autres descendent.</summary>
    private void Promote(Channel ch, float fadeIn)
    {
        foreach (var other in _channels)
            if (other != ch)
            {
                other.Target = 0f;
                other.FadeSpeed = 1f / SceneFadeSeconds;
            }
        ch.Target = 1f;
        ch.FadeSpeed = 1f / fadeIn;
        _lead = ch;
    }

    /// <summary>Met une piste de playlist en pause (conserve sa position) pour une reprise ultérieure.</summary>
    private void Suspend(Channel ch)
    {
        try { ch.Instance.Pause(); } catch { /* périphérique perdu */ }
        _suspended?.Dispose();   // au plus une piste suspendue à la fois
        _suspended = ch;
    }

    private void CancelPending()
    {
        _pendingPath = null;
        _pendingLoad = null;
    }

    private void FadeOutAll()
    {
        foreach (var ch in _channels)
        {
            ch.Target = 0f;
            ch.FadeSpeed = 1f / SceneFadeSeconds;
        }
    }

    private void Shuffle(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static float Approach(float value, float target, float step) =>
        value < target ? Math.Min(target, value + step)
        : value > target ? Math.Max(target, value - step)
        : value;

    private static SoundState SafeState(SoundEffectInstance instance)
    {
        try { return instance.State; }
        catch { return SoundState.Stopped; }
    }

    private static WavData? SafeResult(Task<WavData?> task)
    {
        try { return task.IsCompletedSuccessfully ? task.Result : null; }
        catch { return null; }
    }

    /// <summary>Lit le fichier puis en extrait le PCM (hors thread principal). Null si illisible.</summary>
    private static WavData? ReadWav(string path)
    {
        try { return ParseWav(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>
    /// Extrait le PCM d'un WAV (RIFF) en SAUTANT les chunks parasites (JUNK, LIST, fact…) que
    /// <see cref="SoundEffect.FromStream"/> ne digère pas toujours, et en RAMENANT le 24 bits à du
    /// 16 bits (seul format, avec le 8 bits, accepté par <see cref="SoundEffect"/>). Ces pistes sont
    /// exportées en 24 bits/48 kHz stéréo. Null si non-PCM / résolution inattendue.
    /// </summary>
    private static WavData? ParseWav(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return null;
        if (b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return null;

        int format = 0, channels = 0, sampleRate = 0, bits = 0, dataOffset = -1, dataSize = 0;

        var pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = b[pos + 4] | (b[pos + 5] << 8) | (b[pos + 6] << 16) | (b[pos + 7] << 24);
            pos += 8;
            if (size < 0 || pos + size > b.Length) break;

            if (id == "fmt " && size >= 16)
            {
                format = b[pos] | (b[pos + 1] << 8);
                channels = b[pos + 2] | (b[pos + 3] << 8);
                sampleRate = b[pos + 4] | (b[pos + 5] << 8) | (b[pos + 6] << 16) | (b[pos + 7] << 24);
                bits = b[pos + 14] | (b[pos + 15] << 8);
            }
            else if (id == "data")
            {
                dataOffset = pos;
                dataSize = size;
            }

            pos += size;
            if ((size & 1) != 0) pos++;   // les chunks RIFF sont alignés sur 2 octets
        }

        if (dataOffset < 0 || format != 1 || channels is < 1 or > 2 || sampleRate <= 0)
            return null;

        var pcm16 = bits switch
        {
            16 => Slice(b, dataOffset, dataSize),
            24 => Pcm24To16(b, dataOffset, dataSize),
            _ => null,
        };
        if (pcm16 == null)
            return null;

        return new WavData(pcm16, sampleRate, channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
    }

    private static byte[] Slice(byte[] src, int offset, int count)
    {
        var dst = new byte[count];
        Array.Copy(src, offset, dst, 0, count);
        return dst;
    }

    /// <summary>
    /// Convertit du PCM 24 bits little-endian en 16 bits en ne gardant que les 2 octets de poids fort
    /// de chaque échantillon (l'octet de poids faible est tronqué : perte inaudible). Indépendant du
    /// nombre de canaux (les échantillons sont entrelacés, traités en bloc).
    /// </summary>
    private static byte[] Pcm24To16(byte[] src, int offset, int count)
    {
        var samples = count / 3;
        var dst = new byte[samples * 2];
        for (int i = 0, s = offset; i < dst.Length; i += 2, s += 3)
        {
            dst[i] = src[s + 1];       // octet médian (8 bits faibles du résultat)
            dst[i + 1] = src[s + 2];   // octet de poids fort (avec le signe)
        }
        return dst;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Une piste en cours de lecture, avec son fondu propre.</summary>
    private sealed class Channel
    {
        public SoundEffect Sound = null!;
        public SoundEffectInstance Instance = null!;
        public string Path = "";
        public float Level;       // niveau de fondu courant, 0..1
        public float Target;      // niveau visé, 0..1
        public float FadeSpeed;   // variation de niveau par seconde
        public bool Loop;
        public bool Suspendable;  // piste de playlist : mise en pause (et non détruite) en quittant le combat

        public void Dispose()
        {
            try { Instance.Stop(); } catch { /* ignore */ }
            Instance.Dispose();
            Sound.Dispose();
        }
    }

    /// <summary>PCM 16 bits extrait d'un WAV, prêt pour le constructeur de <see cref="SoundEffect"/>.</summary>
    private readonly struct WavData
    {
        public WavData(byte[] pcm, int sampleRate, AudioChannels channels)
        {
            Pcm = pcm;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public readonly byte[] Pcm;
        public readonly int SampleRate;
        public readonly AudioChannels Channels;
    }

    private sealed class MusicConfig
    {
        public string? Calm { get; set; }
        public string? Boss { get; set; }
        public List<string>? Playlist { get; set; }
    }
}
