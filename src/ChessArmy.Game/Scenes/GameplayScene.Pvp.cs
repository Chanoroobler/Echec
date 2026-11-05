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
    private Rectangle _pvpMenuRect, _pvpDrawRect, _pvpAcceptRect, _pvpRefuseRect;

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
            : new PvpBotController(info.Bot ?? new PvpBotDef(0.75, false, Array.Empty<PvpBuild>()), info.Seed + 1);
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
                    _match.Concede(Faction.Enemy);   // abandon de l'adversaire
                else if (session.Silence > PvpDisconnectAfter + PvpConfig.Current.ReconnectSeconds)
                    _match.Concede(Faction.Enemy);   // pas revenu à temps
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
                    _match.Concede(Faction.Enemy);   // l'adversaire ne s'est pas déclaré perdant : on conclut
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
        if (Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter)
            || (Context.Input.WasLeftClicked && _pvpMenuRect.Contains(Context.Input.MousePosition)))
        {
            Context.Sounds.Play("menu_click");
            Context.Scenes.Change(new PvpLobbyScene(Context));   // Unload libère la session (on quitte le lobby)
        }
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
            _pvpReward = PvpProfileStore.Current.RecordResult(outcome, KillsByPlayer(), info.OpponentRating, info.Ranked);
            PvpProfileStore.Save();
        }
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
            PvpProfileStore.Current.RecordResult(PvpOutcome.Loss, KillsByPlayer(), _pvpInfo.OpponentRating, _pvpInfo.Ranked);
            PvpProfileStore.Save();
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

    private void DrawPvpTopBar(SpriteBatch sb, Viewport viewport, PvpMatchInfo info)
    {
        var font = Context.Font;
        var mine = _match.CurrentTurn == Faction.Player;
        var turn = mine ? Loc.T("pvp.your_turn") : Loc.T("pvp.their_turn", info.DisplayName);
        var center = $"{turn}  {PvpUi.Clock(_pvpClock.MoveLeft)}";
        var w = font.Measure(center, 2) + 40;
        var box = new Rectangle((viewport.Width - w) / 2, 10, w, 30);
        Context.Style.DrawPanel(sb, box);
        var urgent = _pvpClock.MoveLeft < 10f;
        font.DrawCentered(sb, center, box, 2, urgent ? Palette.Purple5 : mine ? Palette.Yellow2 : Palette.Blue1);

        // Réserves : la nôtre à gauche, celle de l'adversaire à droite.
        DrawReserve(sb, new Rectangle(box.X - 150, 12, 140, 26), Loc.T("pvp.you"), _pvpClock.Reserve(Faction.Player), mine);
        DrawReserve(sb, new Rectangle(box.Right + 10, 12, 140, 26), info.DisplayName, _pvpClock.Reserve(Faction.Enemy), !mine);

        var y = box.Bottom + 6;
        if (PvpOpponentDisconnected && PvpNet is { } s)
        {
            var left = PvpDisconnectAfter + PvpConfig.Current.ReconnectSeconds - s.Silence;
            font.DrawCentered(sb, Loc.T("pvp.disconnected", PvpUi.Clock(left)), new Rectangle(0, y, viewport.Width, 7), 1, Palette.Purple5);
            y += 14;
        }
        if (_pvpDesync)
        {
            font.DrawCentered(sb, Loc.T("pvp.desync"), new Rectangle(0, y, viewport.Width, 7), 1, Palette.Purple5);
            y += 14;
        }

        if (_match.IsOver)
            return;
        // Bouton d'égalité (l'indice suit le périphérique : Y à la manette).
        var label = _pvpDrawPending ? Loc.T("pvp.draw_sent")
            : _pvpDrawRefused > 0f ? Loc.T("pvp.draw_refused")
            : (Context.Input.UsingGamepad ? "(Y) " : "") + Loc.T("pvp.offer_draw");
        var bw = font.Measure(label, 1) + 24;
        _pvpDrawRect = new Rectangle(viewport.Width - bw - 10, viewport.Height - 34, bw, 24);
        PvpUi.Button(Context, sb, _pvpDrawRect, label, focused: false, enabled: !_pvpDrawPending && _pvpDrawRefused <= 0f);

        if (PvpNet is { DrawOffered: true })
        {
            var gp = Context.Input.UsingGamepad;
            var title = Loc.T("pvp.draw_offered", info.DisplayName);
            var accept = (gp ? "(A) " : "") + Loc.T("pvp.accept");
            var refuse = (gp ? "(B) " : "") + Loc.T("pvp.refuse");
            var pw = Math.Max(font.Measure(title, 1) + 40, 300);
            var panel = new Rectangle((viewport.Width - pw) / 2, viewport.Height / 2 - 40, pw, 80);
            Context.Style.DrawPanel(sb, panel);
            font.DrawCentered(sb, title, new Rectangle(panel.X, panel.Y + 12, panel.Width, 7), 1, Palette.Yellow2);
            var half = (pw - 48) / 2;
            _pvpAcceptRect = new Rectangle(panel.X + 16, panel.Bottom - 40, half, 26);
            _pvpRefuseRect = new Rectangle(_pvpAcceptRect.Right + 16, panel.Bottom - 40, half, 26);
            PvpUi.Button(Context, sb, _pvpAcceptRect, accept, focused: false);
            PvpUi.Button(Context, sb, _pvpRefuseRect, refuse, focused: false);
        }
    }

    private void DrawReserve(SpriteBatch sb, Rectangle box, string who, float seconds, bool active)
    {
        Context.Style.DrawPanel(sb, box);
        var color = seconds < 60f ? Palette.Purple5 : active ? Palette.Yellow2 : Palette.Blue1;
        Context.Font.DrawCentered(sb, $"{who} {PvpUi.Clock(seconds)}", box, 1, color);
    }

    private void DrawPvpEndPanel(SpriteBatch sb, Viewport viewport, PvpMatchInfo info)
    {
        DrawDim(sb, viewport);
        var font = Context.Font;
        string title;
        Color color;
        if (_match.IsDraw)
        {
            title = Loc.T("pvp.draw");
            color = Palette.Blue1;
        }
        else if (_match.Winner == Faction.Player)
        {
            title = PvpNet is { OpponentLeft: true } ? Loc.T("pvp.opponent_left") : Loc.T("pvp.victory");
            color = Palette.Yellow2;
        }
        else
        {
            title = Loc.T("pvp.defeat");
            color = Palette.Purple5;
        }

        var lines = new System.Collections.Generic.List<(string Text, Color Color)>();
        if (!_pvpValid)
            lines.Add((Loc.T("pvp.not_validated"), Palette.Purple5));
        else if (_pvpReward is { } reward)
        {
            lines.Add((Loc.T("pvp.reward_points", reward.Points, PvpProfileStore.Current.Points), Palette.Yellow2));
            if (reward.Ranked)
                lines.Add((Loc.T("pvp.reward_rating", reward.RatingBefore, reward.RatingAfter,
                    PvpUi.RankName(reward.RatingAfter)), Palette.White));
            else
                lines.Add((Loc.T("pvp.unranked"), Palette.Blue1));
        }

        var label = Loc.T("pvp.back_hub");
        var prompt = Loc.T(Context.Input.UsingGamepad ? "recap.run_back_gp" : "recap.run_back");
        const int Gap = 12;
        var btnW = font.Measure(label, 1) + 40;
        var contentW = new[] { font.Measure(title, 3), btnW, font.Measure(prompt, 1) }
            .Concat(lines.Select(l => font.Measure(l.Text, 1))).Max();
        var boxW = contentW + 2 * ModalPadH;
        var boxH = ModalPadV + 21 + Gap + lines.Count * 14 + Gap + RecapBtnH + Gap + 7 + ModalPadV;
        var panel = new Rectangle((viewport.Width - boxW) / 2, (viewport.Height - boxH) / 2, boxW, boxH);
        Context.Style.DrawPanel(sb, panel);
        var y = panel.Y + ModalPadV;
        font.DrawCentered(sb, title, new Rectangle(panel.X, y, panel.Width, 21), 3, color);
        y += 21 + Gap;
        foreach (var (text, c) in lines)
        {
            font.DrawCentered(sb, text, new Rectangle(panel.X, y, panel.Width, 7), 1, c);
            y += 14;
        }
        y += Gap;
        _pvpMenuRect = new Rectangle(panel.X + (panel.Width - btnW) / 2, y, btnW, RecapBtnH);
        DrawRecapButton(sb, _pvpMenuRect, label);
        y += RecapBtnH + Gap;
        var a = 0.5f + 0.5f * MathF.Abs(MathF.Sin(_time * 3f));
        font.DrawCentered(sb, prompt, new Rectangle(panel.X, y, panel.Width, 7), 1, Palette.Cyan1 * a);
    }
}
