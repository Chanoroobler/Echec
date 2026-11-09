#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>Données SIMULÉES d'un écran de fin multi (captures Debug).</summary>
internal sealed record PvpEndShot(string Name, Point Canvas, Language Lang, PvpOutcome Outcome, MatchEndReason Reason,
    string Opponent, bool Bot, bool Friendly, string Mine, string Theirs, int Before, int After, int Kills, int Lost,
    int Rounds, int Seconds, int Total, bool Valid = true, bool Desync = false, float Time = 5f, bool Chest = true,
    int Streak = 0);

public sealed partial class GameplayScene
{
    /// <summary>Capture (Debug) : termine la partie et ouvre l'écran de fin avec des valeurs imposées.</summary>
    internal void DebugShowPvpEnd(PvpEndShot s)
    {
        if (s.Outcome == PvpOutcome.Draw)
            _match.EndInDraw(s.Reason);
        else
            _match.Concede(s.Outcome == PvpOutcome.Win ? Faction.Enemy : Faction.Player, s.Reason);
        var config = PvpConfig.Current;
        var points = s.Kills * config.PointsPerKill
            + (s.Outcome == PvpOutcome.Win ? config.PointsForWin : s.Outcome == PvpOutcome.Draw ? config.PointsForDraw : 0);
        _pvpRecorded = true;
        _pvpValid = s.Valid;
        _pvpDesync = s.Desync;
        _pvpReward = s.Valid
            ? new PvpReward(points, s.Before, s.After, !s.Friendly, s.Streak, s.Streak > 0 ? config.WinStreakBonus : 0)
            : null;
        BeginPvpEndScreen(s.Outcome);
        var e = _pvpEnd!;
        e.Opponent = s.Opponent;
        e.Bot = s.Bot;
        e.Friendly = s.Friendly;
        e.MyCommander = PvpCatalog.CommanderClass(s.Mine)?.Class;
        e.TheirCommander = PvpCatalog.CommanderClass(s.Theirs)?.Class;
        e.MyCommanderName = PvpUi.CommanderName(s.Mine);
        e.TheirCommanderName = PvpUi.CommanderName(s.Theirs);
        e.KillsLine = Loc.T("pvp.end.kills", s.Kills, config.PointsPerKill);
        e.KillsValue = "+" + s.Kills * config.PointsPerKill;
        e.PointsAfter = s.Total;
        e.PointsBefore = s.Valid ? s.Total - points : s.Total;
        e.ChestReady = s.Valid && s.Chest;
        e.StatValue[0] = s.Rounds.ToString();
        e.StatValue[1] = PvpUi.Clock(s.Seconds);
        e.StatValue[2] = s.Kills.ToString();
        e.StatValue[3] = s.Lost.ToString();
        e.ShownRating = e.ShownPoints = int.MinValue;
        e.RankName = "";
        _pvpEndTime = s.Time;
        RefreshPvpEndValues(e, s.Time);
    }
}

/// <summary>
/// Captures de l'écran de FIN multi : <c>ChessArmy.Game -pvpendshots &lt;dossier&gt; [filtre]</c> (Debug). Chaque
/// étape lance la vraie scène de combat contre un bot (profil EN MÉMOIRE), la termine avec des données simulées
/// (<see cref="PvpEndShot"/>) et enregistre le canevas à l'échelle 1, comme les maquettes. Le jeu se ferme ensuite.
/// </summary>
internal sealed class PvpEndShots
{
    private const int SetupFrames = 60, ShotFrame = 64;
    private readonly GameContext _ctx;
    private readonly Action _exit;
    private readonly string _dir;
    private readonly List<PvpEndShot> _steps = new();
    private GameplayScene? _scene;
    private RenderTarget2D? _canvas;
    private int _index, _frame;

