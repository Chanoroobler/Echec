using System;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Effet « waouh » de la révélation d'un coffre (façon casino), en VRAI PIXEL ART : tout est posé sur une grille
/// de gros pixels de la taille de ceux des sprites de la fenêtre (×3, ×2 en compact). À l'arrêt de la roulette :
/// éclair et gerbe sur la case gagnée. À la fenêtre « NOUVEAU ! » : rayons qui tournent IMAGE PAR IMAGE derrière
/// l'objet, éclair par paliers, pluie de pièces et d'étincelles qui scintillent, fenêtre qui tombe et rebondit.
/// Aucun fondu continu ni pixel tourné : des paliers de couleurs de la palette. Particules dans un tableau fixe
/// (zéro allocation par frame), images des rayons calculées une fois.
/// </summary>
public sealed partial class PvpArmyScene
{
    private const int WowMax = 180;
    private const float WowGravity = 420f;
    private const float RevealInputDelay = 0.45f;   // pas de « CONTINUER » involontaire pendant l'éclat
    private const float RevealDropSeconds = 0.4f;
    private const int RayCount = 12;
    private const int RayFrames = 8;                // images d'un tour (12 rayons : la figure se répète tous les 30°)
    private const float RayFps = 6f;

    private struct WowParticle
    {
        public float X, Y, Vx, Vy, Life, MaxLife;
        public Color Color;
        public bool Coin, Star;
    }

    private readonly WowParticle[] _wow = new WowParticle[WowMax];
    private int _wowCount;
    private float _revealT;
    private bool _revealBurstPending;
    private bool _settleBurstPending;
    private float _settleFlashT = -1f;

    private readonly Texture2D?[] _rayFrames = new Texture2D?[RayFrames];
    private (int W, int H, int Cell, Point Center) _rayKey;
    private Color[]? _rayBuffer;

    /// <summary>Taille d'un « pixel » de l'effet : celle des pixels des sprites de la fenêtre « NOUVEAU ! ».</summary>
    private int WowCell => _lay.Full ? 3 : 2;

    /// <summary>Couleur de l'objet gagné : rareté pour un équipement, or pour un pion ou un commandant.</summary>
    private Color RewardColor() =>
        !_rewardUnits && _reward != null && PvpCatalog.FindEquipment(_reward) is { } eq
            ? (eq.Rarity == EquipmentRarity.Common ? Palette.Yellow2 : PvpArt.RarityColor(eq.Rarity))
            : Palette.Yellow1;

    /// <summary>Gros lot (équipement légendaire, pion de tier 3, commandant) : gerbe plus fournie.</summary>
    private bool RewardIsBig() =>
        _reward != null && (_rewardUnits
            ? PvpCatalog.FindClass(_reward) is not { } u || u.Class.Tier >= 3
            : PvpCatalog.FindEquipment(_reward) is { Rarity: EquipmentRarity.Legendary });

    /// <summary>Gerbe de <paramref name="count"/> particules depuis (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private void WowBurst(float x, float y, int count, float speed, Color main, bool coins)
    {
        for (var i = 0; i < count && _wowCount < WowMax; i++)
        {
            var angle = (float)(_rng.NextDouble() * Math.PI * 2);
            var v = speed * (0.35f + (float)_rng.NextDouble() * 0.9f);
            ref var p = ref _wow[_wowCount++];
            p.X = x;
            p.Y = y;
            p.Vx = MathF.Cos(angle) * v;
            p.Vy = MathF.Sin(angle) * v - speed * 0.45f;   // vers le haut : la gerbe retombe en pluie
            p.MaxLife = p.Life = 0.8f + (float)_rng.NextDouble() * 0.9f;
            p.Coin = coins && _rng.Next(6) == 0;   // une pièce sur six : elles ne cachent pas le texte
            p.Star = !p.Coin && _rng.Next(3) == 0;
            p.Color = _rng.Next(3) switch { 0 => Palette.White, 1 => Palette.Yellow1, _ => main };
        }
    }

