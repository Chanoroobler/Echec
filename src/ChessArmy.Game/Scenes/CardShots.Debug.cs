#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

public sealed partial class GameplayScene
{
    /// <summary>Capture (Debug) : carte détaillée de combat ou compacte de recrutement, SANS charger la scène
    /// (aucune run, aucune sauvegarde touchée). Pion du plateau dans la couche écran, comme sur les cartes de choix.</summary>
    internal void DebugDrawCard(SpriteBatch sb, Rectangle rect, UnitClass c, Faction faction, Domaine domaine,
        int hp, int maxHp, IReadOnlyList<Equipment>? equip, int previewDamage, bool compact)
    {
        _choiceCardNative = true;
        DrawCardLayout(sb, rect, c, faction, domaine, hp, maxHp, equip: equip, hpPreviewDamage: previewDamage,
            compact: compact);
        _choiceCardNative = null;
    }

    /// <summary>Capture (Debug) : pile de popups de la carte (cadres d'objets + traits), sous ou à côté de la carte.</summary>
    internal void DebugDrawCardStack(SpriteBatch sb, Rectangle card, UnitClass c, IReadOnlyList<Equipment>? equip) =>
        DrawKeywordPopupsBelow(sb, c, card, equip);

    /// <summary>Capture (Debug) : tooltip d'équipement centrée au-dessus d'un élément.</summary>
    internal void DebugDrawEquipTooltip(SpriteBatch sb, Equipment equip, Rectangle anchor) =>
        DrawEquipTooltipAbove(sb, equip, anchor);

    internal static int DebugCardH(bool compact, IReadOnlyList<Equipment>? equip) =>
        compact ? DraftCardH : DetailedCardH(equip, null);
}

/// <summary>
/// Captures des cartes d'information d'unité : <c>ChessArmy.Game -cardshots &lt;dossier&gt;</c> (Debug). Rend les
/// cartes par le VRAI code de dessin (GameplayScene, CodexView, UnitCardRenderer, fiche du commandant) dans un
/// canevas 960×540 et 1280×720, composé ×2 comme à l'écran, puis ferme le jeu. Ni run ni sauvegarde écrite.
/// </summary>
internal sealed class CardShots
{
    private sealed record Step(string Name, Point Canvas, Language Lang, Action<SpriteBatch, Point> Draw);

    private const int Scale = 2;
    private readonly GameContext _ctx;
    private readonly Action _exit;
    private readonly string _dir;
    private readonly List<Step> _steps = new();
    private readonly GameplayScene _game;
    private readonly CodexView _codex;
    private readonly UnitCardRenderer _renderer;
    private CommanderSelectScene? _select;
    private RenderTarget2D? _canvas, _out;
    private int _index, _frame;

