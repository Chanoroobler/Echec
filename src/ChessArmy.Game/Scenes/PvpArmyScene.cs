using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.Scenes;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Steamworks;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// ÉCRAN ARMÉE du multijoueur (seul écran du mode, cf. docs/multijoueur-interface.md) : on y arrive directement
/// depuis le menu principal. Barre du haut (rang, cote, bilan, points, COFFRES, RETOUR), 3 armées, panneau du
/// commandant, le vrai plateau 8×8, la réserve paginée PIONS / ÉQUIPEMENTS avec sa fiche, et la bande du bas
/// (DÉFIER UN AMI, budget, LANCER UNE PARTIE). La recherche tourne SANS quitter l'écran (l'armée reste modifiable) ;
/// adversaire trouvé : l'armée sélectionnée est verrouillée, 5 s, puis le combat. La vue COFFRES remplace le
/// build dans la même scène. Les gestes de placement sont ceux de <see cref="PvpPlacement"/> (Core, testés).
///
/// Disposition : celle des maquettes en 1280×720 (canevas des écrans 1440p) ; variante compacte en 960×540
/// (1080p) où le plateau ne montre que ses rangées du bas (les deux lignes du joueur restent visibles).
/// </summary>
public sealed partial class PvpArmyScene : Scene
{
    private enum View { Build, Chests, Ranking }

    private enum Hit
    {
        None, ArmyTab, CmdPrev, CmdNext, Cell, Badge, TabUnits, TabEquip, Card, PagePrev, PageNext,
        ToReserve, Friend, Launch, Cancel, Chests, Back, ChestOpen, RevealOk, TutorialOk, ReserveArea, BoardArea,
        Ranking, RankRow, RankTab, RankPrev, RankNext, HistTab,
        Help, TutorialPrev, TutorialSkip,
        Training, TrainingPick, TrainingClose, TrainingTuto,
    }

    private readonly struct Target
    {
        public readonly Rectangle Rect;
        public readonly Hit Kind;
        public readonly int Index;
        public readonly bool Focusable;
        public Target(Rectangle rect, Hit kind, int index, bool focusable) { Rect = rect; Kind = kind; Index = index; Focusable = focusable; }
    }

    /// <summary>Ce que le joueur tient : un pion ou un équipement, venu de la réserve ou d'une case.</summary>
    private struct Held
    {
        public bool Active;
        public bool IsEquip;
        public string Id;
        public int FromColumn, FromLine;   // -1 : vient de la réserve
        public bool Sticky;                // pris au clic (reste en main) ; sinon glisser-déposer
        public bool FromBoard => FromColumn >= 0;
    }

    private const int CardsPerPageMax = 32;
    private const float DragThreshold = 4f;
    private const float MessageSeconds = 2.6f;

    private readonly List<Target> _targets = new();
    private readonly PvpFocus _focus = new();
    private readonly List<int> _focusMap = new();   // indice de focus → indice dans _targets
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Random _rng = new();
    private PvpBoardRenderer _board = null!;
    private View _view;
    private int _tab;
    private bool _reserveEquip;
    private readonly int[] _page = new int[2];
    private Held _held;
    private bool _pressing;
    private Hit _pressKind;
    private int _pressIndex;
    private Point _pressStart;
    private string _message = "";
    private bool _messageError;
    private float _messageTime;
    private float _time;
    private bool _tutorialOpen;
    private Point _pointer;
    private int _hover = -1;   // cible sous le pointeur (indice dans _targets), -1 sinon

    public PvpArmyScene(GameContext context, CSteamID? invite = null) : base(context)
    {
        _invite = invite ?? CSteamID.Nil;
    }

    /// <summary>Retour depuis l'écran de fin d'une partie : REJOUER relance aussitôt la recherche avec la même armée
    /// (<paramref name="friend"/> : rouvre un défi entre amis ; entraînement : même niveau de bot), ou le bandeau
    /// coffre ouvre la vue Coffres.</summary>
    public enum Entry { Build, Replay, ReplayFriend, ReplayTraining, Chests }

