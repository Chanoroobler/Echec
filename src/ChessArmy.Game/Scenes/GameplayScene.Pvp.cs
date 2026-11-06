using System;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Map;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Combat MULTIJOUEUR (humain via Steam, ou bot de secours) : armées des builds, pendules (réserve + limite par
/// coup), égalité (proposition, position morte, destruction mutuelle), déconnexion avec délai de retour, gains de
/// points et de cote (revalidés par rejeu contre un bot), et le HUD qui va avec. Tout le reste du combat est
/// celui de la campagne : mêmes pions, mêmes traits, mêmes animations.
/// </summary>
public sealed partial class GameplayScene
{
    /// <summary>Silence de l'adversaire au-delà duquel on le considère déconnecté (secondes).</summary>
    private const float PvpDisconnectAfter = 4f;

    /// <summary>Marge accordée à un adversaire dont la pendule (vue d'ici) est tombée, avant de conclure à sa place.</summary>
    private const float PvpOpponentFlagGrace = 15f;

    private readonly PvpMatchInfo? _pvpInfo;
    private PvpClock _pvpClock = null!;
    private bool _pvpDesync;
    private bool _pvpRecorded;
    private bool _pvpValid = true;
    private PvpReward? _pvpReward;
    private bool _pvpDrawPending;
    private float _pvpDrawRefused;
    private float _pvpEnemyFlagTimer;
    private Rectangle _pvpDrawRect, _pvpAcceptRect, _pvpRefuseRect;

    private PvpSession? PvpNet => _pvpInfo?.Session;

    private bool PvpOpponentDisconnected => PvpNet is { } s && !_match.IsOver && s.Silence > PvpDisconnectAfter;

    /// <summary>
    /// Mise en place : map Multi tirée par la graine (échiquier d'herbe nu s'il n'y en a aucune), les deux armées
    /// sur leurs lignes, le contrôleur du camp adverse (réseau ou bot) et la pendule. Le combat démarre aussitôt.
    /// </summary>
    private void BeginPvpBattle()
    {
        var info = _pvpInfo!;
        _map = null;   // la map sert au terrain seulement : ses spawns/objets de campagne ne s'appliquent pas
        Columns = Rows = PvpSetup.Size;
        _boardIntro = _boardIntroTotal = 0f;
        ClearBoardObjects();
        System.Collections.Generic.List<Cell>? bushes = null;
        _battlefield = PvpSetup.PickMap(_maps, info.Seed) is { } map
            ? PvpSetup.BuildBattlefield(map, isHost: !info.Mirrored, out bushes)
            : Battlefield.CreateFlat(Columns, Rows);
        if (bushes != null)
            _bushCells.AddRange(bushes);
        DetectSnowMap();
        SetupBoardDecor();
        _battleLog.Clear();
        _combatSeed = info.Seed;
        _match = PvpSetup.CreateMatch(info.Mirrored, info.LocalStarts, info.Seed, _battlefield, _bushCells);
        ResetCombatState();
        PvpSetup.PlaceArmy(_match, info.LocalBuild, Faction.Player);
        PvpSetup.PlaceArmy(_match, info.OpponentBuild, Faction.Enemy);
        _enemyController = info.Session is { } session
            ? new RemoteBattleController(session)
            : new PvpBotController(info.Bot ?? PvpBotDef.Default, info.Seed + 1,
                // Copies de la partie pour les bots qui calculent : même graine, mêmes armées, même terrain, et le
                // journal des coups tenu par la scène (le bot joue toujours dans le repère de l'hôte).
                new PvpForker(info.Seed, info.LocalStarts, _battlefield, _bushCells, info.LocalBuild, info.OpponentBuild),
                _battleLog);
        var config = PvpConfig.Current;
        _pvpClock = new PvpClock(config.ReserveSeconds, config.MoveSeconds);
        _pvpClock.BeginTurn(_match.CurrentTurn);
        _cursor = new Cell(Columns / 2, Rows - 2);   // curseur manette sur les pions du joueur

        _run.StartBattle();
        _battleIntroTimer = 0;   // aucun panneau de placement à faire sortir
        _lastAiAction = null;
        _aiTimer = AiDelaySeconds;
        MarkLayoutDirty();
        Context.Sounds.Play("battle_start");
    }

    /// <summary>
    /// Après chaque coup résolu (local, adverse ou bot) : règles de fin de coup (Séisme, position morte), envoi
    /// du coup LOCAL à l'adversaire avec l'empreinte et la réserve, et nouvelle limite de temps pour le suivant.
    /// </summary>
    private void OnPvpActionExecuted(in BattleAction action, Faction actor)
    {
        ShowSeismeHits(PvpSetup.AfterAction(_match, actor));
        if (PvpNet is { } session && actor == Faction.Player)
            session.SendAction(action, _match.ComputeStateHash(_pvpInfo!.Mirrored), _pvpClock.Reserve(Faction.Player));
        _pvpClock.BeginTurn(_match.CurrentTurn);
        _pvpEnemyFlagTimer = 0f;
    }

    /// <summary>Coup adverse appliqué : on doit retrouver EXACTEMENT l'état annoncé, et on recale sa pendule.</summary>
    private void OnOpponentActionApplied()
    {
        if (_enemyController is not RemoteBattleController remote)
            return;
        if (remote.ExpectedHash != _match.ComputeStateHash(_pvpInfo!.Mirrored))
            _pvpDesync = true;
        if (remote.OpponentReserve >= 0f)
            _pvpClock.SetReserve(Faction.Enemy, remote.OpponentReserve);
    }

    /// <summary>
    /// Chaque frame de combat multi : réseau, déconnexion, réponses d'égalité, pendule, boutons du HUD ; puis,
    /// partie finie, le panneau de fin. Vrai = la frame est consommée (rien d'autre ne doit jouer).
    /// </summary>
    private bool UpdatePvp(float dt)
    {
        var session = PvpNet;
        session?.Update(dt);
        if (_pvpDrawRefused > 0f)
            _pvpDrawRefused -= dt;

        if (!_match.IsOver)
        {
            if (session != null)
            {
                if (session.OpponentLeft)
                    _match.Concede(Faction.Enemy, MatchEndReason.Resign);   // abandon de l'adversaire
                else if (session.Silence > PvpDisconnectAfter + PvpConfig.Current.ReconnectSeconds)
                    _match.Concede(Faction.Enemy, MatchEndReason.Disconnect);   // pas revenu à temps
                if (session.DrawAnswer is { } answer)
                {
                    session.DrawAnswer = null;
                    _pvpDrawPending = false;
                    if (answer) ApplyAgreedDraw();
                    else _pvpDrawRefused = 3f;
                }
            }
            if (!_match.IsOver && !PvpOpponentDisconnected && BattleSettled && _pvpClock.Tick(dt))
                OnClockFlag();
            if (_pvpClock.Flagged == Faction.Enemy && !_match.IsOver)
            {
                _pvpEnemyFlagTimer += dt;
                if (_pvpEnemyFlagTimer > PvpOpponentFlagGrace)
                    _match.Concede(Faction.Enemy, MatchEndReason.Timeout);   // l'adversaire ne s'est pas déclaré perdant : on conclut
            }
            if (_match.IsOver)
                return false;   // l'issue sera traitée par CheckBattleEnd, une fois le plateau posé
            return UpdatePvpButtons();
        }

        if (!_pvpRecorded)
            return false;   // on attend que le plateau soit posé (CheckBattleEnd → OnPvpBattleEnd)
        _sparks.Update(dt);
        if (_damagePopups.HasActive)
            _damagePopups.Update(dt, BuildLayout(), _sparks);
        UpdatePvpEndScreen(dt);   // fenêtre de fin (GameplayScene.PvpEnd.cs)
        return true;
    }

    /// <summary>Temps écoulé : le nôtre = défaite annoncée à l'adversaire ; celui d'un bot ne tombe jamais.</summary>
    private void OnClockFlag()
    {
        if (_pvpClock.Flagged == Faction.Player && _match.CurrentTurn == Faction.Player)
        {
            ClearSelection();
            ExecuteAction(BattleAction.Resign(Faction.Player), out _);
            Context.Sounds.Play("defeat");
        }
    }

    private void ApplyAgreedDraw()
    {
        if (_match.Apply(BattleAction.AgreeDraw) != MoveKind.Invalid)
            _battleLog.Add(BattleAction.AgreeDraw);
    }

    /// <summary>Boutons du HUD pendant la partie : proposer l'égalité, répondre à une proposition. Vrai si le clic
    /// (ou A) a été consommé.</summary>
    private bool UpdatePvpButtons()
    {
        var input = Context.Input;
        var click = input.WasLeftClicked;
        var mouse = input.MousePosition;

        if (PvpNet is { DrawOffered: true } session)
        {
            // Proposition reçue : souris sur les boutons, ou A = accepter / B = refuser à la manette.
            var accept = (click && _pvpAcceptRect.Contains(mouse)) || (input.UsingGamepad && input.WasConfirmPressed);
            var refuse = (click && _pvpRefuseRect.Contains(mouse)) || (input.UsingGamepad && input.WasCancelPressed);
            if (accept || refuse)
            {
                session.SendDrawReply(accept);
                if (accept) ApplyAgreedDraw();
                Context.Sounds.Play("menu_click");
                return true;
            }
        }

        // Y (manette) ou clic sur le bouton : proposer l'égalité.
        if (!_pvpDrawPending && _pvpDrawRefused <= 0f
            && ((click && _pvpDrawRect.Contains(mouse)) || input.WasQuaternaryPressed))
        {
            Context.Sounds.Play("menu_click");
            if (PvpNet is { } net)
            {
                net.SendDrawOffer();
                _pvpDrawPending = true;
            }
            else if (PvpBotController.AcceptsDraw(_match))
                ApplyAgreedDraw();
            else
                _pvpDrawRefused = 3f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Partie finie (plateau posé) : issue, pions tués, revalidation par rejeu contre un bot, gains de points et
    /// de cote enregistrés UNE fois dans le profil multi.
    /// </summary>
    private void OnPvpBattleEnd()
    {
        if (_pvpRecorded)
            return;
        _pvpRecorded = true;
        var info = _pvpInfo!;
        var outcome = _match.IsDraw ? PvpOutcome.Draw
            : _match.Winner == Faction.Player ? PvpOutcome.Win : PvpOutcome.Loss;

        // Contre un bot, personne ne vérifie de l'autre côté : on rejoue la partie depuis sa graine et ses coups.
        // Une valeur modifiée en mémoire pendant le combat donne un autre état final → aucun gain.
        if (info.IsBot)
        {
            var replay = PvpSetup.Replay(info.Seed, info.LocalStarts, _battlefield, _bushCells,
                info.LocalBuild, info.OpponentBuild, _battleLog);
            _pvpValid = replay.ComputeStateHash() == _match.ComputeStateHash();
        }
        else
            _pvpValid = !_pvpDesync;

        if (_pvpValid && !PvpProfile.Tampered)
        {
            _pvpReward = PvpProfileStore.Current.RecordResult(outcome, KillsByPlayer(), info.OpponentRating, info.Ranked,
                info.OpponentName, info.IsBot, DateTime.UtcNow);
            PvpProfileStore.Save();
            if (info.Ranked)
                PvpLeaderboardService.Upload(PvpProfileStore.Current, info.LocalBuild);   // classement Steam : cote, bilan, formation
        }
        BeginPvpEndScreen(outcome);
        Context.Sounds.Play(outcome == PvpOutcome.Win ? "victory" : outcome == PvpOutcome.Loss ? "defeat" : "reward");
    }

    private int KillsByPlayer() => _match.DeathLog.Count(d => d.Unit.Faction == Faction.Enemy);

    /// <summary>Sortie de la scène : une partie quittée avant la fin compte comme une défaite (anti rage-quit).</summary>
    private void UnloadPvp()
    {
        if (_pvpInfo == null)
            return;
        if (!_pvpRecorded && _match != null && !_match.IsOver && !PvpProfile.Tampered)
        {
            _pvpRecorded = true;
            PvpProfileStore.Current.RecordResult(PvpOutcome.Loss, KillsByPlayer(), _pvpInfo.OpponentRating, _pvpInfo.Ranked,
                _pvpInfo.OpponentName, _pvpInfo.IsBot, DateTime.UtcNow);
            PvpProfileStore.Save();
            if (_pvpInfo.Ranked)
                PvpLeaderboardService.Upload(PvpProfileStore.Current, _pvpInfo.LocalBuild);
        }
        PvpNet?.Dispose();
    }

    // ── HUD ──────────────────────────────────────────────────────────────────────

    /// <summary>HUD multi : pendules et tour courant (haut), égalité, déconnexion, désynchronisation, panneau de fin.</summary>
    private void DrawPvpHud(SpriteBatch sb, Viewport viewport)
    {
        var info = _pvpInfo!;
        sb.Begin(samplerState: SamplerState.PointClamp);
        if (!_match.IsOver || !_pvpRecorded)
            DrawPvpTopBar(sb, viewport, info);
        else
            DrawPvpEndPanel(sb, viewport, info);
        sb.End();
    }

    /// <summary>
    /// HUD du combat multi, RIEN devant le plateau (centré, entier) : la colonne de GAUCHE est la nôtre (en bas,
    /// près de nos pièces), celle de DROITE l'adversaire (en haut, près des siennes) avec en dessous les alertes,
    /// la proposition d'égalité reçue et, en bas, le bouton pour en proposer une. Chaque panneau donne le nom, la
    /// réserve de temps et, pour le camp qui a la main, le temps restant pour ce coup.
    /// </summary>
    private void DrawPvpTopBar(SpriteBatch sb, Viewport viewport, PvpMatchInfo info)
    {
        var font = Context.Font;
        // Colonnes fixes de part et d'autre du plateau AU CADRAGE par défaut (elles ne bougent pas avec la caméra).
        var boardW = Columns * ChessArmy.Engine.Rendering.GridLayout.DefaultTileSize * FitZoom();
        var boardLeft = (viewport.Width - boardW) / 2;
        var boardRight = boardLeft + boardW;
        const int pad = 8;
        var leftCol = new Rectangle(pad, pad, Math.Max(40, boardLeft - 2 * pad), viewport.Height - 2 * pad);
        var rightCol = new Rectangle(boardRight + pad, pad, Math.Max(40, viewport.Width - boardRight - 2 * pad), viewport.Height - 2 * pad);
        var mine = _match.CurrentTurn == Faction.Player;

        // Notre panneau : en bas à gauche ; celui de l'adversaire : en haut à droite.
        var mePanel = SidePanel(leftCol, mine, fromBottom: true);
        DrawSide(sb, mePanel, Loc.T("pvp.you"), _pvpClock.Reserve(Faction.Player), mine);
        var themPanel = SidePanel(rightCol, !mine, fromBottom: false);
        DrawSide(sb, themPanel, info.DisplayName, _pvpClock.Reserve(Faction.Enemy), !mine);

        var y = themPanel.Bottom + 8;
        if (PvpOpponentDisconnected && PvpNet is { } s)
        {
            var left = PvpDisconnectAfter + PvpConfig.Current.ReconnectSeconds - s.Silence;
            y = DrawSideLine(sb, Loc.T("pvp.disconnected", PvpUi.Clock(left)), rightCol, y);
        }
        if (_pvpDesync)
            y = DrawSideLine(sb, Loc.T("pvp.desync"), rightCol, y);

        if (_match.IsOver)
            return;

        // Proposition d'égalité reçue : dans la colonne de l'adversaire, boutons l'un sous l'autre.
        if (PvpNet is { DrawOffered: true })
        {
            var gp = Context.Input.UsingGamepad;
            var title = PvpArt.Fit(font, Loc.T("pvp.draw_offered", info.DisplayName), rightCol.Width - 12, 1);
            var panel = new Rectangle(rightCol.X, y, rightCol.Width, 92);
            Context.Style.DrawPanel(sb, panel);
            font.DrawCentered(sb, title, new Rectangle(panel.X, panel.Y + 10, panel.Width, 7), 1, Palette.Yellow2);
            _pvpAcceptRect = new Rectangle(panel.X + 10, panel.Y + 28, panel.Width - 20, 24);
            _pvpRefuseRect = new Rectangle(panel.X + 10, _pvpAcceptRect.Bottom + 6, panel.Width - 20, 24);
            PvpUi.Button(Context, sb, _pvpAcceptRect, (gp ? "(A) " : "") + Loc.T("pvp.accept"), focused: false);
            PvpUi.Button(Context, sb, _pvpRefuseRect, (gp ? "(B) " : "") + Loc.T("pvp.refuse"), focused: false);
        }

        // Bouton d'égalité, en bas de la colonne de droite (l'indice suit le périphérique : Y à la manette).
        var label = _pvpDrawPending ? Loc.T("pvp.draw_sent")
            : _pvpDrawRefused > 0f ? Loc.T("pvp.draw_refused")
            : (Context.Input.UsingGamepad ? "(Y) " : "") + Loc.T("pvp.offer_draw");
        label = PvpArt.Fit(font, label, rightCol.Width - 16, 1);
        _pvpDrawRect = new Rectangle(rightCol.X, rightCol.Bottom - 24, rightCol.Width, 24);
        PvpUi.Button(Context, sb, _pvpDrawRect, label, focused: false, enabled: !_pvpDrawPending && _pvpDrawRefused <= 0f);
    }

    /// <summary>Panneau d'un camp dans sa colonne (plus haut s'il a la main : il affiche aussi le temps du coup).</summary>
    private static Rectangle SidePanel(Rectangle column, bool active, bool fromBottom)
    {
        var height = active ? 92 : 52;
        var width = Math.Min(column.Width, 220);
        var x = column.X + (column.Width - width) / 2;
        return new Rectangle(x, fromBottom ? column.Bottom - height : column.Y, width, height);
    }

    private void DrawSide(SpriteBatch sb, Rectangle panel, string who, float reserve, bool active)
    {
        var font = Context.Font;
        Context.Style.DrawPanel(sb, panel);
        var nameColor = active ? Palette.Yellow2 : Palette.Blue1;
        font.DrawCentered(sb, PvpArt.Fit(font, who, panel.Width - 12, 1), new Rectangle(panel.X, panel.Y + 9, panel.Width, 7), 1, nameColor);
        // Réserve de temps de la partie.
        var reserveColor = reserve < 60f ? Palette.Purple5 : active ? Palette.White : Palette.Blue1;
        font.DrawCentered(sb, PvpUi.Clock(reserve), new Rectangle(panel.X, panel.Y + 24, panel.Width, 14), 2, reserveColor);
        if (!active)
            return;
        // Le camp qui a la main : son tour et le temps qu'il lui reste pour ce coup.
        font.DrawCentered(sb, PvpArt.Fit(font, Loc.T("pvp.this_move"), panel.Width - 12, 1),
            new Rectangle(panel.X, panel.Y + 50, panel.Width, 7), 1, Palette.Blue1);
        var urgent = _pvpClock.MoveLeft < 10f;
        font.DrawCentered(sb, PvpUi.Clock(_pvpClock.MoveLeft), new Rectangle(panel.X, panel.Y + 64, panel.Width, 14), 2,
            urgent ? Palette.Purple5 : Palette.Yellow2);
    }

    private int DrawSideLine(SpriteBatch sb, string text, Rectangle column, int y)
    {
        Context.Font.DrawCentered(sb, PvpArt.Fit(Context.Font, text, column.Width, 1), new Rectangle(column.X, y, column.Width, 7), 1, Palette.Purple5);
        return y + 14;
    }
}
