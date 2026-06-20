using Echec.Core.Battle;
using Echec.Core.Map;
using Echec.Engine;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game.Scenes;

/// <summary>
/// Scène de partie : terrain 8×8, unités joueur vs IA, déplacement façon échecs et
/// combat PV/dégâts. Tour par tour rythmé : le joueur déplace une unité, puis l'IA
/// joue après un court délai. Échap ouvre le menu pause (overlay souris).
/// </summary>
public sealed class GameplayScene : Scene
{
    private const int Columns = 8;
    private const int Rows = 8;
    private const string GrassTilePath = "Assets/Tiles/grass.png";
    private const double AiDelaySeconds = 0.45;

    private readonly Battlefield _battlefield = Battlefield.CreateFlat(Columns, Rows);

    private Texture2D _grassTile = null!;
    private PauseMenu _pauseMenu = null!;
    private PauseMenuRenderer _pauseRenderer = null!;

    private Match _match = null!;
    private Cell? _selected;
    private System.Collections.Generic.List<Cell> _legal = new();
    private double _aiTimer;

    public GameplayScene(GameContext context) : base(context)
    {
    }

    public override void Load()
    {
        _grassTile = Textures.LoadTileOrPlaceholder(Context.GraphicsDevice, GrassTilePath);

        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _pauseMenu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _pauseRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);

