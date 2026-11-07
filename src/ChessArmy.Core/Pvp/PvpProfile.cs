using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChessArmy.Core.Battle;

namespace ChessArmy.Core.Pvp;

/// <summary>Issue d'une partie multi, vue du joueur local.</summary>
public enum PvpOutcome { Win, Loss, Draw }

/// <summary>Ce qu'une partie a rapporté (affiché sur le panneau de fin).</summary>
public readonly record struct PvpReward(int Points, int RatingBefore, int RatingAfter, bool Ranked);

/// <summary>Une partie de l'historique (DERNIÈRES PARTIES) : issue, adversaire (bot ou non, cote), pions tués,
/// variation de cote et date (secondes Unix UTC).</summary>
public sealed record PvpGameRecord(PvpOutcome Outcome, string Opponent, bool Bot, int OpponentRating, int Kills,
    int RatingDelta, long When);

/// <summary>
/// Profil MULTIJOUEUR, séparé du solo : points de coffre, cote, statistiques, déblocages et les 3 builds.
/// Les valeurs sensibles sont obscurcies en mémoire (<see cref="ObscuredInt"/>, <see cref="ObscuredSet"/>) et
/// le fichier est chiffré et signé (<see cref="Encode"/> / <see cref="Decode"/>).
/// </summary>
public sealed class PvpProfile
{
    public const int BuildSlots = 3;

    /// <summary>Parties gardées par historique (classées d'un côté, défis entre amis de l'autre).</summary>
    public const int HistorySize = 20;

    /// <summary>Jours comptés dans la variation « cette semaine ».</summary>
    public const int WeekDays = 7;

    /// <summary>Version du contenu du fichier : 1 = sans historique (migré à la lecture), 2 = actuel.</summary>
    private const int FormatVersion = 2;

    private ObscuredInt _points;
    private ObscuredInt _best;
    private ObscuredInt _streak;
    private readonly List<PvpGameRecord> _ranked = new();
    private readonly List<PvpGameRecord> _friendly = new();
    private readonly List<(int Day, int Delta)> _days = new();   // variation de cote par jour (classées), 7 derniers jours
    private ObscuredInt _rating;
    private ObscuredInt _games;
    private ObscuredInt _wins;
    private ObscuredInt _losses;
    private ObscuredInt _draws;
    private readonly ObscuredSet _unlocked = new();   // « u:asset », « c:commandant », « e:equipement »

    public int Points => _points;
    public int Rating => _rating;
    public int Games => _games;
    public int Wins => _wins;
    public int Losses => _losses;
    public int Draws => _draws;
    public PvpRank Rank => PvpRanks.FromRating(Rating);

    /// <summary>Meilleure cote atteinte (jamais sous la cote actuelle).</summary>
    public int BestRating => Math.Max(_best, Rating);

    /// <summary>Série en cours sur les parties classées : +3 = trois victoires d'affilée, −2 = deux défaites ;
    /// une nulle la remet à zéro.</summary>
    public int Streak => _streak;

    /// <summary>Dernières parties classées, la plus récente en tête (<see cref="HistorySize"/> au plus).</summary>
    public IReadOnlyList<PvpGameRecord> RankedHistory => _ranked;

    /// <summary>Derniers défis entre amis (non classés), le plus récent en tête.</summary>
    public IReadOnlyList<PvpGameRecord> FriendlyHistory => _friendly;

    /// <summary>Variation de cote sur les <see cref="WeekDays"/> derniers jours (aujourd'hui compris).</summary>
    public int WeekDelta(DateTime nowUtc)
    {
        var today = DayOf(nowUtc);
        var sum = 0;
        foreach (var (day, delta) in _days)
            if (day > today - WeekDays)
                sum += delta;
        return sum;
    }

    private static int DayOf(DateTime utc) => (int)(utc.Ticks / TimeSpan.TicksPerDay);

    /// <summary>Les 3 builds sauvegardés (jamais null).</summary>
    public PvpBuild[] Builds { get; } = new PvpBuild[BuildSlots];

    /// <summary>Build sélectionné par défaut (0..2).</summary>
    public int SelectedBuild { get; set; }

    /// <summary>Ancien tuto multi (un seul panneau) vu ; gardé pour relire les anciens fichiers.</summary>
    public bool TutorialDone { get; set; }

    /// <summary>Version du tuto multi actuel (bulles de l'interface + règles). À monter quand le tuto change
    /// assez pour être remontré à tout le monde.</summary>
    public const int CurrentTutorialVersion = 2;   // 2 : étape « le sol s'effondre » (pendule basse)

    /// <summary>Dernière version du tuto multi vue jusqu'au bout ou passée (0 : jamais, ancien fichier compris).</summary>
    public int TutorialVersion { get; set; }