    private void UpdateWow(float dt)
    {
        if (_settleFlashT >= 0f)
            _settleFlashT += dt;
        if (_revealOpen)
            _revealT += dt;
        for (var i = 0; i < _wowCount; i++)
        {
            ref var p = ref _wow[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _wow[i] = _wow[--_wowCount];   // retrait sans trou : le dernier prend sa place
                i--;
                continue;
            }
            p.Vx *= 1f - 1.2f * dt;
            p.Vy += WowGravity * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
        }
    }

    /// <summary>La roulette vient de s'arrêter sur le gagné : éclair et gerbe sur la case.</summary>
    private void OnReelSettled()
    {
        _settleFlashT = 0f;
        _settleBurstPending = true;   // la gerbe part de la case au prochain dessin (position exacte)
        Context.Sounds.Play("chest_tick");
    }

    /// <summary>La fenêtre « NOUVEAU ! » s'ouvre : la gerbe partira du centre de l'objet au premier dessin.</summary>
    private void OnRevealOpened()
    {
        _revealT = 0f;
        _revealBurstPending = true;
        _settleFlashT = -1f;
    }

    private void ClearWow()
    {
        _wowCount = 0;
        _settleFlashT = -1f;
        _revealBurstPending = false;
        _settleBurstPending = false;
    }

    /// <summary>Gerbes en attente, lancées au dessin depuis la position réelle de l'objet.</summary>
    private void SpawnPendingBurst(ref bool pending, Point at, bool reveal)
    {
        if (!pending)
            return;
        pending = false;
        var big = RewardIsBig();
        if (reveal)
            WowBurst(at.X, at.Y, big ? 140 : 90, _lay.Full ? 520f : 400f, RewardColor(), coins: true);
        else
            WowBurst(at.X, at.Y, big ? 60 : 36, _lay.Full ? 360f : 280f, RewardColor(), coins: false);
#if DEBUG
        for (; _debugWowAdvance > 0f; _debugWowAdvance -= 1f / 60f)   // captures : effet déjà avancé
            UpdateWow(1f / 60f);
#endif
    }

    /// <summary>Décalage vertical de la fenêtre : elle tombe et rebondit une fois (multiple d'un gros pixel).</summary>
    private int RevealDropOffset()
    {
        if (_revealT >= RevealDropSeconds)
            return 0;
        var x = _revealT / RevealDropSeconds;
        var fall = _lay.Full ? 60f : 44f;
        // Chute (0 → 0,6), puis petit rebond (0,6 → 1).
        var off = x < 0.6f ? -fall * (1f - x / 0.6f) * (1f - x / 0.6f) : -fall * 0.18f * MathF.Sin((x - 0.6f) / 0.4f * MathF.PI);
        var cell = WowCell;
        return (int)MathF.Round(off / cell) * cell;
    }

    /// <summary>Éclair sur la case gagnée de la roulette, par paliers : blanc plein, puis cadre qui s'écarte.</summary>
    private void DrawSettleFlash(SpriteBatch sb, Rectangle box)
    {
        SpawnPendingBurst(ref _settleBurstPending, box.Center, reveal: false);
        if (_settleFlashT < 0f)
            return;
        var cell = WowCell;
        var step = (int)(_settleFlashT / 0.06f);   // une image toutes les 60 ms
        if (step >= 5)
            return;
        if (step < 2)
            PvpArt.Fill(sb, Px, box, step == 0 ? Palette.White : Palette.Yellow1);
        var grow = (step + 1) * 3 * cell;
        PvpArt.Border(sb, Px, new Rectangle(box.X - grow, box.Y - grow, box.Width + 2 * grow, box.Height + 2 * grow),
            step < 3 ? Palette.White : RewardColor(), cell);
    }

