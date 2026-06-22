using Echec.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.Rendering;

/// <summary>
/// Rend l'eau animée pixel-art DERRIÈRE le plateau, via le shader
/// <c>Content/Effects/Water.fx</c>. Porté de CosyFarmer, simplifié pour Echec :
/// pas de caméra (le courant est ancré au repère canvas) et le « masque » d'île est
/// simplement le rectangle du plateau.
///
/// Deux temps par frame :
///  1. <see cref="BuildMask"/> — dessine la silhouette du plateau (en BLANC) dans un
///     render target masque. Le shader s'en sert pour faire une ombre douce au pied du plateau.
///  2. <see cref="DrawWater"/> / <see cref="DrawShadow"/> — peignent un quad plein écran :
///     courant postérisé sur une rampe de tons francs, puis frange d'ombre.
///
/// Dégradation propre : si le shader n'a pas pu être chargé (<see cref="Enabled"/> = false),
/// l'appelant se rabat sur un fond uni — le jeu tourne quand même.
/// </summary>
public sealed class WaterRenderer
{
    private readonly GraphicsDevice _device;
    private readonly Effect? _effect;
    private readonly Texture2D _noise;
    private readonly Texture2D _pixel;

    private RenderTarget2D? _mask;
    private int _maskW, _maskH;

    // L'ombre du plateau sur l'eau est STATIQUE (ne dépend que de la position du plateau, pas du
    // temps) → on la calcule une fois dans ce cache et on la blit chaque frame, au lieu de refaire
    // le flou 17 taps par pixel. Rebuild seulement quand le rectangle du plateau (ou la taille) change.
    private RenderTarget2D? _shadowCache;
    private Rectangle _shadowBoard = new(-1, -1, 0, 0);

    public WaterRenderer(GraphicsDevice device, Effect? effect, Texture2D noise, Texture2D pixel)
    {
        _device = device;
        _effect = effect;
        _noise = noise;
        _pixel = pixel;

        if (_effect != null)
            ConfigureConstants();
    }

    /// <summary>Vrai si le shader est disponible (sinon l'appelant doit dessiner un fond uni).</summary>
    public bool Enabled => _effect != null;

    /// <summary>Teinte de repli (eau profonde) si le shader est indisponible.</summary>
    public static Color FallbackColor => Palette.WaterMid1;

    private void Set(string name, float value) => _effect!.Parameters[name]?.SetValue(value);
    private void Set(string name, Vector2 value) => _effect!.Parameters[name]?.SetValue(value);
    private void Set(string name, Vector4 value) => _effect!.Parameters[name]?.SetValue(value);

    /// <summary>Rayon de l'ombre, en pixels canvas (converti en uv selon la résolution).</summary>
    private const float ShadowRadiusPx = 18f;

    /// <summary>Valeurs d'aspect fixes (couleurs + réglages de courant/ombre), posées une fois.</summary>
    private void ConfigureConstants()
    {
        // Courant (pixel art : grille d'art-pixels + postérisation en tons francs).
        Set("NoiseScale", 0.0060f);
        Set("ScrollA", new Vector2(0.021f, 0.012f));
        Set("ScrollB", new Vector2(-0.014f, 0.019f));
        Set("WaterPixel", 3f);    // taille d'un pixel d'eau en unités canvas

        // Rampe de 4 tons francs (du plus profond au plus clair) — toutes de la palette.
        Set("Ramp0", ToVec4(Palette.WaterDeep));
        Set("Ramp1", ToVec4(Palette.WaterMid1));
        Set("Ramp2", ToVec4(Palette.WaterMid2));
        Set("Ramp3", ToVec4(Palette.WaterShallow));

        // Ombre dégradée autour du plateau.
        Set("ShadowStrength", 0.82f);
        Set("ShadowColor", ToVec4(Palette.WaterShadow));
    }

    private void EnsureTargets(int width, int height)
    {
        if (_mask != null && _maskW == width && _maskH == height) return;
        _mask?.Dispose();
        _shadowCache?.Dispose();
        _mask = new RenderTarget2D(_device, width, height);
        _shadowCache = new RenderTarget2D(_device, width, height);
        _maskW = width;
        _maskH = height;
        _shadowBoard = new Rectangle(-1, -1, 0, 0);   // taille changée → forcer le recalcul du cache
    }

