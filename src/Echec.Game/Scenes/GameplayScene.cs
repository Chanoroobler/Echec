using System.Collections.Generic;
using Echec.Core.Battle;
using Echec.Core.Map;
using Echec.Engine;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.UI;
using Echec.Game.Dev;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game.Scenes;

/// <summary>
/// Scène de partie : terrain 8×8, unités joueur vs IA. Déplacement selon le domaine
/// (style) et la classe (portée), combat PV/dégâts, tour par tour rythmé. Pas de
/// level-up pour l'instant. Échap = menu pause, F1 = visualiseur d'arbres (dev).
/// </summary>
public sealed class GameplayScene : Scene
{
    private const int Columns = 8;
    private const int Rows = 8;
    private const double AiDelaySeconds = 0.45;

    // Remontée du sprite (fraction de la case) pour centrer le socle sur la case. 0 = dans la case.
    private const float SpriteLiftFraction = 0.25f;

    // Chemins d'assets résolus depuis le dossier de l'exe (indépendant du répertoire de travail).
    private static string AssetPath(string relative) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, relative);

    private readonly Battlefield _battlefield = Battlefield.CreateFlat(Columns, Rows);

    private Texture2D _grassTile = null!;
    private PauseMenu _pauseMenu = null!;
    private PauseMenuRenderer _pauseRenderer = null!;
    private DomaineTreeRenderer _treeRenderer = null!;

    // Sprites d'unités 64×64 chargés depuis Assets/Units/<asset>.png (null = pas d'asset → placeholder).
    private const string UnitAssetFolder = "Assets/Units";
    private readonly Dictionary<string, Texture2D?> _unitSprites = new();

    private Match _match = null!;
    private Cell? _selected;
    private List<Cell> _legalMoves = new();
    private List<Cell> _attackTargets = new();
    private double _aiTimer;
    private bool _showTrees;

    public GameplayScene(GameContext context) : base(context)
    {
    }

    public override void Load()
    {
        _grassTile = Textures.LoadTileOrPlaceholder(Context.GraphicsDevice, AssetPath("Assets/Tiles/grass.png"));

        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _pauseMenu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _pauseRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);
        _treeRenderer = new DomaineTreeRenderer(Context.Pixel, Context.Font, Context.Style);

        StartMatch();
    }

    public override void Unload()
    {
        _grassTile.Dispose();
        foreach (var sprite in _unitSprites.Values)
            sprite?.Dispose();
        _unitSprites.Clear();
    }

    /// <summary>
    /// Sprite à afficher pour une unité : variante selon le camp.
    /// Joueur → &lt;asset&gt;_back, IA → &lt;asset&gt;_ia_front. Repli sur le PNG simple
    /// &lt;asset&gt;, puis null (placeholder) si rien n'est trouvé.
    /// </summary>
    private Texture2D? UnitSprite(Unit unit)
    {
        var asset = unit.Class.Asset;
        var variant = unit.Faction == Faction.Player ? $"{asset}_back" : $"{asset}_ia_front";
        return SpriteFor(variant) ?? SpriteFor(asset);
    }

    /// <summary>Charge un PNG d'unité par nom de fichier (mis en cache), ou null s'il est absent.</summary>
    private Texture2D? SpriteFor(string fileName)
    {
        if (!_unitSprites.TryGetValue(fileName, out var sprite))
        {
            sprite = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"{UnitAssetFolder}/{fileName}.png"));
            _unitSprites[fileName] = sprite;
        }
        return sprite;
    }

    // Mix des 5 domaines pour visualiser tous les déplacements en jeu (placeholder POC).
    private static readonly (int Column, Domaine Domaine)[] PlayerSetup =
    {
        (2, Domaine.Pion), (3, Domaine.Fou), (4, Domaine.Cavalier)
    };

    private static readonly (int Column, Domaine Domaine)[] EnemySetup =
    {
        (2, Domaine.Tour), (3, Domaine.Dame), (4, Domaine.Pion)
    };

    private void StartMatch()
    {
        _match = new Match(Columns, Rows);
        foreach (var (column, domaine) in PlayerSetup)
            _match.Place(new Cell(column, Rows - 1), Units.Of(domaine, Faction.Player));
        foreach (var (column, domaine) in EnemySetup)
            _match.Place(new Cell(column, 0), Units.Of(domaine, Faction.Enemy));

        ClearSelection();
        _aiTimer = 0;
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────
    public override void Update(GameTime gameTime)
    {
        // Outil dev (F1) : prioritaire, fige le reste.
        if (Context.Input.WasKeyPressed(Keys.F1) && !_pauseMenu.IsOpen)
            _showTrees = !_showTrees;
        if (_showTrees)
        {
            if (Context.Input.WasKeyPressed(Keys.Escape))
                _showTrees = false;
            return;
        }

        if (Context.Input.WasKeyPressed(Keys.Escape))
        {
            if (_pauseMenu.IsOpen) _pauseMenu.Back();
            else _pauseMenu.Open();
        }

        if (_pauseMenu.IsOpen) { UpdatePauseMenu(); return; }

        if (_match.IsOver)
        {
            if (Context.Input.WasLeftClicked) StartMatch();
            return;
        }

        if (_match.CurrentTurn == Faction.Enemy) { UpdateAiTurn(gameTime); return; }

        UpdatePlayerTurn();
    }

    private void UpdatePlayerTurn()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var hit = CellUnderMouse();
        if (hit is null) { ClearSelection(); return; }
        var cell = hit.Value;

        if (_selected is not null && _attackTargets.Contains(cell))
        {
            _match.TryAttack(_selected.Value, cell);
            EndPlayerAction();
            return;
        }

        if (_selected is not null && _legalMoves.Contains(cell))
        {
            _match.TryMove(_selected.Value, cell);
            EndPlayerAction();
            return;
        }

        var unit = _match.UnitAt(cell);
        if (unit is { Faction: Faction.Player })
        {
            _selected = cell;
            _legalMoves = _match.LegalMoves(cell);
            _attackTargets = _match.AttackTargets(cell);
        }
        else
        {
            ClearSelection();
        }
    }

    private void EndPlayerAction()
    {
        ClearSelection();
        if (_match.CurrentTurn == Faction.Enemy && !_match.IsOver)
            _aiTimer = AiDelaySeconds;
    }

    private void UpdateAiTurn(GameTime gameTime)
    {
        _aiTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_aiTimer > 0)
            return;

        var action = EnemyAi.ChooseAction(_match);
        if (action is not { } a)
            return;

        if (a.IsAttack)
            _match.TryAttack(a.From, a.To);
        else
            _match.TryMove(a.From, a.To);
    }

    private void ClearSelection()
    {
        _selected = null;
        _legalMoves.Clear();
        _attackTargets.Clear();
    }

    private Cell? CellUnderMouse()
    {
        var hit = BuildLayout().ScreenToCell(Context.Input.MousePosition, Columns, Rows);
        return hit is null ? null : new Cell(hit.Value.Column, hit.Value.Row);
    }

    // ── Rendu ───────────────────────────────────────────────────────────────────
    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var layout = BuildLayout();
        var viewport = Context.GraphicsDevice.Viewport;

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawTerrain(sb, layout);
        DrawHighlights(sb, layout);
        DrawUnits(sb, layout);
        DrawHud(sb);
        sb.End();

        if (_showTrees)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            _treeRenderer.Draw(sb, viewport.Width, viewport.Height);
            sb.End();
        }
        else if (_pauseMenu.IsOpen)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(sb, _pauseMenu, viewport.Width, viewport.Height,
                Context.Input.MousePosition.ToVector2(), Context.Input.IsLeftDown);
            sb.End();
        }
    }

    private void DrawTerrain(SpriteBatch sb, GridLayout layout)
    {
        foreach (var cell in _battlefield.Cells())
            sb.Draw(_grassTile, layout.CellToSpriteRect(cell.Column, cell.Row), Color.White);
    }

    private void DrawHighlights(SpriteBatch sb, GridLayout layout)
    {
        if (_selected is not null)
            DrawZoneBorder(sb, layout, _selected.Value, Palette.Yellow2, 3);

        foreach (var cell in _legalMoves)         // déplacement = jaune
            DrawZone(sb, layout, cell, Palette.Yellow2 * 0.30f);

        foreach (var cell in _attackTargets)      // cible de tir = rouge
            DrawZone(sb, layout, cell, Palette.Purple5 * 0.50f);
    }

    private void DrawUnits(SpriteBatch sb, GridLayout layout)
    {
        foreach (var (cell, unit) in _match.Units())
            DrawUnit(sb, layout, cell, unit);
    }

    private void DrawUnit(SpriteBatch sb, GridLayout layout, Cell cell, Unit unit)
    {
        var top = layout.CellToScreen(cell.Column, cell.Row);
        var size = layout.TileSize;
        var zx = (int)top.X;
        var zy = (int)top.Y;
        var zone = new Rectangle(zx, zy, size, size);
        var factionColor = unit.Faction == Faction.Player ? Palette.Cyan1 : Palette.Purple5;

        var sprite = UnitSprite(unit);
        if (sprite != null)
        {
            // Le socle est en bas du sprite : on remonte d'une demi-case pour qu'il
            // tombe au centre de la case (la partie haute déborde au-dessus, c'est voulu).
            var lift = (int)(size * SpriteLiftFraction);
            sb.Draw(sprite, new Rectangle(zx, zy - lift, size, size), Color.White);
        }
        else
        {
            // Pas d'asset : placeholder jeton coloré + initiale du domaine.
            var token = new Rectangle(zx + 9, zy + 8, size - 18, size - 26);
            DrawRect(sb, Inflate(token, 2), Palette.Black1);
            DrawRect(sb, token, factionColor);
            Context.Font.DrawCentered(sb, unit.Domaine.ToString()[..1], token, 2, Palette.White);
        }

        var barBg = new Rectangle(zx + 9, zy + size - 14, size - 18, 5);
        DrawRect(sb, barBg, Palette.Black1);
        var ratio = unit.MaxHp == 0 ? 0f : (float)unit.Hp / unit.MaxHp;
        DrawRect(sb, new Rectangle(barBg.X, barBg.Y, (int)(barBg.Width * ratio), barBg.Height), Palette.Green1);
    }

    private void DrawHud(SpriteBatch sb)
    {
        var viewport = Context.GraphicsDevice.Viewport;

        if (_match.IsOver)
        {
            var win = _match.Winner == Faction.Player;
            var area = new Rectangle(0, viewport.Height / 2 - 30, viewport.Width, 24);
            Context.Font.DrawCentered(sb, win ? "VICTOIRE" : "DEFAITE", area, 4,
                win ? Palette.Yellow2 : Palette.Purple5);
            Context.Font.DrawCentered(sb, "CLIC POUR REJOUER",
                new Rectangle(0, viewport.Height / 2 + 16, viewport.Width, 12), 1, Palette.White);
            return;
        }

        var label = _match.CurrentTurn == Faction.Player ? "TOUR : JOUEUR" : "TOUR : ENNEMI";
        var color = _match.CurrentTurn == Faction.Player ? Palette.Cyan1 : Palette.Purple5;
        Context.Font.Draw(sb, label, new Vector2(12, 12), 2, color);
        Context.Font.Draw(sb, "F1 : ARBRES", new Vector2(viewport.Width - 96, 12), 1, Palette.Blue1);

        if (_selected is not null && _match.UnitAt(_selected.Value) is { } unit)
            Context.Font.Draw(sb, Describe(unit), new Vector2(12, viewport.Height - 22), 1, Palette.White);
    }

    private static string Describe(Unit unit) =>
        $"{unit.Domaine} - {unit.Class.Name}  PV {unit.Hp}/{unit.MaxHp}  DEG {unit.Damage}  DEP {unit.MoveRange}  TIR {unit.AttackRange}";

    // ── Helpers de dessin ───────────────────────────────────────────────────────
    private void DrawRect(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Context.Pixel, r, c);

    private void DrawZone(SpriteBatch sb, GridLayout layout, Cell cell, Color c)
    {
        var top = layout.CellToScreen(cell.Column, cell.Row);
        DrawRect(sb, new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize), c);
    }

    private void DrawZoneBorder(SpriteBatch sb, GridLayout layout, Cell cell, Color c, int thickness)
    {
        var top = layout.CellToScreen(cell.Column, cell.Row);
        DrawRectBorder(sb, new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize), c, thickness);
    }

    private void DrawRectBorder(SpriteBatch sb, Rectangle r, Color c, int thickness)
    {
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Y, thickness, r.Height), c);
        DrawRect(sb, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    // Marge autour du terrain (px à l'écran).
    private const int BoardMargin = 24;

    /// <summary>
    /// Calcule l'échelle pour que le terrain remplisse le viewport (moins une marge),
    /// puis centre. S'adapte donc à n'importe quelle taille de terrain.
    /// </summary>
    private GridLayout BuildLayout()
    {
        var viewport = Context.GraphicsDevice.Viewport;

        const int baseTile = GridLayout.DefaultTileSize;                       // 64
        const int baseThickness = GridLayout.DefaultSpriteHeight - baseTile;   // 10

        float boardW = Columns * baseTile;
        float boardH = (Rows - 1) * baseTile + GridLayout.DefaultSpriteHeight;

        var scale = Math.Min(
            (viewport.Width - 2f * BoardMargin) / boardW,
            (viewport.Height - 2f * BoardMargin) / boardH);
        scale = Math.Max(scale, 1f); // ne descend pas sous la taille de base

        var tile = (int)(baseTile * scale);
        var spriteHeight = tile + (int)(baseThickness * scale);

        var pxW = Columns * tile;
        var pxH = (Rows - 1) * tile + spriteHeight;
        var origin = new Vector2((viewport.Width - pxW) / 2f, (viewport.Height - pxH) / 2f);

        return new GridLayout(origin, tileSize: tile, spriteWidth: tile,
            spriteHeight: spriteHeight, rowPitch: tile);
    }

    private void UpdatePauseMenu()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var viewport = Context.GraphicsDevice.Viewport;
        var action = _pauseMenu.HandleClick(Context.Input.MousePosition, viewport.Width, viewport.Height);
        switch (action)
        {
            case MenuAction.Quit:
                Context.Quit();
                break;
            case MenuAction.GraphicsChanged:
                Context.Display.Apply(Context.Settings.Display);
                break;
            case MenuAction.VolumeChanged:
                Context.Audio.Apply();
                break;
        }
    }
}