    /// <summary>Le tuto doit s'ouvrir tout seul à l'arrivée sur l'écran multi.</summary>
    public bool TutorialPending => TutorialVersion < CurrentTutorialVersion;

    /// <summary>Tuto vu jusqu'au bout ou passé : il ne se rouvrira plus tout seul.</summary>
    public void MarkTutorialSeen()
    {
        TutorialVersion = CurrentTutorialVersion;
        TutorialDone = true;
    }

    /// <summary>Vrai si une valeur a été trafiquée en mémoire : le profil ne doit plus être enregistré.</summary>
    public static bool Tampered => ObscuredInt.Tampered;

    // ── Création ─────────────────────────────────────────────────────────────────

    /// <summary>Nouveau profil : points de départ (de quoi ouvrir un coffre de chaque type), cote de départ, pions
    /// T1 et commandants de départ débloqués, et un premier build prêt à jouer (l'échiquier classique).</summary>
    public static PvpProfile CreateNew()
    {
        var config = PvpConfig.Current;
        var profile = new PvpProfile { _points = config.StartPoints, _rating = config.StartRating, _best = config.StartRating };
        foreach (var id in config.StartingCommanders)
            profile._unlocked.Add("c:" + id);
        for (var i = 0; i < BuildSlots; i++)
            profile.Builds[i] = DefaultBuild();
        return profile;
    }

    /// <summary>Build de départ : pvp.json « startingArmy » (à défaut, une armée codée en dur).</summary>
    public static PvpBuild DefaultBuild()
    {
        if (PvpConfig.Current.StartingArmy is { } configured)
            return configured.Clone();   // pvp.json « startingArmy » (composée par l'outil d'équilibrage)
        var build = new PvpBuild { CommanderId = PvpConfig.Current.StartingCommanders.FirstOrDefault() ?? "commandant" };
        string[] back = { Domaines.Tour.BaseClass.Asset, Domaines.Cavalier.BaseClass.Asset, Domaines.Fou.BaseClass.Asset,
            Domaines.Dame.BaseClass.Asset, "", Domaines.Fou.BaseClass.Asset, Domaines.Cavalier.BaseClass.Asset,
            Domaines.Tour.BaseClass.Asset };
        for (var c = 0; c < PvpBuild.Columns; c++)
        {
            if (back[c].Length > 0)
                build.Set(c, PvpBuild.BackLine, back[c]);
            build.Set(c, PvpBuild.FrontLine, Domaines.Dame.BaseClass.Asset);
        }
        return build;
    }

    // ── Déblocages ───────────────────────────────────────────────────────────────

    /// <summary>Vrai si le joueur peut utiliser cet élément (pion par asset, commandant ou équipement par id).
    /// Les pions T1 sont toujours disponibles.</summary>
    public bool Owns(string id)
    {
        if (PvpCatalog.FindClass(id) is { } cls)
            return cls.Class.Tier <= 1 || _unlocked.Contains("u:" + id);
        if (PvpCatalog.CommanderClass(id) != null && Commandes.ById(id) != null)
            return _unlocked.Contains("c:" + id);
        return _unlocked.Contains("e:" + id);
    }

    /// <summary>Contenu restant du coffre de pions : classes T2+ et commandants pas encore débloqués.</summary>
    public List<string> LockedUnits() =>
        PvpCatalog.AllUnitClasses().Where(c => c.Class.Tier > 1 && !Owns(c.Class.Asset)).Select(c => c.Class.Asset)
            .Concat(PvpCatalog.Commanders().Where(c => !Owns(c.Id)).Select(c => c.Id))
            .Distinct().ToList();

    /// <summary>Contenu restant du coffre d'équipements.</summary>
    public List<string> LockedEquipment() =>
        PvpCatalog.AllEquipment().Where(e => !Owns(e.Id)).Select(e => e.Id).ToList();

    /// <summary>
    /// Ouvre un coffre : paie son prix et débloque un élément tiré au hasard parmi ceux qu'on n'a pas encore
    /// (jamais de doublon). Null si le coffre est vide ou trop cher.
    /// </summary>
    /// <summary>Vrai si un coffre de ce type peut s'ouvrir maintenant : assez de points ET quelque chose à gagner.</summary>
    public bool CanOpenChest(bool units) =>
        Points >= (units ? PvpConfig.Current.UnitChestCost : PvpConfig.Current.EquipmentChestCost)
        && (units ? LockedUnits() : LockedEquipment()).Count > 0;