    private readonly Entry _entry;

    public PvpArmyScene(GameContext context, Entry entry) : this(context)
    {
        _entry = entry;
    }

    private static PvpProfile Profile => PvpProfileStore.Current;
    private PvpBuild Build => Profile.Builds[_tab];
    // La grille peut changer de taille (canevas compact / complet) : la page reste dans les bornes.
    private int Page => _lay.CardCount > 0 ? Math.Min(_page[_reserveEquip ? 1 : 0], PageCount - 1) : _page[_reserveEquip ? 1 : 0];
    private bool Locked => _locked != null;   // adversaire trouvé : armée verrouillée
    private bool ArmyFrozen => Locked || _mode != SearchMode.Idle;   // recherche lancée : plus aucune modification d'armée

    public override void Load()
    {
        _board = new PvpBoardRenderer(Context);
        Context.Music.Play(Engine.Audio.MusicScene.Calm);
        _tab = Math.Clamp(Profile.SelectedBuild, 0, PvpProfile.BuildSlots - 1);
        _tutorialOpen = Profile.TutorialPending && _invite == CSteamID.Nil;   // une seule fois (cf. Tutorial.cs)
        _tuto.Restart();
        if (_invite != CSteamID.Nil)
            JoinInvite(_invite);
        else if (_entry is Entry.Replay or Entry.ReplayFriend && !_tutorialOpen)
        {
            _friend = _entry == Entry.ReplayFriend;
            OnLaunch();   // même point d'entrée que le bouton LANCER (recherche, ou bot hors ligne)
        }
        else if (_entry == Entry.ReplayTraining && _trainingReplay != null && !_tutorialOpen)
            StartTraining(_trainingReplay);
        else if (_entry == Entry.Chests && !_tutorialOpen)
            OpenChests();
    }

