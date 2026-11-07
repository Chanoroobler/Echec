namespace ChessArmy.Core.Pvp;

/// <summary>Zone de l'écran multi qu'une étape du tuto met en lumière (None : panneau centré).</summary>
public enum PvpTutorialZone
{
    None, Profile, ArmyTabs, Commander, Board, Reserve, Budget, Points, Chests, Ranking, Launch, Friend,
}

/// <summary>
/// Tuto de l'écran multi : suite fixe d'étapes (une bulle par zone de l'interface, puis les règles du combat).
/// Logique pure de navigation (suivant, précédent, passer) ; l'affichage et la persistance sont côté jeu
/// (<see cref="PvpProfile.MarkTutorialSeen"/> une fois <see cref="Finished"/>).
/// </summary>
public sealed class PvpTutorial
{
    /// <summary>Étapes dans l'ordre : clé de texte (<c>pvp.tuto.&lt;id&gt;.title</c> / <c>.text</c>) et zone visée.
    /// Une idée par bulle : une explication trop longue est coupée en deux étapes.</summary>
    public static readonly (string Id, PvpTutorialZone Zone)[] Steps =
    {
        ("welcome", PvpTutorialZone.None),
        ("profile", PvpTutorialZone.Profile),
        ("armies", PvpTutorialZone.ArmyTabs),
        ("commander", PvpTutorialZone.Commander),
        ("board", PvpTutorialZone.Board),
        ("reserve", PvpTutorialZone.Reserve),
        ("budget", PvpTutorialZone.Budget),
        ("equipment", PvpTutorialZone.Budget),
        ("points", PvpTutorialZone.Points),
        ("chests", PvpTutorialZone.Chests),
        ("ranking", PvpTutorialZone.Ranking),
        ("launch", PvpTutorialZone.Launch),
        ("friend", PvpTutorialZone.Friend),
        ("clock", PvpTutorialZone.None),
        ("collapse", PvpTutorialZone.None),   // pendule basse : une case arrière tremble puis tombe après le coup
        ("rules", PvpTutorialZone.None),
    };

    public static int Count => Steps.Length;

    /// <summary>Étape affichée (0..Count-1) ; Count une fois terminé ou passé.</summary>
    public int Index { get; private set; }

    public bool Finished => Index >= Count;
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Count - 1;
    public string Id => Steps[Index].Id;
    public PvpTutorialZone Zone => Steps[Index].Zone;

    /// <summary>Étape suivante ; après la dernière, le tuto est terminé.</summary>
    public void Next()
    {
        if (!Finished)
            Index++;
    }

    /// <summary>Étape précédente (sans effet sur la première).</summary>
    public bool Previous()
    {
        if (Finished || Index == 0)
            return false;
        Index--;
        return true;
    }

    /// <summary>Passer tout le tuto.</summary>
    public void Skip() => Index = Count;

    /// <summary>Recommencer depuis la première étape (bouton « ? »).</summary>
    public void Restart() => Index = 0;
}
