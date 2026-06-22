using Echec.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Engine.Rendering;

/// <summary>
/// Effets de combat shader (<c>Content/Effects/CombatFx.fx</c>) : estafilade d'impact,
/// dissolution d'une unité qui meurt, flash « touché » des survivants. Chaque passe ouvre
/// son propre <see cref="SpriteBatch"/> (état de mélange dédié), à intercaler entre les
/// batchs normaux de la scène.
///
/// Dégradation propre : si le shader n'a pas pu être chargé (<see cref="Enabled"/> = false),
/// la dissolution se rabat sur un simple fondu alpha (l'unité disparaît quand même) et les
/// effets purement décoratifs (slash, flash) sont silencieusement omis.
/// </summary>
public sealed class CombatFxRenderer
{
    private readonly Effect? _effect;

    public CombatFxRenderer(Effect? effect)
    {
        _effect = effect;
    }

    /// <summary>Vrai si le shader est disponible (sinon repli fondu pour la dissolution).</summary>
    public bool Enabled => _effect != null;

    private void Set(string name, float value) => _effect!.Parameters[name]?.SetValue(value);
    private void Set(string name, Vector2 value) => _effect!.Parameters[name]?.SetValue(value);
    private void Set(string name, Vector4 value) => _effect!.Parameters[name]?.SetValue(value);

    /// <summary>
    /// Estafilade diagonale lumineuse balayant la silhouette de <paramref name="sprite"/> (touché),
    /// <paramref name="progress"/> de 0 (avant la case) à 1 (au-delà). Confinée au sprite via son alpha,
    /// mélange ADDITIF → brille par-dessus l'unité sans déborder sur le fond transparent.
    /// </summary>
    public void DrawSlash(SpriteBatch sb, Texture2D sprite, Rectangle rect, float progress, Color color,
        float halfWidth = 0.14f)
    {
        if (_effect == null)
            return;

        Set("Progress", progress);
        Set("SlashColor", ToVec4(color));
        Set("SlashWidth", halfWidth);
        _effect.CurrentTechnique = _effect.Techniques["Slash"];

        sb.Begin(blendState: BlendState.Additive, samplerState: SamplerState.PointClamp, effect: _effect);
        sb.Draw(sprite, rect, Color.White);
        sb.End();
    }

    /// <summary>
    /// Désintègre <paramref name="sprite"/> dans <paramref name="rect"/> : <paramref name="progress"/>
    /// 0 = intact, 1 = entièrement consumé. <paramref name="seed"/> varie le motif à chaque mort.
    /// Sans shader : simple fondu alpha décroissant.
    /// </summary>
    public void DrawDissolve(SpriteBatch sb, Texture2D sprite, Rectangle rect, float progress,
        Color edge, Vector2 seed)
    {
        if (_effect == null)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(sprite, rect, Color.White * (1f - progress));
            sb.End();
            return;
        }

        Set("Progress", progress);
        Set("DissolveEdge", ToVec4(edge));
        Set("DissolveCells", 44f);   // plus de cellules = grain de dissolution plus FIN
        Set("EdgeWidth", 0.14f);
        Set("Seed", seed);
        _effect.CurrentTechnique = _effect.Techniques["Dissolve"];

        sb.Begin(samplerState: SamplerState.PointClamp, effect: _effect);
        sb.Draw(sprite, rect, Color.White);
        sb.End();
    }

    /// <summary>
    /// Éclaircit la silhouette de <paramref name="sprite"/> (réaction « touché ») par
    /// <paramref name="intensity"/> [0,1]. Mélange ADDITIF, par-dessus le sprite déjà dessiné.
    /// </summary>
    public void DrawFlash(SpriteBatch sb, Texture2D sprite, Rectangle rect, float intensity, Color color)
    {
        if (_effect == null || intensity <= 0f)
            return;

        Set("Intensity", intensity);
        Set("FlashColor", ToVec4(color));
        _effect.CurrentTechnique = _effect.Techniques["Flash"];

        sb.Begin(blendState: BlendState.Additive, samplerState: SamplerState.PointClamp, effect: _effect);
        sb.Draw(sprite, rect, Color.White);
        sb.End();
    }

    private static Vector4 ToVec4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
}
