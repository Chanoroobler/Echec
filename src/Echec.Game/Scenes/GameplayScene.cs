using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
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
/// Scène de campagne (première boucle de gameplay) : terrain 8×8, boucle
/// Placement → Combat → Recrutement → … sur 6 combats, le dernier étant le boss.
/// Le commandant (mort = game over) est posé d'office ; le joueur déploie le reste de
/// son inventaire par glisser-déposer depuis le panneau de droite, puis combat l'IA.
/// Échap = menu pause, F1 = visualiseur d'arbres (dev).
/// </summary>
public sealed class GameplayScene : Scene
{
    private const int Columns = 8;
    private const int Rows = 8;
    private const double AiDelaySeconds = 0.45;

    // Remontée du sprite (fraction de la case) pour centrer le socle sur la case. 0 = dans la case.
    private const float SpriteLiftFraction = 0.25f;

    // Panneau latéral droit (inventaire au placement, infos en combat).
    private const int RightPanelWidth = 220;
    private const int PanelPad = 12;
    private const int CardH = 46;
    private const int CardGap = 6;
    private const int PanelListTop = 120;

    // Ordre des colonnes du centre vers les bords (déploiement groupé au milieu).
    private static readonly int[] CenterOut = { 3, 4, 2, 5, 1, 6, 0, 7 };

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

    private Run _run = null!;
    private Match _match = null!;

    // Lien unité déployée → gabarit d'inventaire, pour calculer les pertes après combat.
    private readonly Dictionary<Unit, UnitSpec> _playerSpec = new();
    // Unités du joueur encore dans l'inventaire (non déployées).
    private readonly List<UnitSpec> _pending = new();
    private string _defeatReason = "";

    // Glisser-déposer du placement.
    private UnitSpec? _dragSpec;
    private Cell? _dragFrom; // origine si on déplace une unité déjà posée (null = vient de l'inventaire)

    private Cell? _selected;
    private List<Cell> _legalMoves = new();
    private List<Cell> _attackTargets = new();
    private double _aiTimer;
    private bool _showTrees;

    public GameplayScene(GameContext context) : base(context)
    {
    }

    /// <summary>Viewport logique (espace virtuel) dans lequel l'UI se met en page.</summary>
    private Viewport VirtualViewport =>
        new(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y);

    public override void Load()
    {
        _grassTile = Textures.LoadTileOrPlaceholder(Context.GraphicsDevice, AssetPath("Assets/Tiles/grass.png"));

        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _pauseMenu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _pauseRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);
        _treeRenderer = new DomaineTreeRenderer(Context.Pixel, Context.Font, Context.Style);