    private CardShots(GameContext ctx, Action exit, string dir)
    {
        _ctx = ctx;
        _exit = exit;
        _dir = dir;
        Directory.CreateDirectory(dir);
        _game = new GameplayScene(ctx, saveSlot: -1);
        _codex = new CodexView(ctx);
        _renderer = new UnitCardRenderer(ctx);

        // Classes à afficher : celles qui portent le PLUS de mots-clés (cas le plus serré en bas de carte).
        var all = new List<(UnitClass C, Domaine D)>();
        void Walk(UnitClass c, Domaine d) { all.Add((c, d)); foreach (var e in c.Evolutions) Walk(e, d); }
        foreach (var d in Domaines.All)
            Walk(d.BaseClass, d.Id);
        var busy = all.OrderByDescending(x => x.C.Traits.Count + (x.C.MinAttackRange > 1 ? 1 : 0)).Take(3).ToList();
        var basic = all[0];
        var hpItem = Equipments.All.FirstOrDefault(e => e.BonusFor(EquipStat.Hp) > 0);
        var hpEquip = hpItem != null ? new List<Equipment> { hpItem } : null;
        var hpBonus = hpItem?.BonusFor(EquipStat.Hp) ?? 0;

        foreach (var (canvas, tag) in new[] { (new Point(960, 540), "960"), (new Point(1280, 720), "1280") })
        {
            _steps.Add(new Step($"{tag}-combat-intact-blesse", canvas, Language.Francais, (sb, vp) =>
            {
                var b = busy[0];
                var h = GameplayScene.DebugCardH(false, null);
                var row = Row(vp, 3, 248, h);
                _game.DebugDrawCard(sb, row[0], basic.C, Faction.Player, basic.D, basic.C.MaxHp, basic.C.MaxHp, null, 0, false);
                _game.DebugDrawCard(sb, row[1], b.C, Faction.Player, b.D, Math.Max(1, b.C.MaxHp - 4), b.C.MaxHp, null, 0, false);
                _game.DebugDrawCard(sb, row[2], b.C, Faction.Enemy, b.D, Math.Max(1, b.C.MaxHp - 2), b.C.MaxHp, null, 3, false);
            }));
            _steps.Add(new Step($"{tag}-combat-bonus-pv", canvas, Language.Francais, (sb, vp) =>
            {
                var b = busy[1];
                var h = GameplayScene.DebugCardH(false, hpEquip);
                var row = Row(vp, 2, 248, h);
                var max = b.C.MaxHp + hpBonus;
                _game.DebugDrawCard(sb, row[0], b.C, Faction.Player, b.D, max, max, hpEquip, 0, false);
                _game.DebugDrawCard(sb, row[1], b.C, Faction.Player, b.D, max - 5, max, hpEquip, 0, false);
            }));
            _steps.Add(new Step($"{tag}-recrutement", canvas, Language.Francais, (sb, vp) =>
            {
                var h = GameplayScene.DebugCardH(true, null);
                var row = Row(vp, 3, 220, h);
                for (var i = 0; i < 3; i++)
                    _game.DebugDrawCard(sb, row[i], busy[i].C, Faction.Player, busy[i].D, busy[i].C.MaxHp, busy[i].C.MaxHp, null, 0, true);
            }));
            _steps.Add(new Step($"{tag}-codex", canvas, Language.Francais, (sb, vp) =>
            {
                var row = Row(vp, 3, UnitCardRenderer.CardW, UnitCardRenderer.CardH);
                for (var i = 0; i < 3; i++)
                    _codex.DebugDrawCard(sb, busy[i].C, busy[i].D, row[i]);
            }));
            _steps.Add(new Step($"{tag}-commandant", canvas, Language.Francais, (sb, vp) =>
            {
                _select!.Draw(new GameTime());   // ouvre et ferme son propre lot
                var b = busy[0];
                var bounds = new Rectangle(0, 0, vp.X, vp.Y);
                sb.Begin(samplerState: SamplerState.PointClamp);
                _renderer.DrawCardNear(sb, b.C, b.D, new Rectangle(vp.X / 2 - 40, vp.Y / 2 - 32, 64, 64), bounds);
                sb.End();
            }));
        }
        // Totem de rempart : rappel de « Rempart » (cité par Aura de rempart) dans la pile de la carte de combat,
        // la tooltip d'objet du jeu et celle du Codex.
        var totem = Equipments.All.FirstOrDefault(e => e.Id == "auraRempartLegendaire");
        if (totem != null)
        {
            var totemEquip = new List<Equipment> { totem };
            foreach (var (canvas, tag, lang) in new[] { (new Point(960, 540), "960", Language.Francais), (new Point(1280, 720), "1280", Language.Francais),
                         (new Point(960, 540), "960", Language.Deutsch) })
            {
                _steps.Add(new Step($"{tag}-totem-{lang}-combat", canvas, lang, (sb, vp) =>
                {
                    var h = GameplayScene.DebugCardH(false, totemEquip);
                    var card = new Rectangle(vp.X - 248 - 12, Math.Max(4, (vp.Y - h) / 2), 248, h);
                    var max = basic.C.MaxHp + totem.BonusFor(EquipStat.Hp);
                    _game.DebugDrawCard(sb, card, basic.C, Faction.Player, basic.D, max, max, totemEquip, 0, false);
                    _game.DebugDrawCardStack(sb, card, basic.C, totemEquip);
                }));
                _steps.Add(new Step($"{tag}-totem-{lang}-infobulles", canvas, lang, (sb, vp) =>
                {
                    _game.DebugDrawEquipTooltip(sb, totem, new Rectangle(vp.X / 4, vp.Y - 60, 32, 32));
                    _codex.DebugDrawEquipTooltip(sb, totem, new Rectangle(vp.X / 2 + 20, vp.Y / 3, 32, 32), new Rectangle(0, 0, vp.X, vp.Y));
                }));
            }
        }
        _steps.Add(new Step("960-zh-combat", new Point(960, 540), Language.ChineseSimplified, (sb, vp) =>
        {
            var b = busy[0];
            var row = Row(vp, 3, 248, GameplayScene.DebugCardH(false, null));
            _game.DebugDrawCard(sb, row[0], b.C, Faction.Player, b.D, Math.Max(1, b.C.MaxHp - 4), b.C.MaxHp, null, 0, false);
            _codex.DebugDrawCard(sb, b.C, b.D, row[1] with { Height = UnitCardRenderer.CardH });
            _renderer.DrawCard(sb, b.C, b.D, row[2] with { Height = UnitCardRenderer.CardH });
        }));
    }

