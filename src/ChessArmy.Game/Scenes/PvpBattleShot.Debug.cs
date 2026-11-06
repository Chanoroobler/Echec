#if DEBUG
using System;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Game.Net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

public sealed partial class GameplayScene
{
    /// <summary>Capture (Debug) : sélectionne le premier pion joueur ÉQUIPÉ, pour montrer sa carte de combat.</summary>
    internal void DebugSelectEquippedPlayer()
    {
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Player && unit.Equipments.Count > 0)
            {
                CombatActAt(cell);
                return;
            }
    }
}

/// <summary>
/// Capture d'un COMBAT MULTI contre un bot Argent (équipements tirés des deux côtés) :
/// <c>ChessArmy.Game -pvpbattleshot &lt;fichier.png&gt;</c> (Debug). Lance la vraie scène de combat, sélectionne un
/// pion équipé (carte), enregistre l'écran (backbuffer) puis ferme le jeu. Aucune partie n'est jouée ni enregistrée.
/// </summary>
internal sealed class PvpBattleShot
{
    private const int SelectFrame = 30, ShotFrame = 50;
    private readonly string _path;
    private readonly GameplayScene _scene;
    private int _frame;

    private PvpBattleShot(GameContext ctx, string path)
    {
        _path = path;
        var rng = new Random(7);
        var local = PvpCatalog.RandomEquipment(PvpProfile.DefaultBuild(), rng);
        var info = PvpMatchInfo.ForBot(local, 1500, rng).WithLocalBuild(local);   // 1500 = Argent
        Console.WriteLine($"pvpbattleshot : bot {PvpRanks.BotKey(1500)}, équipements joueur "
            + $"{local.Slots.Count(s => s.EquipmentId != null)}, adversaire {info.OpponentBuild.Slots.Count(s => s.EquipmentId != null)}");
        _scene = new GameplayScene(ctx, info);
        ctx.Scenes.Change(_scene);
    }

    public static PvpBattleShot? FromArgs(GameContext ctx)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-pvpbattleshot", StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= args.Length ? null : new PvpBattleShot(ctx, args[i + 1]);
    }

    /// <summary>À appeler à la fin du dessin d'une frame (backbuffer complet, avant présentation).</summary>
    public void AfterDraw(GraphicsDevice device, Action exit)
    {
        _frame++;
        if (_frame == SelectFrame)
            _scene.DebugSelectEquippedPlayer();
        if (_frame != ShotFrame)
            return;
        var pp = device.PresentationParameters;
        var data = new Color[pp.BackBufferWidth * pp.BackBufferHeight];
        device.GetBackBufferData(data);
        using var tex = new Texture2D(device, pp.BackBufferWidth, pp.BackBufferHeight);
        tex.SetData(data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using (var fs = File.Create(_path))
            tex.SaveAsPng(fs, tex.Width, tex.Height);
        exit();
    }
}
#endif