    private PvpEndShots(GameContext ctx, Action exit, string dir)
    {
        _ctx = ctx;
        _exit = exit;
        _dir = dir;
        Directory.CreateDirectory(dir);
        var big = new Point(1280, 720);
        var small = new Point(960, 540);
        foreach (var (canvas, tag) in new[] { (big, "1280"), (small, "960") })
        {
            _steps.Add(new($"{tag}-11-victoire", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.LeaderDown,
                "Minos", false, false, "commandant", "Commandant_lancier", 950, 975, 6, 3, 18, 462, 127));
            _steps.Add(new($"{tag}-12-defaite", canvas, Language.Francais, PvpOutcome.Loss, MatchEndReason.LeaderDown,
                "Vauclair", false, false, "commandant", "Commandant_cavalier", 966, 940, 3, 9, 14, 310, 106));
            _steps.Add(new($"{tag}-13-egalite-bot", canvas, Language.Francais, PvpOutcome.Draw, MatchEndReason.DeadPosition,
                "BOT FER", true, false, "commandant", "Commandant_fou", 949, 949, 4, 5, 31, 725, 113));
            _steps.Add(new($"{tag}-14-bot-victoire", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.Timeout,
                "Corwin", true, false, "commandant", "Commandant_brute", 1180, 1202, 5, 2, 22, 540, 88, Chest: false));
            _steps.Add(new($"{tag}-15-defi-ami", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.Resign,
                "Rougegorge_Le_Magnifique_Du_Nord", false, true, "commandant", "Commandant_marchand", 966, 966, 7, 4, 20, 498, 131));
            _steps.Add(new($"{tag}-16-promotion", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.Disconnect,
                "Tonnelier", false, false, "Commandant_duo", "commandant", 990, 1015, 2, 1, 9, 201, 74));
            _steps.Add(new($"{tag}-17-desynchro", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.LeaderDown,
                "Minos", false, false, "commandant", "Commandant_lancier", 950, 950, 6, 3, 18, 462, 67, Valid: false, Desync: true));
            _steps.Add(new($"{tag}-18-animation", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.LeaderDown,
                "Minos", false, false, "commandant", "Commandant_lancier", 950, 975, 6, 3, 18, 462, 127, Time: 0.9f));
            _steps.Add(new($"{tag}-19-egalite-acceptee", canvas, Language.Francais, PvpOutcome.Draw, MatchEndReason.AgreedDraw,
                "Minos", false, false, "commandant", "Commandant_lancier", 1010, 1001, 1, 1, 12, 300, 52));
            _steps.Add(new($"{tag}-20-serie", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.LeaderDown,
                "Minos", false, false, "commandant", "Commandant_lancier", 950, 990, 6, 3, 18, 462, 127, Streak: 4));
        }
        // Langues verbeuses et chinois, au format le plus serré et au grand.
        foreach (var lang in new[] { Language.Deutsch, Language.Polski, Language.Turkce, Language.English, Language.Italiano,
                     Language.Espanol, Language.ChineseSimplified })
            foreach (var (canvas, tag) in new[] { (small, "960"), (big, "1280") })
            {
                _steps.Add(new($"{tag}-lang-{lang}-defaite", canvas, lang, PvpOutcome.Loss, MatchEndReason.Timeout,
                    "Rougegorge_Le_Magnifique_Du_Nord", false, false, "Commandant_cavalier", "Commandant_marchand",
                    1006, 989, 3, 9, 14, 310, 106));
                _steps.Add(new($"{tag}-lang-{lang}-ami", canvas, lang, PvpOutcome.Draw, MatchEndReason.MutualDestruction,
                    "BOT ARGENT", true, true, "commandant", "Commandant_fou", 949, 949, 4, 5, 31, 725, 113));
            }
        // Animation de changement de rang, image par image (filtre « rang-anim ») : temps fort à 1,3 s.
        foreach (var (canvas, tag) in new[] { (big, "1280"), (small, "960") })
            foreach (var t in new[] { 0.8f, 1.25f, 1.33f, 1.4f, 1.47f, 1.55f, 1.75f, 2.4f, 5f })
            {
                var ms = ((int)(t * 1000)).ToString("D4");
                _steps.Add(new($"{tag}-rang-anim-promo-{ms}", canvas, Language.Francais, PvpOutcome.Win, MatchEndReason.LeaderDown,
                    "Noémie", true, false, "commandant", "commandant", 998, 1014, 8, 3, 26, 369, 150, Time: t));
                _steps.Add(new($"{tag}-rang-anim-retro-{ms}", canvas, Language.Francais, PvpOutcome.Loss, MatchEndReason.LeaderDown,
                    "Vauclair", false, false, "commandant", "Commandant_cavalier", 1006, 989, 3, 9, 14, 310, 106, Time: t));
            }
    }