    /// <summary>
    /// Peint l'eau plein canvas (à appeler AVANT le plateau). <paramref name="width"/>/
    /// <paramref name="height"/> = taille du canvas ; le courant est ancré à ce repère.
    /// </summary>
    public void DrawWater(SpriteBatch spriteBatch, float time, int width, int height)
        => DrawWaterRect(spriteBatch, time, new Rectangle(0, 0, width, height),
            Vector2.Zero, new Vector2(width, height));

    /// <summary>
    /// Peint l'eau sur un rectangle écran arbitraire avec un mapping écran→repère EXPLICITE
    /// (<paramref name="worldMin"/>/<paramref name="worldSize"/>). Sert à étendre l'eau sur le
    /// backbuffer réel (bandes du letterbox) en raccordant le repère à celui du canvas → le
    /// courant est continu de part et d'autre de la bordure du canvas.
    /// </summary>
    public void DrawWaterRect(SpriteBatch spriteBatch, float time, Rectangle dest,
        Vector2 worldMin, Vector2 worldSize)
    {
        if (_effect == null) return;
        Set("Time", time);
        Set("WorldRect", new Vector4(worldMin.X, worldMin.Y, worldSize.X, worldSize.Y));
        _effect.CurrentTechnique = _effect.Techniques["Water"];

        // La texture de bruit EST la texture du SpriteBatch (slot 0). PointWrap : bruit net et répété.
        spriteBatch.Begin(samplerState: SamplerState.PointWrap, effect: _effect);
        spriteBatch.Draw(_noise, dest, Color.White);
        spriteBatch.End();
    }

    /// <summary>
    /// Peint l'ombre dégradée autour du plateau, EN SURIMPRESSION sur l'eau (à appeler entre l'eau
    /// et le plateau). <paramref name="board"/> = rectangle du plateau en coordonnées canvas.
    /// L'ombre étant statique, elle est calculée une fois dans un cache (flou 17 taps) puis simplement
    /// blittée chaque frame ; recalculée seulement si le plateau ou la taille du canvas change.
    /// </summary>
    public void DrawShadow(SpriteBatch spriteBatch, Rectangle board, int width, int height)
    {
        if (_effect == null) return;
        EnsureTargets(width, height);

        if (_shadowBoard != board)
        {
            RebuildShadowCache(spriteBatch, board, width, height);
            _shadowBoard = board;
        }

        // Blit du cache statique (aucun shader) sur l'eau animée.
        spriteBatch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.PointClamp);
        spriteBatch.Draw(_shadowCache, new Rectangle(0, 0, width, height), Color.White);
        spriteBatch.End();
    }

    /// <summary>
    /// Recalcule le cache d'ombre : masque silhouette du plateau (blanc) → passe Shadow (flou +
    /// dégradé, masque en LINÉAIRE) écrite en alpha prémultiplié dans <see cref="_shadowCache"/>.
    /// Restaure la render target courante (le canvas virtuel) à la fin.
    /// </summary>
    private void RebuildShadowCache(SpriteBatch spriteBatch, Rectangle board, int width, int height)
    {
        var previous = _device.GetRenderTargets();

        // 1. Masque : silhouette du plateau en blanc sur fond noir.
        _device.SetRenderTarget(_mask);
        _device.Clear(Color.Black);
        spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        spriteBatch.Draw(_pixel, board, Color.White);
        spriteBatch.End();

        // 2. Ombre dégradée dans le cache (transparent) : on capte la sortie prémultipliée telle quelle
        //    (BlendState.Opaque) pour pouvoir la re-blitter ensuite en AlphaBlend sur l'eau.
        Set("ShadowRadius", new Vector2(ShadowRadiusPx / width, ShadowRadiusPx / height));
        _effect!.CurrentTechnique = _effect.Techniques["Shadow"];
        _device.SetRenderTarget(_shadowCache);
        _device.Clear(Color.Transparent);
        spriteBatch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp, effect: _effect);
        spriteBatch.Draw(_mask, new Rectangle(0, 0, width, height), Color.White);
        spriteBatch.End();

        _device.SetRenderTargets(previous);
    }

    public void Dispose()
    {
        _mask?.Dispose();
        _shadowCache?.Dispose();
    }

    private static Vector4 ToVec4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
}