    /// <summary><paramref name="n"/> cartes de front, centrées dans le canevas.</summary>
    private static Rectangle[] Row(Point vp, int n, int w, int h)
    {
        const int gap = 12;
        var x0 = (vp.X - (n * w + (n - 1) * gap)) / 2;
        var y = Math.Max(4, (vp.Y - h) / 2);
        return Enumerable.Range(0, n).Select(i => new Rectangle(x0 + i * (w + gap), y, w, h)).ToArray();
    }

    public static CardShots? FromArgs(GameContext ctx, Action exit)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-cardshots", StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= args.Length ? null : new CardShots(ctx, exit, args[i + 1]);
    }

    public void Tick(GameTime gameTime)
    {
        if (_index >= _steps.Count)
        {
            _exit();
            return;
        }
        var step = _steps[_index];
        var gd = _ctx.GraphicsDevice;
        if (_frame == 0)
        {
            Loc.Current = step.Lang;
            _ctx.VirtualResolution = step.Canvas;
            _select?.Unload();
            _select = null;
            if (step.Name.Contains("commandant"))
            {
                _select = new CommanderSelectScene(_ctx, 0);
                _select.Load();
            }
            _canvas?.Dispose();
            _out?.Dispose();
            _canvas = new RenderTarget2D(gd, step.Canvas.X, step.Canvas.Y);
            _out = new RenderTarget2D(gd, step.Canvas.X * Scale, step.Canvas.Y * Scale);
        }
        NativeSprites.Scale = Scale;
        NativeSprites.Clear();
        gd.SetRenderTarget(_canvas);
        gd.Clear(Palette.Navy1);
        var sb = _ctx.SpriteBatch;
        if (_select != null)
            step.Draw(sb, step.Canvas);   // gère ses lots elle-même (scène puis carte)
        else
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            step.Draw(sb, step.Canvas);
            sb.End();
        }
        gd.SetRenderTarget(_out);
        gd.Clear(Color.Black);
        var dest = new Rectangle(0, 0, _out!.Width, _out.Height);
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_canvas, dest, Color.White);
        sb.End();
        NativeSprites.Flush(sb, dest, Scale);
        gd.SetRenderTarget(null);

        if (++_frame < 4)
            return;
        using (var file = File.Create(Path.Combine(_dir, step.Name + ".png")))
            _out.SaveAsPng(file, _out.Width, _out.Height);
        _frame = 0;
        _index++;
    }
}
#endif