    public string? OpenChest(bool units, Random rng)
    {
        var cost = units ? PvpConfig.Current.UnitChestCost : PvpConfig.Current.EquipmentChestCost;
        var pool = units ? LockedUnits() : LockedEquipment();
        if (pool.Count == 0 || Points < cost)
            return null;
        pool.Sort(StringComparer.Ordinal);
        var pick = pool[rng.Next(pool.Count)];
        _points = Points - cost;
        var prefix = !units ? "e:" : PvpCatalog.FindClass(pick) != null ? "u:" : "c:";
        _unlocked.Add(prefix + pick);
        return pick;
    }

    // ── Résultats ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enregistre une partie : points (chaque pion tué + bonus de victoire / nulle) et, si elle est classée,
    /// nouvelle cote (Elo contre <paramref name="opponentRating"/>). Les défis entre amis rapportent des points
    /// mais ne sont pas classés.
    /// </summary>
    public PvpReward RecordResult(PvpOutcome outcome, int kills, int opponentRating, bool ranked) =>
        RecordResult(outcome, kills, opponentRating, ranked, "", bot: false, DateTime.UtcNow);

    /// <summary>Même chose, et la partie entre dans l'historique (classées ou défis), avec la série, la meilleure
    /// cote et la variation du jour.</summary>
    public PvpReward RecordResult(PvpOutcome outcome, int kills, int opponentRating, bool ranked, string opponent,
        bool bot, DateTime nowUtc)
    {
        var reward = ApplyResult(outcome, kills, opponentRating, ranked);
        var delta = reward.RatingAfter - reward.RatingBefore;
        var history = ranked ? _ranked : _friendly;
        history.Insert(0, new PvpGameRecord(outcome, opponent, bot, opponentRating, kills, delta,
            new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()));
        if (history.Count > HistorySize)
            history.RemoveRange(HistorySize, history.Count - HistorySize);
        if (ranked)
        {
            _best = Math.Max(_best, Rating);
            var s = Streak;
            _streak = outcome switch
            {
                PvpOutcome.Win => s > 0 ? s + 1 : 1,
                PvpOutcome.Loss => s < 0 ? s - 1 : -1,
                _ => 0,
            };
            AddDayDelta(DayOf(nowUtc), delta);
        }
        return reward;
    }

    private void AddDayDelta(int today, int delta)
    {
        _days.RemoveAll(d => d.Day <= today - WeekDays);
        var i = _days.FindIndex(d => d.Day == today);
        if (i >= 0)
            _days[i] = (today, _days[i].Delta + delta);
        else
            _days.Add((today, delta));
    }

    private PvpReward ApplyResult(PvpOutcome outcome, int kills, int opponentRating, bool ranked)
    {
        var config = PvpConfig.Current;
        var gained = kills * config.PointsPerKill + outcome switch
        {
            PvpOutcome.Win => config.PointsForWin,
            PvpOutcome.Draw => config.PointsForDraw,
            _ => 0,
        };
        _points = Points + gained;
        _games = Games + 1;
        if (outcome == PvpOutcome.Win) _wins = Wins + 1;
        else if (outcome == PvpOutcome.Loss) _losses = Losses + 1;
        else _draws = Draws + 1;

        var before = Rating;
        if (ranked)
            _rating = PvpRanks.Updated(before, opponentRating,
                outcome switch { PvpOutcome.Win => 1.0, PvpOutcome.Draw => 0.5, _ => 0.0 });
        return new PvpReward(gained, before, Rating, ranked);
    }

    // ── Fichier chiffré et signé ─────────────────────────────────────────────────

    private sealed class Dto
    {
        public int P { get; set; }
        public int R { get; set; }
        public int G { get; set; }
        public int W { get; set; }
        public int L { get; set; }
        public int D { get; set; }
        public List<string>? U { get; set; }
        public List<string>? B { get; set; }
        public int S { get; set; }
        public bool T { get; set; }
        // Version 2 (absents d'un ancien fichier : migrés à la lecture, réécrits à la prochaine sauvegarde).
        public int V { get; set; }
        public int BR { get; set; }
        public int ST { get; set; }
        public List<GameDto>? HR { get; set; }   // classées
        public List<GameDto>? HF { get; set; }   // défis entre amis
        public List<int[]>? WK { get; set; }     // [jour, variation]
        public int TV { get; set; }              // version du tuto vue (absent d'un ancien fichier : 0)
    }

    private sealed class GameDto
    {
        public int O { get; set; }
        public string? N { get; set; }
        public bool B { get; set; }
        public int R { get; set; }
        public int K { get; set; }
        public int D { get; set; }
        public long W { get; set; }

        public static GameDto From(PvpGameRecord g) =>
            new() { O = (int)g.Outcome, N = g.Opponent, B = g.Bot, R = g.OpponentRating, K = g.Kills, D = g.RatingDelta, W = g.When };

