using System;
using System.Collections.Generic;
using ChessArmy.Engine.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Cœurs du renard caressé : quelques cœurs (<c>Assets/Anim/CoeurFox.png</c>) sortent de sa tête un à un,
/// montent en se dandinant puis ÉCLATENT en petite gerbe rose (particules de <see cref="SparkBurst"/>).
/// Positions en px canvas (repère du clic), calées sur la grille des pixels d'art. Tampon fixe, zéro alloc.
/// </summary>
internal sealed class HeartBurst
{
    private const int MaxHearts = 12;

    private struct Heart
    {
        public bool Active;
        public float Delay;      // attente avant d'apparaître (sortie égrenée)
        public Vector2 Pos;      // px canvas (centre)
        public float Rise;       // vitesse de montée, px / s
        public float SwayPhase;
        public float Life;       // temps restant avant d'éclater
    }

    private readonly Heart[] _hearts = new Heart[MaxHearts];
    private readonly Random _rng = new();
    private Texture2D? _sprite;
    private Color[] _colors = Array.Empty<Color>();   // couleurs du cœur : gerbe de l'éclatement

    public void Load(GraphicsDevice gd, string path)
    {
        _sprite = Textures.LoadPngOrNull(gd, path);
        if (_sprite == null)
            return;
        var data = new Color[_sprite.Width * _sprite.Height];
        _sprite.GetData(data);
        var set = new HashSet<Color>();
        foreach (var c in data)
            if (c.A > 128 && c.R + c.G + c.B > 150)   // couleurs claires (contour sombre exclu)
                set.Add(new Color(c.R, c.G, c.B));
        _colors = new Color[set.Count];
        set.CopyTo(_colors);
    }

    public void Unload()
    {
        _sprite?.Dispose();
        _sprite = null;
        Clear();
    }

    /// <summary>Vrai tant qu'un cœur est en attente, en vol (pas encore éclaté).</summary>
    public bool HasActive
    {
        get
        {
            for (var i = 0; i < MaxHearts; i++)
                if (_hearts[i].Active)
                    return true;
            return false;
        }
    }

    public void Clear()
    {
        for (var i = 0; i < MaxHearts; i++)
            _hearts[i].Active = false;
    }

    /// <summary>Fait sortir <paramref name="count"/> cœurs de <paramref name="origin"/> (px canvas), un à un.</summary>
    public void Spawn(Vector2 origin, int count, float pixel)
    {
        if (_sprite == null)
            return;
        var spawned = 0;
        for (var i = 0; i < MaxHearts && spawned < count; i++)
        {
            if (_hearts[i].Active)
                continue;
            _hearts[i] = new Heart
            {
                Active = true,
                Delay = spawned * (0.1f + (float)_rng.NextDouble() * 0.08f),
                Pos = origin + new Vector2(((float)_rng.NextDouble() - 0.5f) * 8f * pixel, 0f),
                Rise = (22f + (float)_rng.NextDouble() * 12f) * pixel,
                SwayPhase = (float)_rng.NextDouble() * MathF.Tau,
                Life = 0.7f + (float)_rng.NextDouble() * 0.45f,
            };
            spawned++;
        }
    }

    /// <summary>Avance les cœurs ; ceux arrivés en fin de vie éclatent dans <paramref name="sparks"/>.</summary>
    public void Update(float dt, SparkBurst sparks, float pixel)
    {
        for (var i = 0; i < MaxHearts; i++)
        {
            ref var h = ref _hearts[i];
            if (!h.Active)
                continue;
            if (h.Delay > 0f)
            {
                h.Delay -= dt;
                continue;
            }
            h.Life -= dt;
            h.Pos.Y -= h.Rise * dt;
            h.SwayPhase += dt * 7f;
            if (h.Life <= 0f)
            {
                h.Active = false;
                sparks.EmitSplash(h.Pos, 8, Math.Max(1, (int)MathF.Round(pixel)), _colors);   // pop !
            }
        }
    }

    /// <summary>Dessine les cœurs visibles (batch dédié). <paramref name="pixel"/> = taille d'un pixel d'art.</summary>
    public void Draw(SpriteBatch sb, int pixel)
    {
        if (_sprite == null)
            return;
        var begun = false;
        for (var i = 0; i < MaxHearts; i++)
        {
            ref readonly var h = ref _hearts[i];
            if (!h.Active || h.Delay > 0f)
                continue;
            if (!begun)
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                begun = true;
            }
            var sway = MathF.Sin(h.SwayPhase) * 2f;   // dandinement de ±2 pixels d'art
            var w = _sprite.Width * pixel;
            var hgt = _sprite.Height * pixel;
            var x = (int)MathF.Round((h.Pos.X - w / 2f) / pixel + sway) * pixel;
            var y = (int)MathF.Round((h.Pos.Y - hgt / 2f) / pixel) * pixel;
            sb.Draw(_sprite, new Rectangle(x, y, w, hgt), Color.White);
        }
        if (begun)
            sb.End();
    }
}
