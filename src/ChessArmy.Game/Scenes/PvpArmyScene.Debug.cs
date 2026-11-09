#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Localization;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>Accroches de DÉVELOPPEMENT (build Debug seulement) : forcer un état de l'écran pour le capturer.</summary>
public sealed partial class PvpArmyScene
{
    /// <summary>Position du pointeur imposée (survol simulé), à la place de la souris.</summary>
    internal Point? DebugPointer;

    internal void DebugLayout() => ComputeLayout();

    /// <summary>Fenêtre d'entraînement ouverte ; renvoie le centre de la case n° <paramref name="hover"/>.</summary>
    internal Point DebugTraining(int hover)
    {
        ComputeLayout();
        _trainingOpen = true;
        LayoutTraining();
        return hover >= 0 && hover < _trainingCells.Count ? _trainingCells[hover].Center : new Point(-100, -100);
    }

    /// <summary>Entraînement lancé (bot de la division <paramref name="key"/>), compte à rebours en cours.</summary>
    internal void DebugStartTraining(string key) => StartTraining(key);

    /// <summary>Une fiche est ouverte sur le plateau à cette frame.</summary>
    internal bool DebugSheetShown => _view == View.Build && (_sheetSubject.Cls != null || _sheetSubject.Lone != null);

    internal Point DebugCellCenter(int column, int line)
    {
        ComputeLayout();
        var row = line == PvpBuild.BackLine ? PvpBoardRenderer.Size - 1 : PvpBoardRenderer.Size - 2;
        return CellRect(row, column).Center;
    }

    internal Point DebugCardCenter(int slot)
    {
        ComputeLayout();
        return _lay.Cards[slot].Center;
    }

    internal Point DebugBadgeCenter(int column, int line)
    {
        ComputeLayout();
        return BadgeRect(column, line).Center;
    }

    /// <summary>Survol de la carte n° <paramref name="index"/> de la réserve (page selon la taille de la grille).
    /// Hors liste : pointeur hors écran (rien de survolé).</summary>
    internal Point DebugHoverCard(bool equipment, int index)
    {
        ComputeLayout();
        EnsureEntries();
        if (index >= (equipment ? _equips.Count : _units.Count))
            return new Point(-100, -100);
        DebugReserve(equipment, index / _lay.CardCount);
        return DebugCardCenter(index % _lay.CardCount);
    }

    internal void DebugReserve(bool equipment, int page)
    {
        _reserveEquip = equipment;
        _page[equipment ? 1 : 0] = page;
    }

    /// <summary>Ouvre la page de la réserve d'équipements qui porte <paramref name="id"/> et renvoie le centre de sa carte.</summary>
    internal Point DebugShowEquipCard(string id)
    {
        ComputeLayout();
        EnsureEntries();
        var index = Math.Max(0, _equips.FindIndex(e => e.Equipment.Id == id));
        DebugReserve(true, index / _lay.CardCount);
        return DebugCardCenter(index % _lay.CardCount);
    }

    internal void DebugHoldCard(int index, bool equipment)
    {
        ComputeLayout();
        EnsureEntries();
        _reserveEquip = equipment;
        _page[equipment ? 1 : 0] = index / _lay.CardCount;
        Pick(Hit.Card, index, sticky: true);
    }

    internal void DebugHoldCell(int column, int line) => Pick(Hit.Cell, CellIndex(column, line), sticky: false);

    internal void DebugMessage(string key) => Say(Loc.T(key), error: true);

    internal void DebugSearching(float seconds, bool friend)
    {
        _mode = friend ? SearchMode.FriendHost : SearchMode.Search;
        _friend = friend;
        _searchTime = seconds;
    }

    internal void DebugFound(float countdown)
    {
        _locked = LockBuild();
        _botMatch = PvpMatchInfo.ForBot(_locked, Profile.Rating, new Random(3));
        _countdown = countdown;
    }

    internal void DebugChests() => _view = View.Chests;

