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
            ? PvpSetup.BuildBattlefield(map, isHost: !info.Mirrored, out bushes, _catalog)
            : Battlefield.CreateFlat(Columns, Rows);
        if (bushes != null)
            _bushCells.AddRange(bushes);
        DetectSnowMap();
        SetupBoardDecor();
        _battleLog.Clear();
        _combatSeed = info.Seed;
        _match = PvpSetup.CreateMatch(info.Mirrored, info.LocalStarts, info.Seed, _battlefield, _bushCells);
        ResetCombatState();
        _pendingCollapses.Clear();
        _collapseVictims.Clear();
        SetupPvpRanks(info);
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
        _pvpClock = new PvpClock(info.ReserveSeconds ?? config.ReserveSeconds, config.MoveSeconds);
        _pvpClock.BeginTurn(_match.CurrentTurn);
        _pvpTurnStartReserve[0] = _pvpTurnStartReserve[1] = _pvpClock.Reserve(Faction.Player);
        _collapseState[0] = _collapseState[1] = 0;
        _collapseBanner = -1f;
        SyncBotCollapse();
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
        // Pendule basse : une tuile tombe derrière le camp qui vient de jouer (journalisée pour le rejeu ; jamais
        // envoyée : l'adversaire la refait de son côté). On juge sur la réserve au DÉBUT du tour (celle annoncée à
        // la fin de son coup précédent, connue des deux clients) : seul un tour COMMENCÉ sous le seuil fait tomber
        // une tuile, et c'est ce tour-là qu'elle tremble (cf. CollapseWarningCell). Le tour où l'on franchit le
        // seuil ne fait rien tomber.
        if (PvpSetup.CollapseAfterTurn(_match, actor, _pvpTurnStartReserve[(int)actor]) is { } fallen)
        {
            _battleLog.Add(BattleAction.Collapse(actor));
            _pendingCollapses.Add((fallen, _match.LastCollapseVictim));
        }
        if (_match.CurrentTurn != actor)
            _pvpTurnStartReserve[(int)actor] = ActorReserve(actor);   // la main passe : réserve de début de son prochain tour
        SyncBotCollapse();
        if (PvpNet is { } session && actor == Faction.Player)
            session.SendAction(action, _match.ComputeStateHash(_pvpInfo!.Mirrored), _pvpClock.Reserve(Faction.Player));
        _pvpClock.BeginTurn(_match.CurrentTurn);
        _pvpEnemyFlagTimer = 0f;
    }

    /// <summary>
    /// Réserve du camp qui vient de jouer, telle que les DEUX clients la connaissent : la nôtre tronquée à la
    /// milliseconde puis relue EXACTEMENT comme l'adversaire la décode (cf. <see cref="PvpSession.SendAction"/>,
    /// ms entières ÷ 1000f), celle de l'adversaire distant telle qu'annoncée avec son coup. Sinon (bot), la
    /// pendule locale.
    /// </summary>
    private float ActorReserve(Faction actor)
    {
        if (actor == Faction.Player)
            return PvpNet != null ? (int)(_pvpClock.Reserve(Faction.Player) * 1000) / 1000f : _pvpClock.Reserve(Faction.Player);
        return _enemyController is RemoteBattleController { OpponentReserve: >= 0f } remote
            ? remote.OpponentReserve
            : _pvpClock.Reserve(Faction.Enemy);
    }

    /// <summary>Tuiles effondrées par la pendule basse, pas encore animées (on attend que le coup ait fini de
    /// se jouer), avec le pion emporté (déjà retiré du moteur, dessiné sur sa tuile jusqu'à la fin de la chute).</summary>
    private readonly System.Collections.Generic.List<(Cell Cell, ChessArmy.Core.Battle.Unit? Victim)> _pendingCollapses = new();
    private readonly System.Collections.Generic.List<(Cell Cell, ChessArmy.Core.Battle.Unit Victim)> _collapseVictims = new();

    /// <summary>Combat posé (cf. <see cref="FxSettled"/>) : les tuiles en attente commencent leur chute.</summary>
    private void StartPendingCollapses()
    {
        if (_pendingCollapses.Count == 0 || !FxSettled)
            return;
        foreach (var (cell, victim) in _pendingCollapses)
        {
            _chuteFall.Start(cell);
            if (victim != null)
                _collapseVictims.Add((cell, victim));
        }
        _pendingCollapses.Clear();
        Context.Sounds.Play("chute");
    }

    /// <summary>Pions emportés par une tuile effondrée : debout tant qu'elle n'est pas partie, puis ils tombent avec.</summary>
    private void DrawCollapseVictims(SpriteBatch sb, ChessArmy.Engine.Rendering.GridLayout layout)
    {
        foreach (var (cell, victim) in _pendingCollapses)
            if (victim != null)
                DrawUnit(sb, layout, cell, victim);
        foreach (var (cell, victim) in _collapseVictims)
            DrawUnit(sb, layout, cell, victim);
    }

    // Avertissement de pendule basse : la tuile qui va tomber secoue (cf. ChuteTrembleY) et se couvre d'un aplat rouge
    // dont l'opacité pulse, pendant le tour du camp concerné puis jusqu'à sa chute (après le coup).
    private const float CollapseShakeFreq = 40f;     // rad/s : ~6 allers-retours par seconde
    private const int CollapseShakeAmp = 2;          // px canvas (créneau ±2)
    private const float CollapsePulseFreq = 6f;      // rad/s : ~1 pulsation par seconde
    private const float CollapseAlphaMin = 0.25f, CollapseAlphaMax = 0.6f;

    /// <summary>Réserve de chaque camp au DÉBUT de son tour (= annoncée à la fin de son coup précédent) : c'est elle
    /// qui décide de la pendule basse, sur les deux clients.</summary>
    private readonly float[] _pvpTurnStartReserve = new float[2];

    /// <summary>Case qui tombera à la fin du tour en cours (tour commencé sous le seuil de pendule basse), ou null.</summary>
    private Cell? CollapseWarningCell()
    {
        if (_match.IsOver)
            return null;
        var side = _match.CurrentTurn;
        return _pvpTurnStartReserve[(int)side] < PvpConfig.Current.CollapseBelowSeconds ? _match.NextCollapse(side) : null;
    }

    /// <summary>Vrai si le tour de <paramref name="side"/> a commencé sous le seuil de pendule basse.</summary>
    private bool CollapseArmed(Faction side) => _pvpTurnStartReserve[(int)side] < PvpConfig.Current.CollapseBelowSeconds;

    /// <summary>Le bot ne voit pas la pendule : on lui dit si sa case condamnée tombera après son coup. Annonce aussi
    /// (bandeau) le premier tour de chaque camp commencé sous le seuil.</summary>
    private void SyncBotCollapse()
    {
        if (_enemyController is PvpBotController bot)
            bot.CollapseArmed = CollapseArmed(Faction.Enemy);
        for (var s = 0; s < 2; s++)
            if (_collapseState[s] == 0 && CollapseArmed((Faction)s))
                _collapseState[s] = 1;   // annoncé dès que le coup en cours a fini de se jouer (cf. UpdateCollapseBanner)
    }

    // Bandeau « le sol s'effondre » : au centre de l'écran, puis il rétrécit et glisse se ranger dans le panneau du camp
    // concerné, sous sa pendule, où la ligne reste. État par camp : 0 rien, 1 à annoncer, 2 annoncé (ligne affichée).
    private readonly byte[] _collapseState = new byte[2];
    private float _collapseBanner = -1f;              // temps écoulé du bandeau en cours (-1 : aucun)
    private Faction _collapseBannerSide;
    private const float CollapseBannerIn = 0.15f;     // apparition
    private const float CollapseBannerHold = 1.8f;    // début du glissement vers le panneau
    private const float CollapseBannerSlide = 0.6f;   // durée du glissement
    private const int CollapseTagH = 14;              // hauteur ajoutée au panneau pour la ligne
    private Language _collapseTextLang = (Language)(-1);
    private string _collapseBannerText = "", _collapseTagText = "";

    private bool CollapseTagShown(Faction side) => _collapseState[(int)side] == 2;

    private static Rectangle CollapseTagRect(Rectangle panel, bool active) =>
        new(panel.X, panel.Y + (active ? 96 : 56), panel.Width, 7);

    private void RefreshCollapseTexts()
    {
        if (_collapseTextLang == Loc.Current)
            return;
        _collapseTextLang = Loc.Current;
        _collapseBannerText = Loc.T("pvp.collapse.banner", (int)(PvpConfig.Current.CollapseBelowSeconds / 60f));
        _collapseTagText = Loc.T("pvp.collapse.tag");
    }

    private void UpdateCollapseBanner(float dt)
    {
        if (_collapseBanner >= 0f)
        {
            _collapseBanner += dt;
            if (_collapseBanner >= CollapseBannerHold + CollapseBannerSlide)
                _collapseBanner = -1f;
            return;
        }
        if (!BattleSettled)
            return;   // le coup qui vient d'être joué finit d'abord son animation
        for (var s = 0; s < 2; s++)
            if (_collapseState[s] == 1)
            {
                _collapseState[s] = 2;
                _collapseBannerSide = (Faction)s;
                _collapseBanner = 0f;
                return;
            }
    }

    private void DrawCollapseBanner(SpriteBatch sb, Viewport viewport, Rectangle target)
    {
        RefreshCollapseTexts();
        var font = Context.Font;
        var scale = font.Measure(_collapseBannerText, 2) + 32 <= viewport.Width - 16 ? 2 : 1;
        var w = font.Measure(_collapseBannerText, scale) + 32;
        var h = font.LineHeight(scale) + 20;
        var center = new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);
        var t = _collapseBanner;
        var rect = center;
        var alpha = MathHelper.Clamp(t / CollapseBannerIn, 0f, 1f);
        if (t > CollapseBannerHold)
        {
            var p = MathHelper.Clamp((t - CollapseBannerHold) / CollapseBannerSlide, 0f, 1f);
            p = p * p * (3f - 2f * p);
            rect = new Rectangle((int)MathHelper.Lerp(center.X, target.X, p), (int)MathHelper.Lerp(center.Y, target.Y, p),
                (int)MathHelper.Lerp(center.Width, target.Width, p), (int)MathHelper.Lerp(center.Height, target.Height, p));
            if (font.Measure(_collapseBannerText, scale) > rect.Width - 8 || font.LineHeight(scale) > rect.Height)
                scale = 1;   // il rétrécit en entrant dans le panneau : texte à l'échelle entière inférieure
            alpha = 1f - MathHelper.Clamp((p - 0.6f) / 0.4f, 0f, 1f);   // laisse place à la ligne du panneau
        }
        DrawRect(sb, rect, Palette.Black1 * (0.9f * alpha));
        font.DrawCentered(sb, PvpArt.Fit(font, _collapseBannerText, Math.Max(8, rect.Width - 8), scale), rect, scale,
            Palette.Purple5 * alpha);
    }

    /// <summary>Vrai si la tuile de <paramref name="cell"/> va tomber : annoncée pour ce tour, ou déjà effondrée côté
    /// moteur et en attente de son animation (le coup finit de se jouer).</summary>
    private bool IsCollapseDoomed(Cell cell)
    {
        if (_chuteFall.IsFalling(cell))
            return false;
        foreach (var (c, _) in _pendingCollapses)
            if (c == cell)
                return true;
        return CollapseWarningCell() == cell;
    }

    /// <summary>Aplat rouge pulsé sur les tuiles qui vont tomber (sous les pions, il secoue avec la tuile).</summary>
    private void DrawCollapseWarnings(SpriteBatch sb, ChessArmy.Engine.Rendering.GridLayout layout)
    {
        var pulse = 0.5f + 0.5f * MathF.Sin(_time * CollapsePulseFreq);
        var color = Palette.Purple5 * MathHelper.Lerp(CollapseAlphaMin, CollapseAlphaMax, pulse);
        foreach (var (c, _) in _pendingCollapses)
            DrawCollapseWarningAt(sb, layout, c, color);
        if (CollapseWarningCell() is { } cell)
            DrawCollapseWarningAt(sb, layout, cell, color);
    }

    private void DrawCollapseWarningAt(SpriteBatch sb, ChessArmy.Engine.Rendering.GridLayout layout, Cell cell, Color color)
    {
        if (!IsCollapseDoomed(cell))
            return;
        var (introY, introA) = BoardIntroAnim(cell, layout);
        var top = layout.CellToScreen(cell.Column, cell.Row);
        var size = layout.TileSize;
        DrawRect(sb, new Rectangle((int)top.X, (int)top.Y + introY + ChuteTrembleY(cell), size, size), color * introA);
    }

    /// <summary>Chute finie : le pion emporté n'est plus dessiné.</summary>
    private void OnCollapseLanded(Cell cell)
    {
        for (var i = _collapseVictims.Count - 1; i >= 0; i--)
            if (_collapseVictims[i].Cell == cell)
                _collapseVictims.RemoveAt(i);
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
        UpdateCollapseBanner(dt);
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

        if (_pvpValid && !PvpProfile.Tampered && !info.Test)   // partie de test : rien d'enregistré
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
        if (!_pvpRecorded && _match != null && !_match.IsOver && !PvpProfile.Tampered && !_pvpInfo.Test)
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
        var mePanel = SidePanel(leftCol, mine, fromBottom: true, CollapseTagShown(Faction.Player));
        DrawSide(sb, mePanel, Loc.T("pvp.you"), _pvpMyRank, _pvpMyRankName, _pvpClock.Reserve(Faction.Player), mine, Faction.Player);
        var themPanel = SidePanel(rightCol, !mine, fromBottom: false, CollapseTagShown(Faction.Enemy));
        DrawSide(sb, themPanel, info.DisplayName, _pvpTheirRank, _pvpTheirRankName, _pvpClock.Reserve(Faction.Enemy), !mine, Faction.Enemy);
        if (_collapseBanner >= 0f)
            DrawCollapseBanner(sb, viewport, CollapseTagRect(_collapseBannerSide == Faction.Player ? mePanel : themPanel,
                _collapseBannerSide == Faction.Player ? mine : !mine));

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
    private static Rectangle SidePanel(Rectangle column, bool active, bool fromBottom, bool collapseTag)
    {
        var height = (active ? 104 : 64) + (collapseTag ? CollapseTagH : 0);
        var width = Math.Min(column.Width, 220);
        var x = column.X + (column.Width - width) / 2;
        return new Rectangle(x, fromBottom ? column.Bottom - height : column.Y, width, height);
    }

    // Rangs affichés à côté des pseudos (cote figée au début de la partie) : calculés une fois, pas à chaque frame.
    private PvpRank _pvpMyRank, _pvpTheirRank;
    private string _pvpMyRankName = "", _pvpTheirRankName = "";

    private void SetupPvpRanks(PvpMatchInfo info)
    {
        _pvpMyRank = PvpRanks.FromRating(info.LocalRating);
        _pvpTheirRank = PvpRanks.FromRating(info.OpponentRating);
        _pvpMyRankName = PvpUi.RankName(_pvpMyRank);
        _pvpTheirRankName = PvpUi.RankName(_pvpTheirRank);
    }

    private void DrawSide(SpriteBatch sb, Rectangle panel, string who, PvpRank rank, string rankName, float reserve, bool active,
        Faction side)
    {
        var font = Context.Font;
        Context.Style.DrawPanel(sb, panel);
        if (CollapseTagShown(side))
        {
            // Pendule basse annoncée : la ligne rouge reste sous la pendule (le bandeau central vient s'y ranger).
            RefreshCollapseTexts();
            var tag = CollapseTagRect(panel, active);
            var tagAlpha = _collapseBanner >= 0f && _collapseBannerSide == side
                ? MathHelper.Clamp((_collapseBanner - CollapseBannerHold - CollapseBannerSlide * 0.6f) / (CollapseBannerSlide * 0.4f), 0f, 1f)
                : 1f;
            font.DrawCentered(sb, PvpArt.Fit(font, _collapseTagText, panel.Width - 12, 1), tag, 1, Palette.Purple5 * tagAlpha);
        }

        // En-tête : blason du rang (20×24), pseudo et nom du rang à sa droite, le tout centré dans le panneau.
        const int IconW = PvpRankIcon.W * 2, IconH = PvpRankIcon.H * 2, Gap = 6;
        var room = panel.Width - 16 - IconW - Gap;
        who = PvpArt.Fit(font, who, room, 1);
        rankName = PvpArt.Fit(font, rankName, room, 1);
        var textW = Math.Max(font.Measure(who, 1), font.Measure(rankName, 1));
        var x = panel.X + (panel.Width - (IconW + Gap + textW)) / 2;
        var top = panel.Y + 8;
        PvpEndRankIcon.Draw(sb, rank.Id, new Point(x, top), 2);
        var tx = x + IconW + Gap;
        font.Draw(sb, who, new Vector2(tx, top + 3), 1, active ? Palette.Yellow2 : Palette.Blue1);
        font.Draw(sb, rankName, new Vector2(tx, top + IconH - 3 - font.LineHeight(1)), 1, PvpRankIcon.LightColor(rank.Id));

        // Réserve de temps de la partie.
        var reserveColor = reserve < 60f ? Palette.Purple5 : active ? Palette.White : Palette.Blue1;
        font.DrawCentered(sb, PvpUi.Clock(reserve), new Rectangle(panel.X, panel.Y + 38, panel.Width, 14), 2, reserveColor);
        if (!active)
            return;
        // Le camp qui a la main : son tour et le temps qu'il lui reste pour ce coup.
        font.DrawCentered(sb, PvpArt.Fit(font, Loc.T("pvp.this_move"), panel.Width - 12, 1),
            new Rectangle(panel.X, panel.Y + 62, panel.Width, 7), 1, Palette.Blue1);
        var urgent = _pvpClock.MoveLeft < 10f;
        font.DrawCentered(sb, PvpUi.Clock(_pvpClock.MoveLeft), new Rectangle(panel.X, panel.Y + 76, panel.Width, 14), 2,
            urgent ? Palette.Purple5 : Palette.Yellow2);
    }

    private int DrawSideLine(SpriteBatch sb, string text, Rectangle column, int y)
    {
        Context.Font.DrawCentered(sb, PvpArt.Fit(Context.Font, text, column.Width, 1), new Rectangle(column.X, y, column.Width, 7), 1, Palette.Purple5);
        return y + 14;
    }
}