    public static PvpEndShots? FromArgs(GameContext ctx, Action exit)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-pvpendshots", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
            return null;
        var shots = new PvpEndShots(ctx, exit, args[i + 1]);
        if (i + 2 < args.Length && !args[i + 2].StartsWith('-'))
            shots._steps.RemoveAll(s => !s.Name.Contains(args[i + 2], StringComparison.OrdinalIgnoreCase));
        return shots;
    }

    public void Tick(GameTime gameTime)
    {
        if (_index >= _steps.Count)
        {
            _scene?.Unload();
            _exit();
            return;
        }
        var step = _steps[_index];
        var gd = _ctx.GraphicsDevice;
        if (_frame == 0)
        {
            Loc.Current = step.Lang;
            _ctx.VirtualResolution = step.Canvas;
            var profile = PvpProfile.CreateNew();
            profile.MarkTutorialSeen();
            PvpProfileStore.UseInMemory(profile);
            _scene?.Unload();
            var rng = new Random(7);
            var info = PvpMatchInfo.ForBot(PvpProfile.DefaultBuild(), step.Before, rng);
            _scene = new GameplayScene(_ctx, info);
            _scene.Load();
            _canvas?.Dispose();
            _canvas = new RenderTarget2D(gd, step.Canvas.X, step.Canvas.Y, false, SurfaceFormat.Color, DepthFormat.None, 0,
                RenderTargetUsage.PreserveContents);
        }
        if (_frame < SetupFrames)
            _scene!.Update(new GameTime(gameTime.TotalGameTime, TimeSpan.FromSeconds(1.0 / 60)));
        if (_frame == SetupFrames)
            _scene!.DebugShowPvpEnd(step);
        _scene!.SetVirtualScaleHint(1);
        NativeSprites.Scale = 1;
        NativeSprites.Clear();
        gd.SetRenderTarget(_canvas);
        gd.Clear(Color.Black);
        _scene.Draw(gameTime);
        gd.SetRenderTarget(null);
        if (++_frame < ShotFrame)
            return;
        using (var file = File.Create(Path.Combine(_dir, step.Name + ".png")))
            _canvas!.SaveAsPng(file, _canvas.Width, _canvas.Height);
        SaveUltrawide(step);
        _frame = 0;
        _index++;
    }

    /// <summary>
    /// Même écran composé comme sur un écran 21:9 (3440×1440 → canvas 16:9 au centre, bandes de chaque côté) :
    /// bandes du letterbox de la scène puis canvas par-dessus, à l'échelle 1. Vérifie le raccord canvas / bandes.
    /// </summary>
    private void SaveUltrawide(PvpEndShot step)
    {
        var gd = _ctx.GraphicsDevice;
        var w = step.Canvas.Y * 43 / 18;   // 3440:1440
        var offset = new Point((w - step.Canvas.X) / 2, 0);
        using var wide = new RenderTarget2D(gd, w, step.Canvas.Y);
        gd.SetRenderTarget(wide);
        gd.Clear(Color.Black);
        _scene!.DrawLetterboxBackground(new Point(w, step.Canvas.Y), offset, 1);
        var sb = _ctx.SpriteBatch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_canvas!, new Vector2(offset.X, offset.Y), Color.White);
        sb.End();
        gd.SetRenderTarget(null);
        using var file = File.Create(Path.Combine(_dir, step.Name + "-21x9.png"));
        wide.SaveAsPng(file, w, step.Canvas.Y);
    }
}
#endif