    /// <summary>Vue Classement avec un classement factice (données simulées des maquettes multi-7 à 9) ; sans
    /// <paramref name="online"/>, comme sans Steam.</summary>
    internal void DebugRanking(int tab, int page, bool online = true, bool friendly = false)
    {
        const ulong me = 1;
        var armies = PvpConfig.Current.BotArmies;
        var rng = new Random(8);
        PvpBuild Army(int i) => PvpCatalog.RandomEquipment(armies[(i * 7) % armies.Count], rng);
        string[] topNames = { "Kaelith", "Brumerouge", "TourDeGarde", "Pionnier42", "LameDeFer", "Zephyrin", "GrandMaitreLeo",
            "Sorcelune", "Vaillance", "Le_Fou_Du_Roi_Qui_Ne_Dort_Jamais", "Ombrelame", "Castelroc" };
        var top = new List<PvpLeaderboardEntry>();
        for (var i = 0; i < 50; i++)
        {
            var rating = 2312 - i * 22 - (i > 0 ? 2 : 0);
            var w = 47 + i * 5 % 90;
            top.Add(new PvpLeaderboardEntry((ulong)(100 + i), i < topNames.Length ? topNames[i] : $"Joueur{i + 1}", i + 1, rating,
                w, w / 4, i % 10, Army(i)));
        }
        var mine = new PvpLeaderboardEntry(me, "Chanoroobler", 1247, Profile.Rating, Profile.Wins, Profile.Losses, Profile.Draws, Profile.Builds[0]);
        string[] aroundNames = { "Vif_Argent", "Tonnelier", "Minos", "", "Bricabrac", "Escarbille", "Gaston_R", "Lutin" };
        var around = new List<PvpLeaderboardEntry>();
        for (var i = 0; i < aroundNames.Length; i++)
            around.Add(i == 3 ? mine : new PvpLeaderboardEntry((ulong)(200 + i), aroundNames[i], 1244 + i, 956 - i * 2 - (i > 3 ? 1 : 0),
                11 + i, 9 + i % 3, i % 3, Army(i + 20)));
        var friends = new List<PvpLeaderboardEntry>
        {
            new(300, "Basile", 312, 1488, 40, 22, 3, Army(30)),
            new(301, "Rougegorge", 801, 1104, 25, 20, 1, Army(31)),
            mine,
            new(302, "Tonnelier", 1245, 954, 12, 10, 2, Army(32)),
            new(303, "Mamie_Echecs", 3020, 640, 3, 11, 0, null),
        };
        if (online)
            PvpLeaderboardService.UseFake(top, friends, around, me);
        else
            PvpLeaderboardService.UseOffline();
        _view = View.Ranking;
        _rankTab = (RankTab)tab;
        _rankPage = page;
        _histFriendly = friendly;
        _rowKey = default;
    }

    internal Point DebugRankRowCenter(int k)
    {
        ComputeLayout();
        RankingLayout();
        return _rl.Rows[k].Center;
    }

    internal void DebugReveal(string id, bool units)
    {
        _view = View.Chests;
        _reward = id;
        _rewardUnits = units;
        _revealOpen = true;
        _revealT = 1f;   // effet d'ouverture passé : la fenêtre au repos
    }

    /// <summary>Roulette du coffre figée à <paramref name="seconds"/> du défilement.</summary>
    internal void DebugReel(bool units, float seconds)
    {
        _view = View.Chests;
        EnsureChestItems();
        var pool = (units ? _chestUnits : _chestEquips).ToList();
        _reward = pool[pool.Count / 2].Id;
        _rewardUnits = units;
        BuildReel(pool, _reward);
        _rolling = true;
        _rollT = seconds;
    }

    /// <summary>Tuto ouvert à l'étape <paramref name="step"/> (0 = bienvenue).</summary>
    internal void DebugTutorial(int step)
    {
        _view = View.Build;
        _tutorialOpen = true;
        _tuto.Restart();
        for (var i = 0; i < step; i++)
            _tuto.Next();
    }
}

