using System.Text.Json.Serialization;
using Echec.Engine.Localization;
using Echec.Engine.Settings;

namespace Echec.Engine.Persistence;

/// <summary>
/// Forme sérialisable des réglages (un fichier <c>options.json</c>). DTO plat plutôt que de
/// sérialiser <see cref="GameSettings"/> directement : ses sous-objets sont en lecture seule et
/// l'audio expose des champs — un DTO à propriétés simples évite les écueils de System.Text.Json.
/// </summary>
public sealed class SettingsDto
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public WindowMode Mode { get; set; } = WindowMode.Windowed;

    // Compat : anciens réglages booléens, d'avant la fusion en un seul « Mode d'affichage ».
    // Lus pour migrer vers Mode quand un vieux options.json les contient ; jamais réécrits
    // (null dans From() → omis à la sérialisation), si bien qu'ils disparaissent au prochain Save.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fullscreen { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Borderless { get; set; }

    public int Master { get; set; } = 80;
    public int Music { get; set; } = 80;
    public int Sfx { get; set; } = 80;
    public Language Language { get; set; } = Language.Francais;

    public static SettingsDto From(GameSettings s) => new()
    {
        Width = s.Display.Width,
        Height = s.Display.Height,
        Mode = s.Display.Mode,
        Master = s.Audio.Master,
        Music = s.Audio.Music,
        Sfx = s.Audio.Sfx,
        Language = s.Language,
    };

    public void ApplyTo(GameSettings s)
    {
        s.Display.Width = Width;
        s.Display.Height = Height;
        // Migration : un ancien fichier n'a pas de « Mode » mais des booléens Fullscreen/Borderless.
        // S'ils sont présents on en déduit le mode ; sinon on prend directement Mode.
        s.Display.Mode = (Fullscreen.HasValue || Borderless.HasValue)
            ? (Fullscreen == true ? WindowMode.Fullscreen
             : Borderless == true ? WindowMode.Borderless
             : WindowMode.Windowed)
            : Mode;
        s.Audio.Master = Master;
        s.Audio.Music = Music;
        s.Audio.Sfx = Sfx;
        s.Language = Language;
    }
}
