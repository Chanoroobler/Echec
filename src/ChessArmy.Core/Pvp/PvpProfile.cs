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

/// <summary>
/// Profil MULTIJOUEUR, séparé du solo : points de coffre, cote, statistiques, déblocages et les 3 builds.
/// Les valeurs sensibles sont obscurcies en mémoire (<see cref="ObscuredInt"/>, <see cref="ObscuredSet"/>) et
/// le fichier est chiffré et signé (<see cref="Encode"/> / <see cref="Decode"/>).
/// </summary>
public sealed class PvpProfile
{
    public const int BuildSlots = 3;

    private ObscuredInt _points;
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

    /// <summary>Les 3 builds sauvegardés (jamais null).</summary>
    public PvpBuild[] Builds { get; } = new PvpBuild[BuildSlots];

    /// <summary>Build sélectionné par défaut (0..2).</summary>
    public int SelectedBuild { get; set; }

    /// <summary>Le tuto multi (présentation des coffres) a été vu.</summary>
    public bool TutorialDone { get; set; }

    /// <summary>Vrai si une valeur a été trafiquée en mémoire : le profil ne doit plus être enregistré.</summary>
    public static bool Tampered => ObscuredInt.Tampered;

    // ── Création ─────────────────────────────────────────────────────────────────

    /// <summary>Nouveau profil : points de départ (de quoi ouvrir un coffre de chaque type), cote de départ, pions
    /// T1 et commandants de départ débloqués, et un premier build prêt à jouer (l'échiquier classique).</summary>
    public static PvpProfile CreateNew()
    {
        var config = PvpConfig.Current;
        var profile = new PvpProfile { _points = config.StartPoints, _rating = config.StartRating };
        foreach (var id in config.StartingCommanders)
            profile._unlocked.Add("c:" + id);
        for (var i = 0; i < BuildSlots; i++)
            profile.Builds[i] = DefaultBuild();
        return profile;
    }

    /// <summary>Build de départ : 8 Soldats devant, Lancier Cavalier Mage Soldat [roi] Mage Cavalier Lancier au fond.</summary>
    public static PvpBuild DefaultBuild()
    {
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
    public PvpReward RecordResult(PvpOutcome outcome, int kills, int opponentRating, bool ranked)
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
    }

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
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(dto);
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
                SelectedBuild = Math.Clamp(dto.S, 0, BuildSlots - 1), TutorialDone = dto.T,
            };
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