        StartMatch();
    }

    public override void Unload() => _grassTile.Dispose();

    private void StartMatch()
    {
        _match = new Match(Columns, Rows);
        // 3 unités par camp, colonnes centrales. Joueur en bas (rangée 7), ennemi en haut (rangée 0).
        foreach (var column in new[] { 2, 3, 4 })
        {
            _match.Place(new Cell(column, Rows - 1), Units.Soldier(Faction.Player));
            _match.Place(new Cell(column, 0), Units.Soldier(Faction.Enemy));
        }
        _selected = null;
        _legal.Clear();
        _aiTimer = 0;
    }

    public override void Update(GameTime gameTime)
    {
        if (Context.Input.WasKeyPressed(Keys.Escape))
        {
            if (_pauseMenu.IsOpen) _pauseMenu.Back();
            else _pauseMenu.Open();
        }

        if (_pauseMenu.IsOpen)
        {
            UpdatePauseMenu();
            return;
        }

        if (_match.IsOver)
        {
            if (Context.Input.WasLeftClicked)
                StartMatch(); // rejouer
            return;
        }

        if (_match.CurrentTurn == Faction.Enemy)
        {
            UpdateAiTurn(gameTime);
            return;
        }

        UpdatePlayerTurn();
    }

    private void UpdatePlayerTurn()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var hit = CellUnderMouse();
        if (hit is null)
        {
            ClearSelection();
            return;
        }

        var cell = hit.Value;

        // Clic sur une destination légale de l'unité sélectionnée → déplacement/attaque.
        if (_selected is not null && _legal.Contains(cell))
        {
            _match.TryMove(_selected.Value, cell);
            ClearSelection();
            if (_match.CurrentTurn == Faction.Enemy)
                _aiTimer = AiDelaySeconds; // déclenche le tour IA après un délai
            return;
        }

        // Sinon : (dé)sélection d'une unité joueur.
        var unit = _match.UnitAt(cell);
        if (unit is { Faction: Faction.Player })
        {
            _selected = cell;
            _legal = _match.LegalMoves(cell);
        }
        else
        {
            ClearSelection();
        }
    }

    private void UpdateAiTurn(GameTime gameTime)
    {
        _aiTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_aiTimer > 0)
            return;

        var move = EnemyAi.ChooseMove(_match);
        if (move is not null)
            _match.TryMove(move.Value.From, move.Value.To);
    }

    private void ClearSelection()
    {
        _selected = null;
        _legal.Clear();
    }

    private Cell? CellUnderMouse()
    {
        var layout = BuildLayout();
        var hit = layout.ScreenToCell(Context.Input.MousePosition, Columns, Rows);
        return hit is null ? null : new Cell(hit.Value.Column, hit.Value.Row);
    }

    // ──────────────────────────────────────────────────────────────────────────
    public override void Draw(GameTime gameTime)
    {
        var spriteBatch = Context.SpriteBatch;
        var layout = BuildLayout();

        spriteBatch.Begin(samplerState: SamplerState.PointClamp);

        DrawTerrain(spriteBatch, layout);
        DrawHighlights(spriteBatch, layout);
        DrawUnits(spriteBatch, layout);
        DrawHud(spriteBatch);

        spriteBatch.End();

        if (_pauseMenu.IsOpen)
        {
            var viewport = Context.GraphicsDevice.Viewport;
            spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(spriteBatch, _pauseMenu, viewport.Width, viewport.Height,
                Context.Input.MousePosition.ToVector2(), Context.Input.IsLeftDown);
            spriteBatch.End();
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

        foreach (var cell in _legal)
        {
            var enemyHere = _match.UnitAt(cell) is { Faction: Faction.Enemy };
            var tint = (enemyHere ? Palette.Purple5 : Palette.Yellow2) * (enemyHere ? 0.45f : 0.30f);
            DrawZone(sb, layout, cell, tint);
        }
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
        var zoneX = (int)top.X;
        var zoneY = (int)top.Y;

        var token = new Rectangle(zoneX + 9, zoneY + 6, size - 18, size - 24);
        var fill = unit.Faction == Faction.Player ? Palette.Cyan1 : Palette.Purple5;

        DrawRect(sb, Inflate(token, 2), Palette.Black1); // contour
        DrawRect(sb, token, fill);
        Context.Font.DrawCentered(sb, LetterFor(unit.Type), token, 2, Palette.White);

        // Barre de PV en bas de la case.
        var barBg = new Rectangle(zoneX + 9, zoneY + size - 14, size - 18, 5);
        DrawRect(sb, barBg, Palette.Black1);
        var ratio = unit.MaxHp == 0 ? 0f : (float)unit.Hp / unit.MaxHp;
        var fg = new Rectangle(barBg.X, barBg.Y, (int)(barBg.Width * ratio), barBg.Height);
        DrawRect(sb, fg, Palette.Green1);
    }

    private void DrawHud(SpriteBatch sb)
    {
        if (_match.IsOver)
        {
            var win = _match.Winner == Faction.Player;
            var banner = win ? "VICTOIRE" : "DEFAITE";
            var viewport = Context.GraphicsDevice.Viewport;
            var area = new Rectangle(0, viewport.Height / 2 - 30, viewport.Width, 24);
            Context.Font.DrawCentered(sb, banner, area, 4, win ? Palette.Yellow2 : Palette.Purple5);
            var sub = new Rectangle(0, viewport.Height / 2 + 16, viewport.Width, 12);
            Context.Font.DrawCentered(sb, "CLIC POUR REJOUER", sub, 1, Palette.White);
            return;
        }

        var label = _match.CurrentTurn == Faction.Player ? "TOUR : JOUEUR" : "TOUR : ENNEMI";
        var color = _match.CurrentTurn == Faction.Player ? Palette.Cyan1 : Palette.Purple5;
        Context.Font.Draw(sb, label, new Vector2(12, 12), 2, color);
    }

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
        var r = new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize);
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Y, thickness, r.Height), c);
        DrawRect(sb, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static string LetterFor(UnitType type) => type switch
    {
        UnitType.Soldier => "S",
        _ => "?"
    };

    private GridLayout BuildLayout()
    {
        var board = GridLayout.MeasureBoard(Columns, Rows);
        var viewport = Context.GraphicsDevice.Viewport;
        var origin = new Vector2(
            (viewport.Width - board.X) / 2f,
            (viewport.Height - board.Y) / 2f);
        return new GridLayout(origin);
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
