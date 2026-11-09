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

    /// <summary>Capture (Debug) : sélectionne le pion joueur le plus à DROITE (sa fiche déborde sur la colonne des boutons).</summary>
    internal void DebugSelectRightmostPlayer()
    {
        ChessArmy.Core.Map.Cell? best = null;
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Player && !unit.IsEssential && (best == null || cell.Column > best.Value.Column))
                best = cell;
        if (best is { } c)
            CombatActAt(c);
    }

    /// <summary>Capture (Debug, partie tuto) : bulles « but du jeu » et « à toi de jouer » considérées comme lues.</summary>
    internal void DebugSkipFirstTutoBubbles()
    {
        _tutoSeen |= (1 << (int)TutoStep.Goal) | (1 << (int)TutoStep.Turn);
        _tutoOpen = null;
    }

    /// <summary>Capture (Debug) : retire tous les pions ennemis (et ceux du joueur si <paramref name="both"/>), puis
    /// fait passer le tour du camp qui a la main : commandant isolé (bandeau, gemme) ou égalité à commandants.</summary>
    internal void DebugStripPawns(bool both)
    {
        var cells = new System.Collections.Generic.List<ChessArmy.Core.Map.Cell>();
        foreach (var (cell, unit) in _match.Units())
            if (!unit.IsEssential && (unit.Faction == Faction.Enemy || both))
                cells.Add(cell);
        foreach (var cell in cells)
            _match.Remove(cell);
        _match.PassTurn();
    }

    /// <summary>Capture (Debug) : fait tomber les tuiles du fond ennemi jusqu'à emporter un PION (jamais le
    /// commandant), pour montrer l'éclat de moral qui vole vers la barre.</summary>
    internal void DebugKillEnemyPawn()
    {
        while (_match.NextCollapse(Faction.Enemy) is { } next && !_match.IsOver)
        {
            var unit = _match.UnitAt(next);
            if (unit is { IsEssential: true })
                return;
            _match.CollapseTile(Faction.Enemy);
            if (unit != null)
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
    private const int SelectFrame = 30;
    private readonly int _shotFrame;
    private int _killFrame = -1;   // 50 par défaut ; 3e argument pour capturer plus tard (après le pile ou face)
    private int _strip;            // 1 : ennemi réduit à son commandant ; 2 : plus que des commandants
    private bool _tuto;            // partie tuto (bulles)
    private bool _right;           // « right » : sélection du pion le plus à droite à la frame du 4e argument
    private readonly string _path;
    private readonly GameplayScene _scene;
    private int _frame;

    private PvpBattleShot(GameContext ctx, string path, int shotFrame, bool moraleWin = false, bool tutorial = false)
    {
        _path = path;
        _shotFrame = shotFrame;
        if (moraleWin)
        {
            // Capture de la victoire au moral : seuil abaissé à 9 (le raccourci de test ne peut pas faire tomber
            // assez de pions sans emporter le commandant). Jeu de capture seulement, jamais enregistré.
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Config", "pvp.json"));
            PvpConfig.Load(PvpConfig.FromJson(json.Replace("\"moraleToWin\": 15", "\"moraleToWin\": 9")));
        }
        var rng = new Random(7);
        var local = PvpCatalog.RandomEquipment(PvpProfile.DefaultBuild(), rng);
        var info = tutorial
            ? PvpMatchInfo.ForTutorial(PvpProfile.DefaultBuild(), 1000, rng)   // partie tuto : bulles des règles
            : PvpMatchInfo.ForBot(local, 1500, rng).WithLocalBuild(local);     // 1500 = Argent
        Console.WriteLine($"pvpbattleshot : bot {PvpRanks.BotKey(1500)}, équipements joueur "
            + $"{local.Slots.Count(s => s.EquipmentId != null)}, adversaire {info.OpponentBuild.Slots.Count(s => s.EquipmentId != null)}");
        _scene = new GameplayScene(ctx, info);
        ctx.Scenes.Change(_scene);
    }

    public static PvpBattleShot? FromArgs(GameContext ctx)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-pvpbattleshot", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
            return null;
        var frame = i + 2 < args.Length && int.TryParse(args[i + 2], out var f) && f > SelectFrame ? f : 50;
        // 4e argument : frame où un pion ennemi tombe (éclat de moral), cf. DebugKillEnemyPawn.
        var kill = i + 3 < args.Length && int.TryParse(args[i + 3], out var k) ? k : -1;
        // 5e argument « win » : seuil de moral abaissé, pour capturer l'explosion de la barre.
        var win = i + 4 < args.Length && args[i + 4] == "win";
        // « lone » : à la frame du 4e argument, l'ennemi perd tous ses pions ; « cmds » : les deux camps (égalité).
        var strip = i + 4 < args.Length ? args[i + 4] switch { "lone" => 1, "cmds" => 2, _ => 0 } : 0;
        var tuto = i + 4 < args.Length && args[i + 4] == "tuto";   // « tuto » : partie tuto (bulles d'explication)
        return new PvpBattleShot(ctx, args[i + 1], frame, win, tuto) { _killFrame = kill, _strip = strip, _tuto = tuto,
            _right = i + 4 < args.Length && args[i + 4] == "right" };
    }

    /// <summary>À appeler à la fin du dessin d'une frame (backbuffer complet, avant présentation).</summary>
    public void AfterDraw(GraphicsDevice device, Action exit)
    {
        _frame++;
        if (_frame == SelectFrame && !_right)
            _scene.DebugSelectEquippedPlayer();
        if (_right && _frame == _killFrame)
            _scene.DebugSelectRightmostPlayer();   // fiche contre la colonne des boutons (après le pile ou face)
        if (_tuto)
        {
            if (_frame == _killFrame)
            {
                _scene.DebugSkipFirstTutoBubbles();   // capture de la bulle du moral : un seul pion ennemi tombe
                _scene.DebugKillEnemyPawn();
            }
        }
        else if (_strip > 0)
        {
            if (_frame == _killFrame)
                _scene.DebugStripPawns(both: _strip == 2);
        }
        else if (!_right && _killFrame > 0 && _frame >= _killFrame && _frame < _killFrame + 6)   // jusqu'à six pions, un par frame
            _scene.DebugKillEnemyPawn();
        if (_frame != _shotFrame)
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