/// <summary>
/// Captures de l'écran Armée sans piloter la fenêtre : <c>ChessArmy.Game -pvpshots &lt;dossier&gt;</c> (Debug).
/// Chaque étape crée la scène avec un profil EN MÉMOIRE (le vrai profil n'est ni lu ni écrit), force un état,
/// rend le canevas à la taille voulue puis le compose ×2 comme à l'écran (couche des sprites nets comprise) et
/// l'enregistre en PNG. Le jeu se ferme à la fin.
/// </summary>
internal sealed class PvpShots
{
    private sealed record Step(string Name, Point Canvas, Language Lang, Action<PvpArmyScene> Setup);

    private const int Scale = 2;
    private readonly GameContext _ctx;
    private readonly Action _exit;
    private readonly string _dir;
    private readonly List<Step> _steps = new();
    private PvpArmyScene? _scene;
    private RenderTarget2D? _canvas, _out;
    private int _index, _frame;
    private readonly PvpConfig _config;

    private PvpShots(GameContext ctx, Action exit, string dir)
    {
        _ctx = ctx;
        _exit = exit;
        _dir = dir;
        _config = PvpConfig.Current;
        Directory.CreateDirectory(dir);
        var big = new Point(1280, 720);
        var small = new Point(960, 540);
        foreach (var (canvas, tag) in new[] { (big, "1280"), (small, "960") })
        {
            _steps.Add(new Step($"{tag}-1-build-pions", canvas, Language.Francais, s =>
            {
                s.DebugReserve(false, 0);
                s.DebugPointer = s.DebugCellCenter(3, PvpBuild.FrontLine) - new Point(18, 18);   // le pion, pas son badge
            }));
            _steps.Add(new Step($"{tag}-2-build-equipements", canvas, Language.Francais, s =>
            {
                Profile.Builds[0].CommanderId = "Commandant_cavalier";
                s.DebugReserve(true, 0);
                s.DebugPointer = s.DebugCardCenter(1);
            }));
            _steps.Add(new Step($"{tag}-2b-equipement-tenu", canvas, Language.Francais, s =>
            {
                s.DebugHoldCard(0, equipment: true);
                s.DebugPointer = s.DebugCellCenter(1, PvpBuild.FrontLine);
            }));
            _steps.Add(new Step($"{tag}-2c-traits-longs", canvas, Language.Deutsch, s =>
            {
                // Les commandants multi n'ont pas de trait aujourd'hui : on en injecte deux longs pour vérifier le retour à la ligne.
                var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/Config/pvp.json"));
                json = System.Text.RegularExpressions.Regex.Replace(json, "(\"Commandant_cavalier\":\\s*\\{[^}]*?)\\s*\\}",
                    "$1, \"traits\": [\"Repositionnement stratégique\", \"Aura de célérité\", \"Orage\"] }");
                PvpConfig.Load(PvpConfig.FromJson(json));
                Profile.Builds[0].CommanderId = "Commandant_cavalier";
                s.DebugPointer = s.DebugCellCenter(PvpBuild.KingColumn, PvpBuild.BackLine);
            }));
            // Fiche au survol : Garde équipé (bonus de PV), Paladin avec un équipement à trait (3 traits), commandant.
            _steps.Add(new Step($"{tag}-2d-fiche-garde", canvas, Language.Francais, s =>
            {
                var eq = PvpCatalog.AllEquipment().First(e => e.BonusFor(Core.Equip.EquipStat.Hp) > 0);
                Profile.Builds[0].Set(0, PvpBuild.FrontLine, "garde", eq.Id);
                s.DebugPointer = s.DebugCellCenter(0, PvpBuild.FrontLine) - new Point(18, 18);
            }));
            _steps.Add(new Step($"{tag}-2e-fiche-paladin", canvas, Language.Francais, s =>
            {
                var eq = PvpCatalog.AllEquipment().First(e => e.Traits.Any(t => t is not ("Franchissement" or "Impact")));
                Profile.Builds[0].Set(0, PvpBuild.FrontLine, "paladin", eq.Id);
                s.DebugPointer = s.DebugCellCenter(0, PvpBuild.FrontLine) - new Point(18, 18);
            }));
            _steps.Add(new Step($"{tag}-2f-fiche-commandant", canvas, Language.Francais, s =>
                s.DebugPointer = s.DebugCellCenter(PvpBuild.KingColumn, PvpBuild.BackLine)));
            _steps.Add(new Step($"{tag}-3-glisser", canvas, Language.Francais, s =>
            {
                s.DebugHoldCell(1, PvpBuild.FrontLine);
                s.DebugPointer = s.DebugCellCenter(4, PvpBuild.FrontLine);
            }));
            _steps.Add(new Step($"{tag}-4-recherche", canvas, Language.Francais, s =>
            {
                s.DebugSearching(2.4f, friend: false);
                s.DebugMessage("pvp.err_king");
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-5b-entrainement", canvas, Language.Francais, s =>
                s.DebugPointer = s.DebugTraining(hover: 9)));   // survol : Or I
            _steps.Add(new Step($"{tag}-5c-entrainement-de", canvas, Language.Deutsch, s =>
                s.DebugPointer = s.DebugTraining(hover: 12)));   // textes longs, survol : Master
            _steps.Add(new Step($"{tag}-5d-entrainement-lance", canvas, Language.Francais, s =>
            {
                s.DebugStartTraining("or-2");
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-5-adversaire-trouve", canvas, Language.Francais, s =>
            {
                s.DebugFound(4.2f);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-6-coffres", canvas, Language.Francais, s =>
            {
                s.DebugChests();
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-6b-roulette-equipements", canvas, Language.Francais, s =>
            {
                s.DebugReel(units: false, seconds: 1.2f);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-6c-roulette-pions", canvas, Language.Francais, s =>
            {
                s.DebugReel(units: true, seconds: 2.0f);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-6d-roulette-arret", canvas, Language.Francais, s =>
            {
                s.DebugReel(units: true, seconds: 4f);
                s.DebugWow(reveal: false, seconds: 0.12f);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-7b-coffre-waouh", canvas, Language.Francais, s =>
            {
                var eq = PvpCatalog.AllEquipment().Where(e => e.Rarity == Core.Equip.EquipmentRarity.Legendary)
                    .OrderByDescending(e => e.Effects.Count(x => x.Trait != null)).First();   // avec un trait : description visible
                s.DebugReveal(eq.Id, units: false);
                s.DebugWow(reveal: true, seconds: 0.4f);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-10-classement-mondial", canvas, Language.Francais, s =>
            {
                s.DebugRanking(tab: 0, page: 0);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-10b-classement-survol", canvas, Language.Francais, s =>
            {
                s.DebugRanking(tab: 0, page: 1);
                s.DebugPointer = s.DebugRankRowCenter(1);
            }));
            _steps.Add(new Step($"{tag}-10c-classement-amis", canvas, Language.Francais, s =>
            {
                s.DebugRanking(tab: 1, page: 0, friendly: true);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-10d-classement-autour", canvas, Language.Francais, s =>
            {
                s.DebugRanking(tab: 2, page: 0);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-10e-classement-hors-ligne", canvas, Language.Francais, s =>
            {
                s.DebugRanking(tab: 0, page: 0, online: false);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"{tag}-7-coffre-recompense", canvas, Language.Francais, s =>
            {
                var eq = PvpCatalog.AllEquipment().First(e => Profile.Owns(e.Id) && e.Rarity == Core.Equip.EquipmentRarity.Rare);
                s.DebugReveal(eq.Id, units: false);
                s.DebugPointer = new Point(-100, -100);
            }));
            AddRevealSteps(tag, canvas, Language.Francais);
        }
        // Totem de rempart : la description d'Aura de rempart cite Rempart, rappelé juste dessous (fiche + coffre).
        foreach (var (canvas, tag, lang) in new[] { (big, "1280", Language.Francais), (small, "960", Language.Francais),
                     (small, "960", Language.Deutsch), (big, "1280", Language.Deutsch) })
        {
            _steps.Add(new Step($"{tag}-totem-{lang}-fiche", canvas, lang, s => s.DebugPointer = s.DebugShowEquipCard("auraRempartRare")));
            _steps.Add(new Step($"{tag}-totem-{lang}-coffre", canvas, lang, s =>
            {
                s.DebugReveal("auraRempartLegendaire", units: false);
                s.DebugPointer = new Point(-100, -100);
            }));
        }
        AddRevealSteps("960", small, Language.Deutsch);
        AddRevealSteps("960", small, Language.ChineseSimplified);
        // Langues verbeuses et chinois, au format le plus serré.
        foreach (var lang in new[] { Language.Deutsch, Language.Polski, Language.Turkce, Language.English, Language.ChineseSimplified })
        {
            _steps.Add(new Step($"960-lang-{lang}-build", small, lang, s =>
            {
                Profile.Builds[0].CommanderId = "Commandant_cavalier";
                s.DebugReserve(true, 0);
                s.DebugPointer = s.DebugCellCenter(3, PvpBuild.FrontLine);
            }));
            _steps.Add(new Step($"960-lang-{lang}-coffres", small, lang, s =>
            {
                s.DebugChests();
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"960-lang-{lang}-classement", small, lang, s =>
            {
                s.DebugRanking(tab: 0, page: 0);
                s.DebugPointer = new Point(-100, -100);
            }));
            _steps.Add(new Step($"1280-lang-{lang}-classement", big, lang, s =>
            {
                s.DebugRanking(tab: 2, page: 0);
                s.DebugPointer = new Point(-100, -100);
            }));
        }
        // Tuto : chaque étape aux deux tailles en français, et au format serré dans les langues verbeuses et en chinois.
        foreach (var (canvas, tag, lang) in new[] { (big, "1280", Language.Francais), (small, "960", Language.Francais),
                     (small, "960", Language.Deutsch), (small, "960", Language.Polski), (small, "960", Language.ChineseSimplified) })
            for (var i = 0; i < PvpTutorial.Count; i++)
            {
                var step = i;
                _steps.Add(new Step($"{tag}-tuto-{lang}-{step + 1:00}-{PvpTutorial.Steps[step].Id}", canvas, lang, s =>
                {
                    s.DebugTutorial(step);
                    s.DebugPointer = new Point(-100, -100);
                }));
            }

        // Fiches : survol de CHAQUE carte de la réserve (pions et équipements), pour vérifier que tout tient
        // sans défilement. Les cartes au-delà de la fin de la liste ne survolent rien.
        foreach (var (canvas, tag, lang) in new[] { (small, "960", Language.Francais), (big, "1280", Language.Francais),
                     (small, "960", Language.Deutsch) })
            foreach (var equip in new[] { false, true })
                for (var i = 0; i < 40; i++)
                {
                    var index = i;
                    var e = equip;
                    _steps.Add(new Step($"{tag}-fiche-{lang}-{(equip ? "equip" : "pion")}-{index:00}", canvas, lang,
                        s => s.DebugPointer = s.DebugHoverCard(e, index)));
                }

        // Fiche sur le plateau, cas de la maquette, dans les 8 langues aux deux tailles : Arbalétrier (Zone morte,
        // Recule, Transpercer) porteur de la Bannière du régiment (Formation), le badge de cet objet (« sur ce pion »),
        // le commandant (case fixe), une carte d'équipement, et le plateau dégagé quand rien n'est survolé.
        foreach (var lang in Enum.GetValues<Language>())
            foreach (var (canvas, tag) in new[] { (big, "1280"), (small, "960") })
            {
                _steps.Add(new Step($"{tag}-plateau-{lang}-1-arbaletrier", canvas, lang, s =>
                {
                    Profile.Builds[0].Set(2, PvpBuild.FrontLine, "arbaletrier", "formationRare");
                    s.DebugPointer = s.DebugCellCenter(2, PvpBuild.FrontLine) - new Point(18, 18);
                }));
                _steps.Add(new Step($"{tag}-plateau-{lang}-2-badge", canvas, lang, s =>
                {
                    Profile.Builds[0].Set(2, PvpBuild.FrontLine, "arbaletrier", "formationRare");
                    s.DebugPointer = s.DebugBadgeCenter(2, PvpBuild.FrontLine);
                }));
                _steps.Add(new Step($"{tag}-plateau-{lang}-3-commandant", canvas, lang, s =>
                {
                    Profile.Builds[0].CommanderId = "Commandant_cavalier";
                    s.DebugPointer = s.DebugCellCenter(PvpBuild.KingColumn, PvpBuild.BackLine);
                }));
                _steps.Add(new Step($"{tag}-plateau-{lang}-4-equipement", canvas, lang,
                    s => s.DebugPointer = s.DebugShowEquipCard("auraRempartRare")));
                _steps.Add(new Step($"{tag}-plateau-{lang}-5-carte-pion", canvas, lang, s => s.DebugPointer = s.DebugHoverCard(false, 2)));
                _steps.Add(new Step($"{tag}-plateau-{lang}-6-vide", canvas, lang, s => s.DebugPointer = new Point(-100, -100)));
            }
    }

    /// <summary>Fenêtre « NOUVEAU ! » : Fleuret de riposte (+2 puissance, Riposte), Ceinture du karatéka (2 bonus,
    /// Esquive), Paladin (3 traits : objet ×2) et un commandant.</summary>
    private void AddRevealSteps(string tag, Point canvas, Language lang)
    {
        foreach (var (name, id, units) in new[] { ("fleuret", "riposteRare", false), ("ceinture", "esquiveRare", false),
                     ("paladin", "paladin", true), ("commandant", "Commandant_fou", true) })
            _steps.Add(new Step($"{tag}-revele-{lang}-{name}", canvas, lang, s =>
            {
                s.DebugReveal(id, units);
                s.DebugPointer = new Point(-100, -100);
            }));
    }

    private static PvpProfile Profile => PvpProfileStore.Current;

    public static PvpShots? FromArgs(GameContext ctx, Action exit)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => string.Equals(a, "-pvpshots", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
            return null;
        var shots = new PvpShots(ctx, exit, args[i + 1]);
        // Filtre facultatif : -pvpshots <dossier> <texte> ne garde que les captures dont le nom contient ce texte.
        if (i + 2 < args.Length && !args[i + 2].StartsWith('-'))
            shots._steps.RemoveAll(s => !s.Name.Contains(args[i + 2], StringComparison.OrdinalIgnoreCase));
        return shots;
    }

    /// <summary>Historique simulé des maquettes : 22 parties (12 V, 8 D, 2 N), série de 3 victoires, les 4 dernières
    /// contre Minos, Rougegorge, un bot Fer et Tonnelier ; deux défis entre amis.</summary>
    private static void AddDemoHistory(PvpProfile p)
    {
        var now = DateTime.UtcNow;
        var filler = "WLWWLDWLWWLWLDWLWL";   // 9 V, 7 D, 2 N, les plus anciennes
        for (var i = 0; i < filler.Length; i++)
        {
            var o = filler[i] switch { 'W' => PvpOutcome.Win, 'L' => PvpOutcome.Loss, _ => PvpOutcome.Draw };
            p.RecordResult(o, 3 + i % 4, 940 + i % 5 * 8, true, $"Adversaire{i}", i % 3 == 0, now.AddDays(-9 + i * 0.4));
        }
        p.RecordResult(PvpOutcome.Loss, 3, 962, true, "Tonnelier", false, now.AddHours(-26));
        p.RecordResult(PvpOutcome.Win, 7, 1110, true, "BOT FER", true, now.AddHours(-2));
        p.RecordResult(PvpOutcome.Win, 4, 931, true, "Rougegorge", false, now.AddMinutes(-40));
        p.RecordResult(PvpOutcome.Win, 6, 957, true, "Minos", false, now.AddMinutes(-12));
        p.RecordResult(PvpOutcome.Win, 5, 1200, false, "Basile", false, now.AddDays(-1));
        p.RecordResult(PvpOutcome.Loss, 2, 1104, false, "Rougegorge_Le_Magnifique_Du_Nord", false, now.AddMinutes(-5));
    }

    /// <summary>Profil de démonstration : quelques parties gagnées, deux coffres de chaque type ouverts, une armée équipée.</summary>
    private static PvpProfile DemoProfile()
    {
        var p = PvpProfile.CreateNew();
        p.MarkTutorialSeen();
        for (var i = 0; i < 6; i++)
            p.RecordResult(i % 4 == 3 ? PvpOutcome.Loss : PvpOutcome.Win, 20, 950, ranked: true);
        var rng = new Random(7);
        for (var i = 0; i < 4; i++)
            p.OpenChest(units: true, rng);
        for (var i = 0; i < 5; i++)
            p.OpenChest(units: false, rng);
        var b = p.Builds[0];
        // De la place dans le budget pour deux équipements : deux soldats de devant en moins.
        PvpPlacement.RemoveUnit(b, 0, PvpBuild.FrontLine);
        PvpPlacement.RemoveUnit(b, 7, PvpBuild.FrontLine);
        PvpPlacement.RemoveUnit(b, 6, PvpBuild.FrontLine);
        var owned = PvpCatalog.AllEquipment().Where(e => p.Owns(e.Id)).OrderBy(e => PvpConfig.Current.CostOf(e)).ToList();
        if (owned.Count > 0) PvpPlacement.Equip(b, 3, PvpBuild.FrontLine, owned[0].Id);
        if (owned.Count > 1) PvpPlacement.Equip(b, 4, PvpBuild.FrontLine, owned[1].Id);
        if (owned.Count > 2) PvpPlacement.Equip(b, 2, PvpBuild.BackLine, owned[2].Id);
        p.Builds[1].Slots.Clear();
        p.Builds[1].Set(3, PvpBuild.FrontLine, Core.Battle.Domaines.Dame.BaseClass.Asset);
        p.Builds[2].Slots.Clear();
        return p;
    }

    /// <summary>Une frame du mode capture (à la place du rendu normal).</summary>
    public void Tick(GameTime gameTime)
    {
        if (_index >= _steps.Count)
        {
            _exit();
            return;
        }
        var step = _steps[_index];
        var gd = _ctx.GraphicsDevice;
        if (_frame == 0)
        {
            Loc.Current = step.Lang;
            PvpConfig.Load(_config);   // une étape peut l'avoir modifié
            _ctx.VirtualResolution = step.Canvas;
            PvpProfileStore.UseInMemory(DemoProfile());
            if (step.Name.Contains("classement"))
                AddDemoHistory(PvpProfileStore.Current);
            _scene?.Unload();
            _scene = new PvpArmyScene(_ctx);
            _scene.Load();
            step.Setup(_scene);
            _canvas?.Dispose();
            _out?.Dispose();
            _canvas = new RenderTarget2D(gd, step.Canvas.X, step.Canvas.Y);
            _out = new RenderTarget2D(gd, step.Canvas.X * Scale, step.Canvas.Y * Scale);
        }
        _scene!.Update(gameTime);
        NativeSprites.Scale = Scale;
        NativeSprites.Clear();
        gd.SetRenderTarget(_canvas);
        gd.Clear(Color.Black);
        _scene.Draw(gameTime);
        gd.SetRenderTarget(_out);
        gd.Clear(Color.Black);
        var sb = _ctx.SpriteBatch;
        var dest = new Rectangle(0, 0, _out!.Width, _out.Height);
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_canvas, dest, Color.White);
        sb.End();
        NativeSprites.Flush(sb, dest, Scale);
        gd.SetRenderTarget(null);

        if (++_frame < 4)
            return;
        using (var file = File.Create(Path.Combine(_dir, step.Name + ".png")))
            _out.SaveAsPng(file, _out.Width, _out.Height);
        // Rapport des fiches qui ne tiennent pas telles quelles (fiches.txt) : rappels retirés, demi-taille, tronquées.
        if (_scene.DebugSheetShown && _scene.SheetFit > 0)
            File.AppendAllText(Path.Combine(_dir, "fiches.txt"),
                $"{step.Name} : {(_scene.SheetFit switch { 1 => "rappels retires", 2 => "demi-taille", _ => "TRONQUEE" })}\n");
        _frame = 0;
        _index++;
    }
}
#endif
