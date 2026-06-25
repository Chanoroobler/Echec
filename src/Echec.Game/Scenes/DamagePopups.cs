using System;
using System.Collections.Generic;
using Echec.Core.Map;
using Echec.Engine.Rendering;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Scenes;

/// <summary>Un chiffre de dégâts flottant au-dessus d'une case. Mutable, recyclé par la liste.</summary>
internal sealed class DamagePopup
{
    public int Column;
    public int Row;
    public string Text = "";
    public float Life;
    public float MaxLife;
}

/// <summary>
/// Chiffres de dégâts qui jaillissent au-dessus de la victime au moment de l'impact (en même temps
/// que les étincelles et le FX de dégât), montent en s'estompant et disparaissent. Ancrés à la CASE
/// (pas à un point écran fixe) → suivent le plateau si la caméra bouge. Rendu pixel-perfect : police
/// bitmap à échelle entière, position arrondie, ombre portée pour rester lisible sur tout terrain.
/// </summary>
internal sealed class DamagePopups
{
    private const float LifeDur = 0.5f;        // durée de vie d'un chiffre avant explosion (s)
    private const float RiseFraction = 0.6f;   // montée totale, en fraction de case

    private readonly List<DamagePopup> _active = new();

    public bool HasActive => _active.Count > 0;

    /// <summary>Vide les chiffres en cours (au démarrage d'un nouveau combat, pour ne pas reporter une explosion).</summary>
    public void Clear() => _active.Clear();

    /// <summary>Fait jaillir « amount » au-dessus de la case <paramref name="cell"/>.</summary>
    public void Spawn(Cell cell, int amount)
    {
        if (amount <= 0)
            return;
        _active.Add(new DamagePopup
        {
            Column = cell.Column,
            Row = cell.Row,
            Text = amount.ToString(),
            MaxLife = LifeDur,
            Life = LifeDur,
        });
    }

    /// <summary>
    /// Avance les chiffres et, quand l'un s'éteint, le fait ÉCLATER en feu d'artifice là où il
    /// flottait (gerbe radiale via <paramref name="sparks"/>). <paramref name="layout"/> sert à
    /// convertir la case en position écran (suit le zoom/la caméra).
    /// </summary>
    public void Update(float dt, GridLayout layout, SparkBurst sparks)
    {
        var pixel = MathF.Max(2f, layout.TileSize / 32f);
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.Life -= dt;
            if (p.Life > 0f)
                continue;

            sparks.EmitFirework(DeathOrigin(layout, p), count: 22, pixel);
            _active.RemoveAt(i);
        }
    }

    /// <summary>Point écran où le chiffre se trouve à l'instant de son extinction (t = 1).</summary>
    private static Vector2 DeathOrigin(GridLayout layout, DamagePopup p)
    {
        var size = layout.TileSize;
        var top = layout.CellToScreen(p.Column, p.Row);
        var rise = size * RiseFraction;                       // montée totale (t = 1)
        return new Vector2(top.X + size / 2f, top.Y + size * 0.18f - rise);
    }

    public void Draw(SpriteBatch sb, PixelFont font, GridLayout layout)
    {
        if (_active.Count == 0)
            return;

        var size = layout.TileSize;
        var baseScale = Math.Max(2, size / 22);

        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var p in _active)
        {
            var t = 1f - p.Life / p.MaxLife;                       // 0 → 1

            // Montée en décélération (ease-out) : vif au départ, ralentit en fin de vie.
            var rise = (1f - (1f - t) * (1f - t)) * size * RiseFraction;

            // Petit « pop » : un cran d'échelle en plus sur les premières frames, puis taille normale.
            var scale = t < 0.14f ? baseScale + 1 : baseScale;

            // Solide quasiment jusqu'au bout : c'est l'explosion (feu d'artifice) qui sert de sortie,
            // pas un fondu. Juste un léger estompage sur les toutes dernières frames.
            var alpha = t < 0.85f ? 1f : Math.Max(0f, 1f - (t - 0.85f) / 0.15f);

            var top = layout.CellToScreen(p.Column, p.Row);
            var w = font.Measure(p.Text, scale);
            var x = top.X + size / 2f - w / 2f;
            var y = top.Y + size * 0.18f - rise;                  // part du haut de la case, monte
            var pos = new Vector2((int)MathF.Round(x), (int)MathF.Round(y));

            font.Draw(sb, p.Text, pos + new Vector2(scale, scale), scale, Palette.Black1 * alpha);  // ombre
            font.Draw(sb, p.Text, pos, scale, Palette.Yellow2 * alpha);                             // chiffre jaune vif
        }
        sb.End();
    }
}
