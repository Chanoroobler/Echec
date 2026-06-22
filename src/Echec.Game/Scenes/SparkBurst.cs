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
/// Gerbe d'étincelles d'impact : particules pixel-art recyclées via <see cref="Pool{T}"/> (pas
/// d'allocation par coup). Émises au contact, soumises à la gravité, s'éteignent par fondu postérisé.
/// Rendu en carrés alignés à la grille (pixel-perfect, chunky).
/// </summary>
internal sealed class SparkBurst
{
    private const float Gravity = 760f;        // px/s² (canvas)

    private readonly Pool<Spark> _pool = new(() => new Spark(), prewarm: 64);
    private readonly List<Spark> _active = new();
    private readonly Random _rng = new();

    public bool HasActive => _active.Count > 0;

    /// <summary>
    /// Émet <paramref name="count"/> étincelles depuis <paramref name="origin"/>, en cône autour de
    /// <paramref name="dir"/> (direction du coup) + léger biais vers le haut. Couleur principale
    /// <paramref name="color"/>, avec ~1/3 d'étincelles en <paramref name="hot"/> (cœur chaud, pétille).
    /// <paramref name="pixel"/> = côté de bloc en px canvas (taille de pixel d'art).
    /// </summary>
    public void Emit(Vector2 origin, Vector2 dir, int count, Color color, Color hot, float pixel)
    {
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var baseAngle = MathF.Atan2(dir.Y, dir.X);
        var size = Math.Max(2, (int)pixel);

        for (var i = 0; i < count; i++)
        {
            var s = _pool.Get();
            var ang = baseAngle + (float)(_rng.NextDouble() - 0.5) * 2.4f;   // cône large
            var speed = 90f + (float)_rng.NextDouble() * 240f;
            s.Position = origin;
            s.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed - new Vector2(0, 70f); // gerbe vers le haut
            s.MaxLife = 0.20f + (float)_rng.NextDouble() * 0.18f;
            s.Life = s.MaxLife;
            s.Size = _rng.Next(3) == 0 ? size * 2 : size;   // quelques grosses, beaucoup de petites
            s.Color = _rng.Next(3) == 0 ? hot : color;      // cœurs chauds éparpillés
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
