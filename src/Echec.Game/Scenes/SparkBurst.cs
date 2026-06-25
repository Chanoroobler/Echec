using System;
using System.Collections.Generic;
using Echec.Core.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Scenes;

/// <summary>Une étincelle d'impact : carré pixel qui fuse puis s'éteint. Mutable (recyclée par le pool).</summary>
internal sealed class Spark
{
    public Vector2 Position;   // px canvas
    public Vector2 Velocity;   // px/s
    public float Life;         // restant (s)
    public float MaxLife;
    public int Size;           // côté du carré, en px canvas
    public Color Color;
}

/// <summary>
/// Gerbe de particules pixel-art recyclées via <see cref="Pool{T}"/> (pas d'allocation). Soumises à
/// la gravité, s'éteignent par fondu postérisé, rendu en carrés alignés à la grille (pixel-perfect,
/// chunky). Seul usage restant : le feu d'artifice d'extinction des chiffres de dégâts (cf. <see
/// cref="EmitFirework"/>) — les étincelles d'impact/recrutement ont été retirées.
/// </summary>
internal sealed class SparkBurst
{
    private const float Gravity = 760f;        // px/s² (canvas)

    private readonly Pool<Spark> _pool = new(() => new Spark(), prewarm: 64);
    private readonly List<Spark> _active = new();
    private readonly Random _rng = new();

    // Teintes « feu d'artifice » (or, orange chaud, rouge vif, crème) — même entorse palette
    // assumée que les étincelles d'impact : ces FX doivent péter à l'écran.
    private static readonly Color[] FireworkColors =
    {
        new(255, 210, 90), new(255, 140, 60), new(255, 70, 70), new(255, 240, 200),
    };

    public bool HasActive => _active.Count > 0;

    /// <summary>Vide les particules en cours (au démarrage d'un nouveau combat) et les rend au pool.</summary>
    public void Clear()
    {
        foreach (var s in _active)
            _pool.Return(s);
        _active.Clear();
    }

    /// <summary>
    /// Gerbe RADIALE (360°) de particules colorées depuis <paramref name="origin"/> : un petit feu
    /// d'artifice. Vitesses variées + gravité (cf. <see cref="Update"/>) → éclat puis retombée.
    /// </summary>
    public void EmitFirework(Vector2 origin, int count, float pixel)
    {
        var size = Math.Max(2, (int)pixel);
        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            var ang = (float)(_rng.NextDouble() * Math.PI * 2);          // tout autour
            var speed = 120f + (float)_rng.NextDouble() * 220f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
            s.MaxLife = 0.35f + (float)_rng.NextDouble() * 0.30f;
            s.Life = s.MaxLife;
            s.Size = _rng.Next(4) == 0 ? size * 2 : size;               // quelques grosses braises
            s.Color = FireworkColors[_rng.Next(FireworkColors.Length)];
            _active.Add(s);
        }
    }

    public void Update(float dt)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var s = _active[i];
            s.Life -= dt;
            if (s.Life <= 0f)
            {
                _active.RemoveAt(i);
                _pool.Return(s);
                continue;
            }
            s.Velocity.Y += Gravity * dt;
            s.Position += s.Velocity * dt;
        }
    }

    public void Draw(SpriteBatch sb, Texture2D pixel)
    {
        if (_active.Count == 0)
            return;

        sb.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.PointClamp);
        foreach (var s in _active)
        {
            // OPAQUES la quasi-totalité de leur vie (visibilité), un SEUL palier de fondu sur la fin,
            // puis disparition nette (pas de transparence lisse qui les rend fades).
            var k = s.Life / s.MaxLife;                       // 1 → 0
            var alpha = k > 0.25f ? 1f : 0.5f;
            // Position calée sur la grille de blocs (carrés nets, pas de sous-pixel).
            var x = (int)MathF.Round(s.Position.X / s.Size) * s.Size;
            var y = (int)MathF.Round(s.Position.Y / s.Size) * s.Size;
            sb.Draw(pixel, new Rectangle(x, y, s.Size, s.Size), s.Color * alpha);
        }
        sb.End();
    }
}
