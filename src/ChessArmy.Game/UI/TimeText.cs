namespace ChessArmy.Game.UI;

/// <summary>
/// Mise en forme des durées affichées (chronomètre de run). Partagé par le récap de fin
/// (<c>GameplayScene.DrawRunRecap</c>) et la ligne de slot du menu principal, pour que le même temps
/// s'écrive pareil aux deux endroits.
/// </summary>
public static class TimeText
{
    /// <summary>
    /// Durée compacte : <c>M:SS</c> sous une heure, <c>H:MM:SS</c> au-delà. Pas de zéro de tête sur l'unité
    /// la plus grande (« 7:04 » plutôt que « 07:04 »). Les valeurs négatives ou non finies valent 0.
    /// La police pixel n'ayant pas de glyphe pour tous les symboles, on s'en tient aux chiffres et à « : ».
    /// </summary>
    public static string Duration(double seconds)
    {
        var total = seconds > 0 && double.IsFinite(seconds) ? (long)seconds : 0;
        var h = total / 3600;
        var m = total / 60 % 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