        StartRun();
    }

    public override void Unload()
    {
        _grassTile.Dispose();
        foreach (var sprite in _unitSprites.Values)
            sprite?.Dispose();
        _unitSprites.Clear();
    }

    // ── Cycle de campagne ─────────────────────────────────────────────────────────

    private void StartRun()
    {
        _run = new Run();
        BeginPlacement();
    }

    /// <summary>Prépare la phase de placement : nouveau terrain, commandant posé d'office.</summary>
    private void BeginPlacement()
    {
        _match = new Match(Columns, Rows);
        _playerSpec.Clear();
        _pending.Clear();
        _dragSpec = null;
        _dragFrom = null;
        ClearSelection();
        _aiTimer = 0;

        var commander = _run.Commander;
        PlacePlayer(commander, new Cell(Columns / 2, Rows - 1));

        foreach (var spec in _run.Roster)
            if (spec != commander)
                _pending.Add(spec);

        // La vague ennemie est posée dès le placement : le joueur voit le déploiement
        // adverse avant de positionner ses pièces (rangées 0-1, hors zone joueur).
        PlaceEnemies(_run.BuildEnemyWave());
    }

    /// <summary>Fin du placement : lance le combat (la vague ennemie est déjà posée).</summary>
    private void BeginBattle()
    {
        CancelDrag();
        _run.StartBattle();
        ClearSelection();
        _aiTimer = 0;
    }

    private void PlacePlayer(UnitSpec spec, Cell cell)
    {
        var unit = spec.Spawn(Faction.Player);
        _match.Place(cell, unit);
        _playerSpec[unit] = spec;
    }

    private void PlaceEnemies(List<UnitSpec> wave)
    {
        var cells = EnemyDeployCells().ToList();
        var i = 0;
        foreach (var spec in wave)
        {
            while (i < cells.Count && _match.UnitAt(cells[i]) != null) i++;
            if (i >= cells.Count) break;
            _match.Place(cells[i], spec.Spawn(Faction.Enemy));
            i++;
        }
    }

    private static bool IsPlayerZone(Cell cell) => cell.Row >= Rows - 2;

    private static IEnumerable<Cell> PlayerDeployCells()
    {
        for (var row = Rows - 1; row >= Rows - 2; row--)
            foreach (var col in CenterOut)
                yield return new Cell(col, row);
    }

    private static IEnumerable<Cell> EnemyDeployCells()
    {
        for (var row = 0; row <= 1; row++)
            foreach (var col in CenterOut)
                yield return new Cell(col, row);
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

        switch (_run.Phase)
        {
            case RunPhase.Placement: UpdatePlacement(); break;
            case RunPhase.Battle: UpdateBattle(gameTime); break;
            case RunPhase.Recruitment: UpdateRecruitment(); break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                if (Context.Input.WasLeftClicked) StartRun();
                break;
        }
    }

    private void UpdatePlacement()
    {
        if (Context.Input.WasKeyPressed(Keys.Enter))
        {
            BeginBattle();
            return;
        }

        var mouse = Context.Input.MousePosition;
        if (Context.Input.WasLeftClicked)
            BeginDrag(mouse);
        else if (Context.Input.WasLeftReleased && _dragSpec != null)
            EndDrag(mouse);
    }

    private void BeginDrag(Point mouse)
    {
        // 1. Prise depuis l'inventaire (carte du panneau de droite).
        if (PanelCardAt(mouse) is { } i)
        {
            _dragSpec = _pending[i];
            _pending.RemoveAt(i);
            _dragFrom = null;
            return;
        }

        // 2. Prise d'une unité déjà posée (on la retire du terrain en attendant le drop).
        if (CellUnderMouse() is { } cell
            && _match.UnitAt(cell) is { Faction: Faction.Player } unit
            && _playerSpec.TryGetValue(unit, out var spec))
        {
            _dragSpec = spec;
            _dragFrom = cell;
            _match.Remove(cell);
            _playerSpec.Remove(unit);
        }
    }

    private void EndDrag(Point mouse)
    {
        var spec = _dragSpec!;
        var cell = CellUnderMouse();

        if (cell is { } c && IsPlayerZone(c) && _match.UnitAt(c) == null)
            PlacePlayer(spec, c);                       // pose / repositionne
        else if (!spec.Essential && IsOverPanel(mouse))
            _pending.Add(spec);                         // retour à l'inventaire (jamais le commandant)
        else if (_dragFrom is { } from && _match.UnitAt(from) == null)
            PlacePlayer(spec, from);                    // drop invalide : remet à l'origine
        else
            _pending.Add(spec);                         // venait de l'inventaire : y retourne

        _dragSpec = null;
        _dragFrom = null;
    }

    /// <summary>Repose proprement l'unité en cours de glisser (origine ou inventaire).</summary>
    private void CancelDrag()
    {
        if (_dragSpec == null)
            return;

        if (_dragFrom is { } from && _match.UnitAt(from) == null)
            PlacePlayer(_dragSpec, from);
        else
            _pending.Add(_dragSpec);

        _dragSpec = null;
        _dragFrom = null;
    }

    private void UpdateBattle(GameTime gameTime)
    {
        if (_match.CurrentTurn == Faction.Enemy)
            UpdateAiTurn(gameTime);
        else
            UpdatePlayerTurn();

        CheckBattleEnd();
    }

    private void CheckBattleEnd()
    {
        if (!_match.IsOver)
            return;

        if (_match.Winner == Faction.Player)
        {
            var casualties = _playerSpec
                .Where(kv => !kv.Key.IsAlive)
                .Select(kv => kv.Value)
                .ToList();
            _run.CompleteCombat(casualties);
        }
        else
        {
            _defeatReason = CommanderAlive() ? "ARMEE DETRUITE" : "COMMANDANT TOMBE";
            _run.Defeat();
        }

        ClearSelection();
    }

    private bool CommanderAlive() =>
        _playerSpec.Any(kv => kv.Value.Essential && kv.Key.IsAlive);

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

    private void UpdateRecruitment()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var viewport = VirtualViewport;
        var mouse = Context.Input.MousePosition;
        for (var i = 0; i < _run.Draft.Count; i++)
        {
            if (DraftCardRect(i, viewport.Width, viewport.Height).Contains(mouse))
            {
                _run.Recruit(_run.Draft[i]);
                BeginPlacement();
                return;
            }
        }
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
        var viewport = VirtualViewport;

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawTerrain(sb, layout);

        switch (_run.Phase)
        {
            case RunPhase.Placement:
                DrawDeploymentZone(sb, layout);
                DrawUnits(sb, layout);
                DrawPanelBackground(sb);
                DrawPlacementPanel(sb);
                DrawDragGhost(sb);
                break;
            case RunPhase.Battle:
                DrawHighlights(sb, layout);
                DrawUnits(sb, layout);
                DrawPanelBackground(sb);
                DrawBattlePanel(sb);
                break;
            case RunPhase.Recruitment:
                DrawUnits(sb, layout);
                DrawDim(sb, viewport);
                DrawRecruitment(sb, viewport);
                break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                DrawUnits(sb, layout);
                DrawDim(sb, viewport);
                DrawEndHud(sb, viewport);
                break;
        }
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

    private void DrawDeploymentZone(SpriteBatch sb, GridLayout layout)
    {
        foreach (var cell in PlayerDeployCells())
            DrawZone(sb, layout, cell, Palette.Green1 * 0.22f);
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

        // Liseré doré pour les unités pivots (commandant / boss).
        if (unit.IsEssential)
            DrawRectBorder(sb, zone, Palette.Yellow1, 3);

        var sprite = UnitSprite(unit);
        if (sprite != null)
        {
            // Le socle est en bas du sprite : on remonte pour le centrer (haut qui déborde, voulu).
            var lift = (int)(size * SpriteLiftFraction);
            sb.Draw(sprite, new Rectangle(zx, zy - lift, size, size), Color.White);
        }
        else
        {
            // Pas d'asset : placeholder jeton coloré + initiale de la classe.
            var token = new Rectangle(zx + 9, zy + 8, size - 18, size - 26);
            DrawChip(sb, unit.Class, unit.Faction, token);
        }

        var barBg = new Rectangle(zx + 9, zy + size - 14, size - 18, 5);
        DrawRect(sb, barBg, Palette.Black1);
        var ratio = unit.MaxHp == 0 ? 0f : (float)unit.Hp / unit.MaxHp;
        DrawRect(sb, new Rectangle(barBg.X, barBg.Y, (int)(barBg.Width * ratio), barBg.Height), Palette.Green1);
    }

    // ── Panneau latéral ───────────────────────────────────────────────────────────

    private Rectangle PanelRect()
    {
        var vp = VirtualViewport;
        return new Rectangle(vp.Width - RightPanelWidth, 0, RightPanelWidth, vp.Height);
    }

    private bool IsOverPanel(Point p) =>
        p.X >= Context.VirtualResolution.X - RightPanelWidth;

    private Rectangle PanelCardRect(int index)
    {
        var panel = PanelRect();
        return new Rectangle(panel.X + PanelPad, PanelListTop + index * (CardH + CardGap),
            panel.Width - 2 * PanelPad, CardH);
    }

    private int? PanelCardAt(Point p)
    {
        for (var i = 0; i < _pending.Count; i++)
            if (PanelCardRect(i).Contains(p))
                return i;
        return null;
    }

    private void DrawPanelBackground(SpriteBatch sb)
    {
        var panel = PanelRect();
        DrawRect(sb, panel, Palette.Navy2);
        DrawRect(sb, new Rectangle(panel.X, 0, 2, panel.Height), Palette.Navy1);
    }

    private void DrawPlacementPanel(SpriteBatch sb)
    {
        var panel = PanelRect();
        var x = panel.X + PanelPad;

        Context.Font.Draw(sb, CombatTitle(), new Vector2(x, 16), 1, Palette.Yellow1);
        Context.Font.Draw(sb, "PLACEMENT", new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, "INVENTAIRE", new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);

        if (_pending.Count == 0 && _dragSpec == null)
            Context.Font.Draw(sb, "TOUT DEPLOYE", new Vector2(x, PanelListTop + 4), 1, Palette.Cyan2);

        for (var i = 0; i < _pending.Count; i++)
            DrawInventoryCard(sb, _pending[i], PanelCardRect(i));

        var bottom = panel.Height - 44;
        Context.Font.Draw(sb, "GLISSER POUR PLACER", new Vector2(x, bottom), 1, Palette.Blue1);
        Context.Font.Draw(sb, "ENTREE : COMBATTRE", new Vector2(x, bottom + 16), 1, Palette.Cyan1);
    }

    private void DrawInventoryCard(SpriteBatch sb, UnitSpec spec, Rectangle rect)
    {
        Context.Style.DrawPanel(sb, rect);
        var icon = new Rectangle(rect.X + 4, rect.Y + 4, rect.Height - 8, rect.Height - 8);
        DrawChip(sb, spec.UnitClass, Faction.Player, icon);

        var tx = icon.Right + 8;
        Context.Font.Draw(sb, spec.Name.ToUpperInvariant(), new Vector2(tx, rect.Y + 9), 1, Palette.White);
        Context.Font.Draw(sb, $"DOM {spec.Domaine}".ToUpperInvariant(), new Vector2(tx, rect.Y + 25), 1, Palette.Cyan1);
    }

    private void DrawBattlePanel(SpriteBatch sb)
    {
        var panel = PanelRect();
        var x = panel.X + PanelPad;

        Context.Font.Draw(sb, CombatTitle(), new Vector2(x, 16), 1, Palette.Yellow1);

        var objective = _run.IsBossCombat ? "TUER LE BOSS" : "ELIMINER LES ENNEMIS";
        Context.Font.Draw(sb, "OBJECTIF", new Vector2(x, 38), 1, Palette.Blue1);
        Context.Font.Draw(sb, objective, new Vector2(x, 52), 1,
            _run.IsBossCombat ? Palette.Purple5 : Palette.Cyan2);

        var turn = _match.CurrentTurn == Faction.Player ? "TOUR : JOUEUR" : "TOUR : ENNEMI";
        var color = _match.CurrentTurn == Faction.Player ? Palette.Cyan1 : Palette.Purple5;
        Context.Font.Draw(sb, turn, new Vector2(x, 78), 2, color);

        if (_selected is not null && _match.UnitAt(_selected.Value) is { } unit)
            DrawSelectedInfo(sb, unit, x, 120);
    }

    private void DrawSelectedInfo(SpriteBatch sb, Unit unit, int x, int y)
    {
        Context.Font.Draw(sb, unit.Class.Name.ToUpperInvariant(), new Vector2(x, y), 2, Palette.White);
        Context.Font.Draw(sb, $"DOM {unit.Domaine}".ToUpperInvariant(), new Vector2(x, y + 22), 1, Palette.Cyan1);
        Context.Font.Draw(sb, $"PV {unit.Hp}/{unit.MaxHp}", new Vector2(x, y + 38), 1, Palette.Yellow2);
        Context.Font.Draw(sb, $"DEG {unit.Damage}", new Vector2(x, y + 52), 1, Palette.Brown3);
        Context.Font.Draw(sb, $"DEP {unit.MoveRange}   TIR {unit.AttackRange}", new Vector2(x, y + 66), 1, Palette.Cyan2);
    }

    private string CombatTitle() =>
        _run.IsBossCombat ? "COMBAT DE BOSS" : $"COMBAT {_run.CombatNumber} / {Run.TotalCombats}";

    /// <summary>Jeton/sprite d'une unité dessiné dans une zone (placeholder si pas d'asset).</summary>
    private void DrawChip(SpriteBatch sb, UnitClass cls, Faction faction, Rectangle area)
    {
        var sprite = SpriteFor(cls, faction);
        if (sprite != null)
        {
            sb.Draw(sprite, area, Color.White);
            return;
        }

        var color = faction == Faction.Player ? Palette.Cyan1 : Palette.Purple5;
        DrawRect(sb, Inflate(area, 2), Palette.Black1);
        DrawRect(sb, area, color);
        Context.Font.DrawCentered(sb, cls.Name[..1], area, 2, Palette.White);
    }

    private void DrawDragGhost(SpriteBatch sb)
    {
        if (_dragSpec == null)
            return;

        var m = Context.Input.MousePosition;
        const int s = 56;
        DrawChip(sb, _dragSpec.UnitClass, Faction.Player, new Rectangle(m.X - s / 2, m.Y - s / 2, s, s));
    }

    private void DrawRecruitment(SpriteBatch sb, Viewport viewport)
    {
        Context.Font.DrawCentered(sb, "RECRUTEMENT", new Rectangle(0, 60, viewport.Width, 24), 3, Palette.Yellow2);
        Context.Font.DrawCentered(sb, "CHOISIS UNE UNITE A REJOINDRE",
            new Rectangle(0, 100, viewport.Width, 12), 1, Palette.Blue1);

        for (var i = 0; i < _run.Draft.Count; i++)
            DrawDraftCard(sb, _run.Draft[i], DraftCardRect(i, viewport.Width, viewport.Height));
    }

    private void DrawDraftCard(SpriteBatch sb, UnitSpec spec, Rectangle rect)
    {
        Context.Style.DrawPanel(sb, rect);
        var c = spec.UnitClass;

        var icon = new Rectangle(rect.X + (rect.Width - 56) / 2, rect.Y + 12, 56, 56);
        DrawChip(sb, c, Faction.Player, icon);

        Context.Font.DrawCentered(sb, c.Name.ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 6, rect.Width, 14), 2, Palette.White);
        Context.Font.DrawCentered(sb, $"DOM {spec.Domaine}".ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 26, rect.Width, 10), 1, Palette.Cyan1);
        Context.Font.DrawCentered(sb, $"PV {c.MaxHp}   DEG {c.Damage}",
            new Rectangle(rect.X, icon.Bottom + 42, rect.Width, 10), 1, Palette.Yellow2);
        Context.Font.DrawCentered(sb, $"DEP {c.MoveRange}   TIR {c.AttackRange}",
            new Rectangle(rect.X, icon.Bottom + 56, rect.Width, 10), 1, Palette.Cyan2);
    }

    private static Rectangle DraftCardRect(int index, int vpW, int vpH)
    {
        const int cardW = 180, cardH = 190, gap = 28;
        var total = Run.DraftSize * cardW + (Run.DraftSize - 1) * gap;
        var x0 = (vpW - total) / 2;
        var y = (vpH - cardH) / 2 + 20;
        return new Rectangle(x0 + index * (cardW + gap), y, cardW, cardH);
    }

    private void DrawEndHud(SpriteBatch sb, Viewport viewport)
    {
        var victory = _run.Phase == RunPhase.Victory;
        var title = victory ? "VICTOIRE" : "DEFAITE";
        var sub = victory ? "BOSS VAINCU" : _defeatReason;

        Context.Font.DrawCentered(sb, title,
            new Rectangle(0, viewport.Height / 2 - 40, viewport.Width, 28), 4,
            victory ? Palette.Yellow2 : Palette.Purple5);
        Context.Font.DrawCentered(sb, sub,
            new Rectangle(0, viewport.Height / 2 + 4, viewport.Width, 12), 2, Palette.White);
        Context.Font.DrawCentered(sb, "CLIC POUR REJOUER",
            new Rectangle(0, viewport.Height / 2 + 36, viewport.Width, 12), 1, Palette.Blue1);
    }

    // ── Helpers de dessin ───────────────────────────────────────────────────────
    private void DrawRect(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Context.Pixel, r, c);

    private void DrawDim(SpriteBatch sb, Viewport viewport) =>
        DrawRect(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * 0.62f);

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
    /// Place le terrain dans la zone à gauche du panneau avec un zoom ENTIER (les sprites
    /// 64×64 ne sont jamais étirés : pixel-perfect), au plus grand multiple qui rentre,
    /// puis centre. S'adapte à n'importe quelle taille de terrain.
    /// </summary>
    private GridLayout BuildLayout()
    {
        var viewport = VirtualViewport;
        var availWidth = viewport.Width - RightPanelWidth;

        const int baseTile = GridLayout.DefaultTileSize;        // 64
        const int baseSprite = GridLayout.DefaultSpriteHeight;  // 74

        float boardW = Columns * baseTile;                      // largeur à zoom 1
        float boardH = (Rows - 1) * baseTile + baseSprite;      // hauteur à zoom 1

        // Plus grand zoom entier qui tient dans la zone disponible (jamais sous 1).
        int zoom = (int)Math.Min(
            (availWidth - 2f * BoardMargin) / boardW,
            (viewport.Height - 2f * BoardMargin) / boardH);
        zoom = Math.Max(zoom, 1);

        var tile = baseTile * zoom;
        var spriteHeight = baseSprite * zoom;

        var pxW = Columns * tile;
        var pxH = (Rows - 1) * tile + spriteHeight;
        var origin = new Vector2((availWidth - pxW) / 2f, (viewport.Height - pxH) / 2f);

        return new GridLayout(origin, tileSize: tile, spriteWidth: tile,
            spriteHeight: spriteHeight, rowPitch: tile);
    }

    /// <summary>
    /// Sprite à afficher pour une unité : variante selon le camp.
    /// Joueur → &lt;asset&gt;_back, IA → &lt;asset&gt;_ia_front. Repli sur le PNG simple
    /// &lt;asset&gt;, puis null (placeholder) si rien n'est trouvé.
    /// </summary>
    private Texture2D? UnitSprite(Unit unit) => SpriteFor(unit.Class, unit.Faction);

    private Texture2D? SpriteFor(UnitClass cls, Faction faction)
    {
        var variant = faction == Faction.Player ? $"{cls.Asset}_back" : $"{cls.Asset}_ia_front";
        return SpriteFor(variant) ?? SpriteFor(cls.Asset);
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

    private void UpdatePauseMenu()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var viewport = VirtualViewport;
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
