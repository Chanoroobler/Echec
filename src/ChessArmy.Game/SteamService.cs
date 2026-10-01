using System.Diagnostics;
using Steamworks;

namespace ChessArmy.Game;

/// <summary>
/// Pont vers Steamworks (Steamworks.NET) : initialisation, pompe des callbacks et succès. Échec SILENCIEUX
/// partout : sans Steam lancé, sans <c>steam_api64.dll</c> ou dans la build démo, le jeu tourne normalement
/// et <see cref="Unlock"/> ne fait rien. Les succès se DÉCLARENT dans Steamworks (App Admin → Stats &amp;
/// Achievements) ; les identifiants de <see cref="Achievements"/> doivent y correspondre exactement.
/// </summary>
public static class SteamService
{
    /// <summary>AppID de CHESS ARMY (même numéro que la page boutique, cf. <see cref="Store.SteamUrl"/>).</summary>
    public const uint AppId = 4971900;

    /// <summary>Steam initialisé (client lancé, DLL présente, compte possédant le jeu).</summary>
    public static bool IsActive { get; private set; }

    /// <summary>Faux en mode démo : aucun succès n'est débloqué (la démo n'a pas les siens).</summary>
    public static bool AchievementsEnabled { get; set; } = true;

    // Avec le SDK 1.60, les stats du joueur doivent être REÇUES (UserStatsReceived_t) avant tout
    // SetAchievement : les déblocages demandés avant sont mis en attente puis rejoués.
    private static bool _statsReady;
    private static readonly HashSet<string> _pending = new();
    private static Callback<UserStatsReceived_t>? _statsReceived;

    /// <summary>À appeler une fois, AVANT la création du périphérique graphique (l'overlay Steam s'y accroche).</summary>
    public static void Init()
    {
        try
        {
            IsActive = SteamAPI.Init();
            if (!IsActive)
                return;
            _statsReceived = Callback<UserStatsReceived_t>.Create(OnStatsReceived);
            SteamUserStats.RequestCurrentStats();
        }
        catch (System.Exception ex)   // DllNotFoundException (steam_api64.dll absente), etc.
        {
            IsActive = false;
            Debug.WriteLine($"Steam indisponible : {ex.Message}");
        }
    }

    /// <summary>Pompe les callbacks Steam (à appeler à chaque frame).</summary>
    public static void Update()
    {
        if (IsActive)
            SteamAPI.RunCallbacks();
    }

    public static void Shutdown()
    {
        if (!IsActive)
            return;
        IsActive = false;
        SteamAPI.Shutdown();
    }

    /// <summary>Débloque un succès (idempotent). Sans effet hors Steam ou en démo.</summary>
    public static void Unlock(string id)
    {
        if (!IsActive || !AchievementsEnabled)
            return;
        if (!_statsReady)
        {
            _pending.Add(id);
            return;
        }

        if (SteamUserStats.GetAchievement(id, out var achieved) && achieved)
            return;

        if (SteamUserStats.SetAchievement(id))
            SteamUserStats.StoreStats();   // envoie au serveur + déclenche la notification Steam
        else
            Debug.WriteLine($"Succès Steam inconnu : {id} (déclaré et publié dans Steamworks ?)");
    }

    private static void OnStatsReceived(UserStatsReceived_t e)
    {
        if (e.m_nGameID != AppId || e.m_eResult != EResult.k_EResultOK)
            return;
        _statsReady = true;
        foreach (var id in _pending.ToList())
            Unlock(id);
        _pending.Clear();
    }
}

/// <summary>
/// Identifiants API des succès (colonne « API Name » dans Steamworks). Les succès PAR COMMANDANT sont
/// dérivés de son id (<c>CommandeDef.Id</c>) : un nouveau commandant n'a qu'à être déclaré dans Steamworks.
/// </summary>
public static class Achievements
{
    /// <summary>Gagner une partie (n'importe quel commandant, n'importe quelle difficulté).</summary>
    public const string RunWon = "ACH_RUN_WON";

    /// <summary>Gagner une partie avec ce commandant (toute difficulté).</summary>
    public static string WinWith(string commanderId) => "ACH_WIN_" + commanderId.ToUpperInvariant();

    /// <summary>Gagner une partie en Difficile avec ce commandant.</summary>
    public static string WinHardWith(string commanderId) => "ACH_WIN_HARD_" + commanderId.ToUpperInvariant();

    /// <summary>Découvrir la moitié des équipements.</summary>
    public const string EquipmentHalf = "ACH_EQUIPMENT_HALF";
    /// <summary>Découvrir tous les équipements.</summary>
    public const string EquipmentAll = "ACH_EQUIPMENT_ALL";
    /// <summary>Découvrir toutes les classes normales (4 domaines × 3 tiers, hors Paysan et boss).</summary>
    public const string UnitsAll = "ACH_UNITS_ALL";

    /// <summary>Le Broyeur : promouvoir la paysanne commandant (nœud « Révolte »).</summary>
    public const string Revolte = "ACH_REVOLTE";

    /// <summary>Unités mortes en combat, tous camps confondus, cumulées sur toutes les parties.</summary>
    public const string Deaths = "ACH_DEATHS_500";
    public const int DeathsThreshold = 500;

    /// <summary>3 ennemis tués en une seule action du joueur (effets en chaîne compris).</summary>
    public const string TripleKill = "ACH_TRIPLE_KILL";

    /// <summary>Tous les paysans d'une mission spéciale « libérer » / « sauver » / « protéger » (Normal ou Difficile).</summary>
    public const string LibererAll = "ACH_LIBERER_ALL";
    public const string SauverAll = "ACH_SAUVER_ALL";
    public const string ProtegerAll = "ACH_PROTEGER_ALL";

    /// <summary>Première fusion donnant une unité de tier 3.</summary>
    public const string FusionT3 = "ACH_FUSION_T3";
}
