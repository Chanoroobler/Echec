using System;
using System.IO;
using ChessArmy.Engine;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Écran du logo du studio, joué au lancement AVANT le menu principal, sur le dégradé du menu. Le logo grossit (zoom), puis
/// redescend à sa taille native en accélérant comme s'il tombait sur la page (dézoom) ; à l'impact,
/// petit son (clé <c>logo_land</c> de sounds.json) + secousse, courte pause, fondu au noir, menu.
/// Le zoom est un effet visuel délibéré : au repos le logo est dessiné à sa taille NATIVE, sur un pixel
/// entier (pixel-perfect). N'importe quelle touche / clic / bouton passe directement au fondu de sortie.
/// </summary>
public sealed class SplashScene : Scene
{
    private const string LogoPath = "Assets/UI/ChaNoStudioLogo.png";

    // Chronologie (secondes).
    private const float ZoomTime = 0.7f;     // apparition + zoom jusqu'à ZoomPeak
    private const float DropTime = 0.35f;    // dézoom accéléré jusqu'à la taille native (= l'atterrissage)
    private const float HoldTime = 1.1f;     // pause sur le logo posé
    private const float FadeTime = 0.45f;    // fondu au noir avant le menu
    private const float StartScale = 0.3f;
    private const float ZoomPeak = 1.5f;

    // Secousse à l'impact : amplitude en pixels canvas, amortie sur ShakeTime.
    private const float ShakeTime = 0.25f;
    private const float ShakeAmp = 4f;

    private Texture2D? _logo;
    private MenuBackdrop _backdrop = null!;   // même fond que le menu principal, sur tout l'écran
    private float _time;
    private bool _landed;
    private float _fadeStart = -1f;   // instant où le fondu de sortie a commencé (-1 = pas encore)
    private bool _done;

    private static float LandTime => ZoomTime + DropTime;

    public SplashScene(GameContext context) : base(context) { }

    public override void Load()
    {
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        _logo = Textures.LoadPngOrNull(Context.GraphicsDevice, Path.Combine(AppContext.BaseDirectory, LogoPath));
        if (_logo == null)
            _done = true;   // logo absent : on file directement au menu
    }

    public override void Unload()
    {
        _logo?.Dispose();
        _logo = null;
        _backdrop.Dispose();
    }

    public override void Update(GameTime gameTime)
    {
        if (_done)
        {
            Context.Scenes.Change(new MainMenuScene(Context));
            return;
        }

        _time += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (!_landed && _time >= LandTime)
        {
            _landed = true;
            Context.Sounds.Play("logo_land");
        }

        if (_fadeStart < 0f && (_time >= LandTime + HoldTime || SkipPressed()))
            _fadeStart = _time;

        if (_fadeStart >= 0f && _time - _fadeStart >= FadeTime)
            _done = true;
    }

    private bool SkipPressed()
    {
        var input = Context.Input;
        return input.WasKeyPressed(Keys.Escape) || input.WasKeyPressed(Keys.Enter) || input.WasKeyPressed(Keys.Space)
            || input.WasLeftClicked || input.WasConfirmPressed || input.WasCancelPressed || input.WasMenuPressed;
    }

    /// <summary>Échelle du logo à l'instant courant : zoom (décéléré) puis chute (accélérée) jusqu'à 1.</summary>
    private float LogoScale()
    {
        if (_time < ZoomTime)
        {
            var t = _time / ZoomTime;
            var e = 1f - MathF.Pow(1f - t, 3f);   // ease-out : jaillit puis ralentit au sommet
            return MathHelper.Lerp(StartScale, ZoomPeak, e);
        }
        if (_time < LandTime)
        {
            var t = (_time - ZoomTime) / DropTime;
            return MathHelper.Lerp(ZoomPeak, 1f, t * t);   // ease-in : accélère comme une chute
        }
        return 1f;
    }

    /// <summary>Opacité du voile noir de sortie (0 tant que le fondu n'a pas commencé).</summary>
    private float FadeAlpha() =>
        _fadeStart < 0f ? 0f : MathHelper.Clamp((_time - _fadeStart) / FadeTime, 0f, 1f);

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var res = Context.VirtualResolution;
        var screen = new Rectangle(0, 0, res.X, res.Y);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, res.X, res.Y);

        if (_logo != null)
        {
            var shake = Point.Zero;
            var sinceLand = _time - LandTime;
            if (_landed && sinceLand < ShakeTime)
            {
                var amp = ShakeAmp * (1f - sinceLand / ShakeTime);
                shake = new Point((int)MathF.Round(MathF.Sin(sinceLand * 90f) * amp),
                                  (int)MathF.Round(MathF.Cos(sinceLand * 70f) * amp));
            }

            var alpha = MathHelper.Clamp(_time / 0.25f, 0f, 1f);
            var scale = LogoScale();
            if (scale == 1f)
            {
                // Posé : taille native, coin sur un pixel entier → net.
                var pos = new Vector2((res.X - _logo.Width) / 2 + shake.X, (res.Y - _logo.Height) / 2 + shake.Y);
                sb.Draw(_logo, pos, Color.White * alpha);
            }
            else
            {
                var center = new Vector2(res.X / 2f, res.Y / 2f);
                var origin = new Vector2(_logo.Width / 2f, _logo.Height / 2f);
                sb.Draw(_logo, center, null, Color.White * alpha, 0f, origin, scale, SpriteEffects.None, 0f);
            }
        }

        if (FadeAlpha() is var fade and > 0f)
            sb.Draw(Context.Pixel, screen, Color.Black * fade);
        sb.End();
    }

    /// <summary>Bandes du letterbox (écran non 16:9) : le dégradé continue sur tout l'écran, fondu compris.</summary>
    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution,
            Color.Black * FadeAlpha());
}