        public PvpGameRecord ToRecord() =>
            new(Enum.IsDefined(typeof(PvpOutcome), O) ? (PvpOutcome)O : PvpOutcome.Draw, N ?? "", B, R, K, D, W);
    }

    /// <summary>Tests : profil au format 1 (avant l'historique), pour vérifier la migration.</summary>
    internal static byte[] EncodeLegacyV1(string ownerId, int points, int rating, int games, int wins) =>
        Seal(JsonSerializer.SerializeToUtf8Bytes(new { P = points, R = rating, G = games, W = wins, L = games - wins, D = 0, S = 0, T = true }), ownerId);

    // Secret embarqué : ce n'est PAS une sécurité absolue (tout ce qui est sur le PC du joueur finit par être lisible),
    // seulement de quoi rendre le fichier illisible et toute retouche détectable.
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("ChessArmy/pvp/7f3c9a1e-profile-seal");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CAPV1");

    /// <summary>
    /// Fichier du profil : JSON chiffré (AES) puis signé (HMAC-SHA256). La clé dépend de <paramref name="ownerId"/>
    /// (l'identifiant Steam) : un profil copié depuis un autre compte est refusé.
    /// </summary>
    public byte[] Encode(string ownerId)
    {
        var dto = new Dto
        {
            P = Points, R = Rating, G = Games, W = Wins, L = Losses, D = Draws,
            U = _unlocked.Items.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            B = Builds.Select(b => b.ToJson()).ToList(), S = SelectedBuild, T = TutorialDone,
            V = FormatVersion, BR = BestRating, ST = Streak,
            HR = _ranked.Select(GameDto.From).ToList(), HF = _friendly.Select(GameDto.From).ToList(),
            WK = _days.Select(d => new[] { d.Day, d.Delta }).ToList(),
            TV = TutorialVersion,
        };
        return Seal(JsonSerializer.SerializeToUtf8Bytes(dto), ownerId);
    }

    private static byte[] Seal(byte[] plain, string ownerId)
    {
        var (encKey, macKey) = Keys(ownerId);
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.GenerateIV();
        var cipher = aes.EncryptCbc(plain, aes.IV);

        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(aes.IV);
        output.Write(cipher);
        var body = output.ToArray();
        var mac = HMACSHA256.HashData(macKey, body);
        return body.Concat(mac).ToArray();
    }

    /// <summary>Relit un fichier de profil ; null s'il est illisible, retouché ou appartient à un autre compte.</summary>
    public static PvpProfile? Decode(byte[] data, string ownerId)
    {
        const int MacSize = 32, IvSize = 16;
        if (data.Length < Magic.Length + IvSize + MacSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        var (encKey, macKey) = Keys(ownerId);
        var body = data.AsSpan(0, data.Length - MacSize);
        var expected = HMACSHA256.HashData(macKey, body);
        if (!CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - MacSize)))
            return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = encKey;
            var iv = body.Slice(Magic.Length, IvSize).ToArray();
            var plain = aes.DecryptCbc(body[(Magic.Length + IvSize)..], iv);
            var dto = JsonSerializer.Deserialize<Dto>(plain);
            if (dto == null)
                return null;
            var profile = new PvpProfile
            {
                _points = dto.P, _rating = dto.R, _games = dto.G, _wins = dto.W, _losses = dto.L, _draws = dto.D,
                SelectedBuild = Math.Clamp(dto.S, 0, BuildSlots - 1), TutorialDone = dto.T, TutorialVersion = Math.Max(0, dto.TV),
                // Format 1 : pas de meilleure cote connue → la cote actuelle ; historique et série vides.
                _best = Math.Max(dto.BR, dto.R), _streak = dto.ST,
            };
            foreach (var g in (dto.HR ?? new List<GameDto>()).Take(HistorySize))
                profile._ranked.Add(g.ToRecord());
            foreach (var g in (dto.HF ?? new List<GameDto>()).Take(HistorySize))
                profile._friendly.Add(g.ToRecord());
            foreach (var d in dto.WK ?? new List<int[]>())
                if (d is { Length: 2 })
                    profile._days.Add((d[0], d[1]));
            foreach (var id in dto.U ?? new List<string>())
                profile._unlocked.Add(id);
            for (var i = 0; i < BuildSlots; i++)
                profile.Builds[i] = (dto.B != null && i < dto.B.Count ? PvpBuild.FromJson(dto.B[i]) : null) ?? DefaultBuild();
            return profile;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static (byte[] Enc, byte[] Mac) Keys(string ownerId)
    {
        var seed = HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(ownerId));
        return (SHA256.HashData(seed.Concat(new byte[] { 1 }).ToArray()),
                SHA256.HashData(seed.Concat(new byte[] { 2 }).ToArray()));
    }
}
