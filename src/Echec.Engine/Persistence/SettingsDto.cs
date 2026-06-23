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
    public bool Fullscreen { get; set; }
    public bool Borderless { get; set; }
    public int Master { get; set; } = 80;
    public int Music { get; set; } = 80;
    public int Sfx { get; set; } = 80;

    public static SettingsDto From(GameSettings s) => new()
    {
        Width = s.Display.Width,
        Height = s.Display.Height,
        Fullscreen = s.Display.Fullscreen,
        Borderless = s.Display.Borderless,
        Master = s.Audio.Master,
        Music = s.Audio.Music,
        Sfx = s.Audio.Sfx,
    };

    public void ApplyTo(GameSettings s)
    {
        s.Display.Width = Width;
        s.Display.Height = Height;
        s.Display.Fullscreen = Fullscreen;
        s.Display.Borderless = Borderless;
        s.Audio.Master = Master;
        s.Audio.Music = Music;
        s.Audio.Sfx = Sfx;
    }
}