    /// <summary>
    /// Bandes du letterbox (écran ultra-large / non 16:9) : même fond uni que le canvas et mêmes voiles,
    /// empilés dans l'ordre de <see cref="Draw"/>, pour un fond continu sur toute la fenêtre.
    /// </summary>
    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale)
    {
        var sb = Context.SpriteBatch;
        var full = new Rectangle(0, 0, realScreen.X, realScreen.Y);
        var veil = Palette.Black1 * 0.8f;   // = DrawReel / DrawReveal / DrawTutorial / DrawError
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Px, full, Palette.Black3);
        if (_view == View.Chests && _rolling)
            sb.Draw(Px, full, veil);
        if (_view == View.Chests && _revealOpen && _reward != null)
        {
            sb.Draw(Px, full, veil);
            var step = (int)(_revealT / 0.06f);   // = DrawRevealFlash
            if (step < 2)
                sb.Draw(Px, full, Color.White * (step == 0 ? 0.75f : 0.35f));
        }
        if (_tutorialOpen)
            sb.Draw(Px, full, veil);
        if (_trainingOpen && _view == View.Build)
            sb.Draw(Px, full, veil);
        if (_error != null)
            sb.Draw(Px, full, veil);
        sb.End();
    }

    public override void Unload()
    {
        StopMatchmaking();
        PvpProfileStore.Save();
        _board.Dispose();
        DisposeRays();   // images des rayons de la révélation des coffres
        _sheetSmall?.Dispose();
        _sheetSmall = null;
        foreach (var t in _textures.Values)
            t?.Dispose();
        _textures.Clear();
    }

    // ── Contenu de la réserve ────────────────────────────────────────────────────

    private sealed class UnitEntry
    {
        public required UnitClass Class;
        public required Domaine Domaine;
        public required bool Owned;
        public required string Name;
        public required string Cost;
        public required int Price;
    }

    private sealed class EquipEntry
    {
        public required Equipment Equipment;
        public required bool Owned;
        public required string Name;
        public required string Cost;
        public required int Price;
    }

    private readonly List<UnitEntry> _units = new();
    private readonly List<EquipEntry> _equips = new();
    private (Language Lang, bool Full, int Unlocks) _entriesKey = (Language.Francais, false, -1);
    private int _unlockVersion;   // +1 à chaque coffre ouvert

    /// <summary>Listes de la réserve (débloqués d'abord, puis par coût), reconstruites seulement quand la langue,
    /// la disposition ou les déblocages changent : les noms tronqués et les prix sont prêts pour le dessin.</summary>
    private void EnsureEntries()
    {
        var key = (Loc.Current, _lay.Full, _unlockVersion);
        if (key == _entriesKey)
            return;
        _entriesKey = key;
        var config = PvpConfig.Current;
        var nameW = _lay.CardW - 4;
        _units.Clear();
        foreach (var (dom, cls) in PvpCatalog.AllUnitClasses()
                     .OrderBy(c => Profile.Owns(c.Class.Asset) ? 0 : 1).ThenBy(c => config.CostOf(c.Class))
                     .ThenBy(c => c.Class.Tier).ThenBy(c => c.Class.Asset, StringComparer.Ordinal))
            _units.Add(new UnitEntry
            {
                Class = cls, Domaine = dom, Owned = Profile.Owns(cls.Asset), Price = config.CostOf(cls),
                Name = PvpArt.Fit(Context.Font, PvpUi.UnitName(cls), nameW, 1), Cost = config.CostOf(cls).ToString(),
            });
        _equips.Clear();
        foreach (var e in PvpCatalog.AllEquipment()
                     .OrderBy(e => Profile.Owns(e.Id) ? 0 : 1).ThenBy(e => config.CostOf(e)).ThenBy(e => e.Rarity)
                     .ThenBy(e => e.Id, StringComparer.Ordinal))
            _equips.Add(new EquipEntry
            {
                Equipment = e, Owned = Profile.Owns(e.Id), Price = config.CostOf(e),
                Name = PvpArt.Fit(Context.Font, EquipmentNames.Localized(e).ToUpperInvariant(), nameW, 1),
                Cost = config.CostOf(e).ToString(),
            });
    }

    private int EntryCount => _reserveEquip ? _equips.Count : _units.Count;
    private int PageCount => Math.Max(1, (EntryCount + _lay.CardCount - 1) / _lay.CardCount);

    private readonly List<CommandeDef> _ownedCommanders = new();
    private int _ownedCommandersVersion = -1;

    /// <summary>Commandants débloqués (ceux que proposent les flèches), relus seulement après un coffre.</summary>
    private List<CommandeDef> OwnedCommanders()
    {
        if (_ownedCommandersVersion != _unlockVersion)
        {
            _ownedCommandersVersion = _unlockVersion;
            _ownedCommanders.Clear();
            _ownedCommanders.AddRange(PvpCatalog.Commanders().Where(c => Profile.Owns(c.Id)));
        }
        return _ownedCommanders;
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────────

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _time += dt;
        if (_messageTime > 0f) _messageTime -= dt;
        RenderSmallSheet();   // fiche en demi-taille préparée à la frame précédente : rendue ici, hors de tout lot
        ComputeLayout();
        EnsureEntries();
        BuildTargets();

        UpdateMatch(dt);   // recherche, adversaire trouvé, compte à rebours (continue même dans la vue Coffres)
        if (_leaving)
            return;

        var input = Context.Input;
        if (_tutorialOpen)
        {
            UpdateTutorial(input);
            return;
        }
        if (_error != null)
        {
            if (input.WasConfirmPressed || input.WasCancelPressed || input.WasLeftClicked
                || input.WasKeyPressed(Keys.Enter) || input.WasKeyPressed(Keys.Escape))
                ResetAfterError();
            return;
        }

        if (_view == View.Chests)
        {
            UpdateChests(dt);
            return;
        }
        if (_view == View.Ranking)
        {
            UpdateRanking();
            return;
        }

        _focus.Navigate(input);
        _pointer = _focus.Pointer(input);
#if DEBUG
        if (DebugPointer is { } forced) _pointer = forced;
        if (input.WasKeyPressed(Keys.F9))
        {
            // Tests du dev : sauvegarde multi supprimée, retour au profil par défaut.
            PvpProfileStore.ResetToDefault();
            Context.Scenes.Change(new PvpArmyScene(Context));
            return;
        }
#endif
        _hover = HitIndexAt(_pointer);

        // Annuler / lâcher / retour.
        if (input.WasKeyPressed(Keys.Escape) || input.WasCancelPressed || input.WasRightClicked)
        {
            if (_held.Active || _pressing)
            {
                CancelHeld();
                Context.Sounds.Play("unit_deselect");
            }
            else if (!input.WasRightClicked)
                OnBack();
            return;
        }

        if (input.UsingGamepad)
            UpdateGamepadShortcuts();
        else if (input.ScrollDelta != 0 && _lay.Res.Contains(_pointer))
            TurnPage(-Math.Sign(input.ScrollDelta));

        if (input.UsingGamepad)
        {
            if (input.WasConfirmPressed && _hover >= 0)
                OnClick(_targets[_hover]);
            return;
        }
        UpdateMouse(input);
    }

    private void UpdateGamepadShortcuts()
    {
        var input = Context.Input;
        if (input.WasLeftShoulderPressed) TurnPage(-1);
        if (input.WasRightShoulderPressed) TurnPage(1);
        if (input.WasLeftTriggerPressed) SetReserveTab(false);
        if (input.WasRightTriggerPressed) SetReserveTab(true);
    }

    private void UpdateMouse(Engine.Input.InputManager input)
    {
        var mouse = input.MousePosition;
        if (input.WasLeftClicked)
        {
            var hit = HitAt(mouse);
            if (_held.Active && _held.Sticky)
            {
                OnClick(hit ?? default);
                return;
            }
            if (hit is { } t && IsDraggable(t))
            {
                _pressing = true;
                _pressKind = t.Kind;
                _pressIndex = t.Index;
                _pressStart = mouse;
                return;
            }
            if (hit is { } b)
                OnClick(b);
            return;
        }

        if (_pressing && input.IsLeftDown && !_held.Active)
        {
            var d = mouse - _pressStart;
            if (d.X * d.X + d.Y * d.Y >= DragThreshold * DragThreshold)
                Pick(_pressKind, _pressIndex, sticky: false);   // glisser-déposer
        }

        if (input.WasLeftReleased && (_pressing || (_held.Active && !_held.Sticky)))
        {
            if (_held.Active && !_held.Sticky)
                Drop(HitAt(mouse));
            else if (_pressing)
                Pick(_pressKind, _pressIndex, sticky: true);   // simple clic : l'objet reste en main
            _pressing = false;
        }
    }

    /// <summary>Clic (souris) ou A (manette) sur une cible. Si on tient quelque chose au clic, c'est un dépôt.</summary>
    private void OnClick(Target t)
    {
        if (_held.Active && _held.Sticky && IsDropTarget(t))
        {
            // Une autre carte de la réserve : on change ce qu'on tient.
            if (t.Kind == Hit.Card && !_held.FromBoard)
            {
                Pick(Hit.Card, t.Index, sticky: true);
                return;
            }
            Drop(t.Kind == Hit.None ? null : t);
            return;
        }
        if (_held.Active)
            CancelHeld();   // un bouton : on remet d'abord en place ce qu'on tient
        if (IsDraggable(t))
        {
            Pick(t.Kind, t.Index, sticky: true);
            return;
        }
        Activate(t);
    }

    private static bool IsDropTarget(Target t) =>
        t.Kind is Hit.None or Hit.Cell or Hit.Badge or Hit.Card or Hit.ToReserve or Hit.ReserveArea or Hit.BoardArea;

    private bool IsDraggable(Target t)
    {
        if (ArmyFrozen)
            return false;
        switch (t.Kind)
        {
            case Hit.Card:
                return true;   // une carte verrouillée explique pourquoi au lieu d'être prise (cf. Pick)
            case Hit.Badge:
                return true;
            case Hit.Cell:
                var (c, line) = CellOf(t.Index);
                return !PvpBuild.IsKingCell(c, line) && line >= 0 && Build.SlotAt(c, line) != null;
            default:
                return false;
        }
    }

    // ── Prendre / poser ──────────────────────────────────────────────────────────

    private void Pick(Hit kind, int index, bool sticky)
    {
        _pressing = false;
        switch (kind)
        {
            case Hit.Card:
                if (_reserveEquip)
                {
                    var e = _equips[index];
                    if (!e.Owned) { Say(Loc.T("pvp.locked_equip"), error: true); return; }
                    _held = new Held { Active = true, IsEquip = true, Id = e.Equipment.Id, FromColumn = -1, FromLine = -1, Sticky = sticky };
                }
                else
                {
                    var u = _units[index];
                    if (!u.Owned) { Say(Loc.T("pvp.locked_unit"), error: true); return; }
                    _held = new Held { Active = true, Id = u.Class.Asset, FromColumn = -1, FromLine = -1, Sticky = sticky };
                }
                break;
            case Hit.Cell:
            {
                var (c, line) = CellOf(index);
                if (Build.SlotAt(c, line) is not { } slot)
                    return;
                _held = new Held { Active = true, Id = slot.ClassAsset, FromColumn = c, FromLine = line, Sticky = sticky };
                break;
            }
            case Hit.Badge:
            {
                var (c, line) = CellOf(index);
                if (Build.SlotAt(c, line) is not { EquipmentId: { } eq })
                    return;
                _held = new Held { Active = true, IsEquip = true, Id = eq, FromColumn = c, FromLine = line, Sticky = sticky };
                break;
            }
            default:
                return;
        }
        Context.Sounds.Play("unit_pick");
    }

    private void CancelHeld()
    {
        _held = default;
        _pressing = false;
    }

    /// <summary>Lâche ce qu'on tient sur <paramref name="target"/> (null : dans le vide), selon le tableau des
    /// interactions du document. Au clic, un objet de la réserve reste en main pour en poser plusieurs.</summary>
    private void Drop(Target? target)
    {
        var h = _held;
        var keep = false;   // garder l'objet en main après le geste
        var kind = target?.Kind ?? Hit.None;
        int column = -1, line = -1;
        var onCell = kind is Hit.Cell or Hit.Badge;
        if (onCell)
            (column, line) = CellOf(target!.Value.Index);
        var playerCell = onCell && line >= 0;
        var toReserve = kind is Hit.ToReserve or Hit.ReserveArea or Hit.Card;
        PlacementResult result;

        if (!h.IsEquip && !h.FromBoard)
        {
            // Pion de la réserve.
            if (playerCell)
                result = PvpPlacement.PlaceUnit(Build, column, line, h.Id);
            else
                result = onCell ? PlacementResult.OutOfZone : PlacementResult.Unchanged;
            keep = h.Sticky && !toReserve && kind != Hit.None;
        }
        else if (!h.IsEquip)
        {
            // Pion posé : déplacé / échangé sur une case du joueur ; retiré ailleurs (réserve, hors des lignes, vide).
            result = playerCell
                ? PvpPlacement.MoveUnit(Build, h.FromColumn, h.FromLine, column, line)
                : PvpPlacement.RemoveUnit(Build, h.FromColumn, h.FromLine);
            if (playerCell && result == PlacementResult.KingCell)
                keep = h.Sticky;
        }
        else if (!h.FromBoard)
        {
            // Équipement de la réserve.
            if (onCell)
                result = playerCell ? PvpPlacement.Equip(Build, column, line, h.Id) : PlacementResult.NoUnit;
            else
                result = PlacementResult.Unchanged;
            keep = h.Sticky && !toReserve && kind != Hit.None;
        }
        else
        {
            // Badge d'équipement : vers un autre pion (échange s'il est équipé) ; retiré hors du plateau ou sur la réserve.
            if (onCell)
                result = playerCell ? PvpPlacement.MoveEquipment(Build, h.FromColumn, h.FromLine, column, line) : PlacementResult.NoUnit;
            else
                result = PvpPlacement.RemoveEquipment(Build, h.FromColumn, h.FromLine);
            keep = h.Sticky && onCell && result != PlacementResult.Ok && result != PlacementResult.Unchanged;
        }

        Report(result, h);
        if (result == PlacementResult.Ok)
            PvpProfileStore.Save();
        if (!keep)
            CancelHeld();
    }

    private void Report(PlacementResult result, Held h)
    {
        switch (result)
        {
            case PlacementResult.Ok:
                Context.Sounds.Play("unit_place");
                _messageTime = 0f;
                break;
            case PlacementResult.Unchanged:
                break;
            case PlacementResult.OverBudget:
                Say(Loc.T("pvp.err_budget", HeldName(h)), error: true);
                break;
            case PlacementResult.KingCell:
                Say(Loc.T("pvp.err_king"), error: true);
                break;
            case PlacementResult.TooManyCopies:
                Say(Loc.T("pvp.err_copies", PvpConfig.Current.MaxSameEquipment), error: true);
                break;
            case PlacementResult.CommanderNoEquipment:
                Say(Loc.T("pvp.err_commander_equip"), error: true);
                break;
            case PlacementResult.NoUnit:
                Say(Loc.T("pvp.err_no_unit"), error: true);
                break;
            case PlacementResult.OutOfZone:
                Say(Loc.T("pvp.err_zone"), error: true);
                break;
            default:
                Say(Loc.T("pvp.err_army"), error: true);
                break;
        }
    }

    private static string HeldName(Held h) =>
        h.IsEquip
            ? PvpCatalog.FindEquipment(h.Id) is { } e ? EquipmentNames.Localized(e) : h.Id
            : PvpCatalog.FindClass(h.Id) is { } c ? Loc.TOr("unit." + c.Class.Asset, c.Class.Name) : h.Id;

    private void Say(string text, bool error)
    {
        _message = text;
        _messageError = error;
        _messageTime = MessageSeconds;
        if (error)
            Context.Sounds.Play("menu_close");
    }

    // ── Boutons ──────────────────────────────────────────────────────────────────

    private void Activate(Target t)
    {
        switch (t.Kind)
        {
            case Hit.ArmyTab:
                if (ArmyFrozen || t.Index == _tab) break;
                _tab = t.Index;
                Profile.SelectedBuild = _tab;
                PvpProfileStore.Save();
                Context.Sounds.Play("menu_click");
                break;
            case Hit.CmdPrev:
            case Hit.CmdNext:
                if (!ArmyFrozen) StepCommander(t.Kind == Hit.CmdNext ? 1 : -1);
                break;
            case Hit.TabUnits:
                SetReserveTab(false);
                break;
            case Hit.TabEquip:
                SetReserveTab(true);
                break;
            case Hit.PagePrev:
                TurnPage(-1);
                break;
            case Hit.PageNext:
                TurnPage(1);
                break;
            case Hit.Friend:
                if (_mode != SearchMode.Idle || Locked) break;
                _friend = !_friend;
                Context.Sounds.Play("menu_click");
                break;
            case Hit.Training:
                OpenTraining();
                break;
            case Hit.TrainingPick:
                if (t.Index >= 0 && t.Index < _trainingKeys.Count)
                    StartTraining(_trainingKeys[t.Index]);
                break;
            case Hit.TrainingClose:
                CloseTraining();
                break;
            case Hit.TrainingTuto:
                StartTutorialMatch();
                break;
            case Hit.Launch:
                OnLaunch();
                break;
            case Hit.Cancel:
                CancelSearch();
                break;
            case Hit.Chests:
                OpenChests();
                break;
            case Hit.Ranking:
                OpenRanking();
                break;
            case Hit.Back:
                OnBack();
                break;
            case Hit.Help:
                StartTutorial();
                break;
        }
    }

    private void SetReserveTab(bool equipment)
    {
        if (_reserveEquip == equipment)
            return;
        _reserveEquip = equipment;
        if (_held.Active && !_held.FromBoard)
            CancelHeld();
        Context.Sounds.Play("menu_click");
    }

    private void TurnPage(int dir)
    {
        var i = _reserveEquip ? 1 : 0;
        var next = Math.Clamp(Page + dir, 0, PageCount - 1);   // depuis la page AFFICHÉE (déjà ramenée dans les bornes)
        if (next == Page)
            return;
        _page[i] = next;
        Context.Sounds.Play("menu_click");
    }

    private void StepCommander(int dir)
    {
        var owned = OwnedCommanders();
        if (owned.Count < 2)
            return;
        var i = Math.Max(0, owned.FindIndex(c => c.Id == Build.CommanderId));
        Build.CommanderId = owned[(i + dir + owned.Count) % owned.Count].Id;
        PvpProfileStore.Save();
        Context.Sounds.Play("menu_click");
    }

    private void OnBack()
    {
        if (Locked)
            return;   // combat imminent : on ne quitte plus
        if (_trainingOpen)
        {
            CloseTraining();
            return;
        }
        CancelHeld();
        if (_view == View.Chests)
        {
            CloseChests();
            return;
        }
        if (_view == View.Ranking)
        {
            CloseRanking();
            return;
        }
        Context.Sounds.Play("menu_close");
        _leaving = true;
        Context.Scenes.Change(new MainMenuScene(Context));
    }

    private bool _leaving;

    // ── Cibles ───────────────────────────────────────────────────────────────────

    /// <summary>Case d'indice <paramref name="index"/> (rangée × 8 + colonne du plateau) en repère de build :
    /// ligne -1 hors des deux lignes du joueur.</summary>
    private static (int Column, int Line) CellOf(int index)
    {
        var row = index / PvpBoardRenderer.Size;
        var col = index % PvpBoardRenderer.Size;
        var line = row == PvpBoardRenderer.Size - 1 ? PvpBuild.BackLine : row == PvpBoardRenderer.Size - 2 ? PvpBuild.FrontLine : -1;
        return (col, line);
    }

    private static int CellIndex(int column, int line) =>
        (line == PvpBuild.BackLine ? PvpBoardRenderer.Size - 1 : PvpBoardRenderer.Size - 2) * PvpBoardRenderer.Size + column;

    private void Add(Rectangle r, Hit kind, int index = 0, bool focusable = true)
    {
        _targets.Add(new Target(r, kind, index, focusable));
        if (!focusable)
            return;
        _focus.Add(r);
        _focusMap.Add(_targets.Count - 1);
    }

    private Target? HitAt(Point p)
    {
        var i = HitIndexAt(p);
        return i >= 0 ? _targets[i] : null;
    }

    /// <summary>Cible sous <paramref name="p"/> : la dernière ajoutée gagne (badges par-dessus les cases, zones de
    /// fond ajoutées en premier). À la manette, le focus.</summary>
    private int HitIndexAt(Point p)
    {
        if (Context.Input.UsingGamepad)
            return _focus.Index >= 0 && _focus.Index < _focusMap.Count ? _focusMap[_focus.Index] : -1;
        for (var i = _targets.Count - 1; i >= 0; i--)
            if (_targets[i].Rect.Contains(p))
                return i;
        return -1;
    }

    private void BuildTargets()
    {
        _targets.Clear();
        _focus.Clear();
        _focusMap.Clear();
        if (_tutorialOpen)
        {
            AddTutorialTargets();
            return;
        }
        if (_trainingOpen && _view == View.Build)
        {
            AddTrainingTargets();   // fenêtre des niveaux : seules ses cases répondent
            return;
        }
        AddTopBar();
        if (_view == View.Chests)
        {
            AddChestTargets();
            return;
        }
        if (_view == View.Ranking)
        {
            AddRankingTargets();
            return;
        }

        var L = _lay;
        Add(L.Res, Hit.ReserveArea, focusable: false);
        Add(L.Board, Hit.BoardArea, focusable: false);
        for (var i = 0; i < PvpProfile.BuildSlots; i++)
            Add(L.Tabs[i], Hit.ArmyTab, i);
        if (OwnedCommanders().Count > 1)
        {
            Add(L.CmdPrev, Hit.CmdPrev);
            Add(L.CmdNext, Hit.CmdNext);
        }

        // Cases : toutes celles affichées (dépôt hors des lignes = retrait) ; seules les deux lignes sont focalisables.
        for (var row = L.FirstRow; row < PvpBoardRenderer.Size; row++)
            for (var col = 0; col < PvpBoardRenderer.Size; col++)
                Add(CellRect(row, col), Hit.Cell, row * PvpBoardRenderer.Size + col, focusable: row >= PvpBoardRenderer.Size - 2);
        foreach (var slot in Build.Slots)
            if (slot.EquipmentId != null)
                Add(BadgeRect(slot.Column, slot.Line), Hit.Badge, CellIndex(slot.Column, slot.Line));

        if (_held.Active && _held.FromBoard)
            Add(ToReserveRect, Hit.ToReserve);   // l'en-tête devient « REMETTRE EN RÉSERVE »
        else
        {
            Add(L.TabUnits, Hit.TabUnits);
            Add(L.TabEquip, Hit.TabEquip);
            Add(L.PagePrev, Hit.PagePrev);   // pagination à droite de l'en-tête
            Add(L.PageNext, Hit.PageNext);
        }
        var first = Page * L.CardCount;
        for (var k = 0; k < L.CardCount && first + k < EntryCount; k++)
            Add(L.Cards[k], Hit.Card, first + k);

        Add(L.Friend, Hit.Friend);
        Add(L.Training, Hit.Training);
        if (_mode != SearchMode.Idle && !Locked)
            Add(L.Cancel, Hit.Cancel);
        Add(L.Launch, Hit.Launch);
    }

    private void AddTopBar()
    {
        if (CanOpenTutorial)
            Add(_lay.HelpBtn, Hit.Help);
        Add(_lay.RankingBtn, Hit.Ranking);
        Add(_lay.ChestsBtn, Hit.Chests);
        Add(_lay.BackBtn, Hit.Back);
    }

    /// <summary>Rectangle du DESSUS (64×64) d'une case du plateau à l'écran.</summary>
    private Rectangle CellRect(int row, int col) =>
        new(_lay.Board.X + col * PvpBoardRenderer.Tile, _lay.Board.Y + (row - _lay.FirstRow) * PvpBoardRenderer.Tile,
            PvpBoardRenderer.Tile, PvpBoardRenderer.Tile);

    /// <summary>Badge 32×32 d'équipement, en bas à droite de la case (cadre de 3 px compris dans la case).</summary>
    private Rectangle BadgeRect(int column, int line)
    {
        var cell = CellRect(line == PvpBuild.BackLine ? PvpBoardRenderer.Size - 1 : PvpBoardRenderer.Size - 2, column);
        return new Rectangle(cell.Right - 32 - 3, cell.Bottom - 32 - 3, 32, 32);
    }

    // ── Textures ─────────────────────────────────────────────────────────────────

    /// <summary>PNG sous <c>Assets/&lt;rel&gt;.png</c> (mis en cache ; null si absent).</summary>
    private Texture2D? Tex(string rel)
    {
        if (_textures.TryGetValue(rel, out var tex))
            return tex;
        tex = Textures.LoadPngOrNull(Context.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, $"Assets/{rel}.png"));
        _textures[rel] = tex;
        return tex;
    }

    private Texture2D? UnitBack(string asset) => Tex($"Units/{asset}_back") ?? Tex($"Units/{asset}");
    private Texture2D? UnitFront(string asset) => Tex($"Units/{asset}_front") ?? Tex($"Units/{asset}");
    private Texture2D? EquipIcon(Equipment e) => Tex($"Equipment/{e.Icon}");
}