    /// <summary>Éclair plein écran à l'ouverture de la fenêtre : deux paliers, pas de fondu.</summary>
    private void DrawRevealFlash(SpriteBatch sb)
    {
        var step = (int)(_revealT / 0.06f);
        if (step >= 2)
            return;
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, _lay.W, _lay.H), Color.White * (step == 0 ? 0.75f : 0.35f));
    }

    /// <summary>Rayons autour de <paramref name="center"/> : une image précalculée en gros pixels, changée
    /// <see cref="RayFps"/> fois par seconde (rotation image par image, sans pixel tourné).</summary>
    private void DrawRays(SpriteBatch sb, Point center)
    {
        var cell = WowCell;
        var key = (_lay.W, _lay.H, cell, center);
        if (key != _rayKey)
        {
            _rayKey = key;
            DisposeRays();
        }
        var frame = (int)(_revealT * RayFps) % RayFrames;
        var tex = _rayFrames[frame] ??= CreateRayFrame(frame, cell, center);
        var appear = _revealT < 1f / RayFps ? 0.5f : 1f;   // première image à demi, puis pleine
        sb.Draw(tex, new Rectangle(0, 0, tex.Width * cell, tex.Height * cell), RewardColor() * appear);
    }

    private void DrawWowParticles(SpriteBatch sb)
    {
        var cell = WowCell;
        var blink = (int)(_time * 10f) % 2 == 0;
        for (var i = 0; i < _wowCount; i++)
        {
            ref var p = ref _wow[i];
            var x = (int)MathF.Floor(p.X / cell) * cell;   // calées sur la grille des gros pixels
            var y = (int)MathF.Floor(p.Y / cell) * cell;
            var dying = p.Life < p.MaxLife * 0.3f;
            if (p.Coin)
            {
                if (!dying || blink)   // en fin de vie, la pièce clignote puis disparaît
                    PvpArt.Coin(sb, Px, new Point(x - PvpArt.CoinSize * cell / 2, y - PvpArt.CoinSize * cell / 2), cell);
                continue;
            }
            var color = dying ? Palette.Brown3 : p.Color;
            PvpArt.Fill(sb, Px, new Rectangle(x, y, cell, cell), color);
            if (p.Star && !dying && ((i & 1) == 0) == blink)   // étoile qui scintille : croix d'un gros pixel
            {
                PvpArt.Fill(sb, Px, new Rectangle(x - cell, y, cell, cell), color);
                PvpArt.Fill(sb, Px, new Rectangle(x + cell, y, cell, cell), color);
                PvpArt.Fill(sb, Px, new Rectangle(x, y - cell, cell, cell), color);
                PvpArt.Fill(sb, Px, new Rectangle(x, y + cell, cell, cell), color);
            }
        }
    }

    /// <summary>Une image des rayons, un texel par gros pixel : 12 secteurs alternés (deux opacités), tournés de
    /// <paramref name="frame"/> / <see cref="RayFrames"/> de l'écart entre deux rayons.</summary>
    private Texture2D CreateRayFrame(int frame, int cell, Point center)
    {
        var w = (_lay.W + cell - 1) / cell;
        var h = (_lay.H + cell - 1) / cell;
        if (_rayBuffer == null || _rayBuffer.Length != w * h)
            _rayBuffer = new Color[w * h];
        var cx = center.X / (float)cell;
        var cy = center.Y / (float)cell;
        var sector = MathHelper.TwoPi / RayCount;
        var offset = sector * frame / RayFrames;
        var strong = Color.White * 0.22f;
        var soft = Color.White * 0.12f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var raw = (MathF.Atan2(y + 0.5f - cy, x + 0.5f - cx) - offset) / sector;
                var index = (int)MathF.Floor(raw);
                var inRay = raw - index < 0.32f;                     // le rayon occupe un tiers du secteur
                _rayBuffer[y * w + x] = !inRay ? Color.Transparent : (index & 1) == 0 ? strong : soft;
            }
        var tex = new Texture2D(Context.GraphicsDevice, w, h);
        tex.SetData(_rayBuffer);
        return tex;
    }

    private void DisposeRays()
    {
        for (var i = 0; i < _rayFrames.Length; i++)
        {
            _rayFrames[i]?.Dispose();
            _rayFrames[i] = null;
        }
    }

#if DEBUG
    /// <summary>Captures : effet avancé de <paramref name="seconds"/> (arrêt de la roulette, ou fenêtre « NOUVEAU ! »).</summary>
    internal void DebugWow(bool reveal, float seconds)
    {
        if (reveal)
            OnRevealOpened();
        else
            OnReelSettled();
        _debugWowAdvance = seconds;   // appliqué quand la gerbe part, au premier dessin
    }

    private float _debugWowAdvance;
#endif
}
