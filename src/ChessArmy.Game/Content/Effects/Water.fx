// ─────────────────────────────────────────────────────────────────────────────
// Water.fx — eau animée (technique Water) + ombre dégradée autour du plateau (Shadow).
// Porté de CosyFarmer. Pixel shaders compatibles SpriteBatch (pas de vertex shader →
// SpriteBatch fournit le sien). Tous deux dessinés sur un quad plein écran. La texture
// échantillonnée est TOUJOURS celle liée par SpriteBatch au slot 0 :
//   • Water  : on dessine la texture de BRUIT    → courant ancré au repère canvas (2 couches).
//   • Shadow : on dessine le MASQUE de silhouette → ombre douce en dégradé autour du plateau.
//
// Différence avec CosyFarmer : le courant n'est PAS interpolé entre deux couleurs (ce qui
// produirait des teintes hors palette). Il est POSTÉRISÉ sur une RAMPE de 4 tons FRANCS,
// tous issus de la palette du jeu → chaque pixel d'eau est une couleur exacte de la palette.
//
// Sorties en alpha PRÉMULTIPLIÉ (MonoGame BlendState.AlphaBlend = premult).
// ─────────────────────────────────────────────────────────────────────────────

#if OPENGL
    #define PS_SHADERMODEL ps_3_0
#else
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float  Time;
float4 WorldRect;    // xy = coin canvas haut-gauche couvert par l'écran, zw = taille canvas

// ── Eau ──
float  NoiseScale;
float2 ScrollA;
float2 ScrollB;
float  WaterPixel;   // taille d'un pixel d'eau, en unités canvas (chunky → pixel art)

// Rampe de 4 tons d'eau, du plus profond (0) au plus clair (3). Tons francs de la palette.
float4 Ramp0;
float4 Ramp1;
float4 Ramp2;
float4 Ramp3;

// ── Ombre ──
float2 ShadowRadius;   // rayon du dégradé d'ombre, en uv écran (≈ px / résolution)
float  ShadowStrength; // opacité max de l'ombre
float4 ShadowColor;    // teinte de l'ombre (sombre)

// Texture liée par SpriteBatch au slot 0 (bruit pour Water, masque pour Shadow).
texture SpriteTexture;
sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };

// Choisit un ton de la rampe selon le courant [0,1] : 4 paliers francs, sans interpolation.
float4 PickRamp(float t)
{
    if (t < 0.25) return Ramp0;
    if (t < 0.50) return Ramp1;
    if (t < 0.75) return Ramp2;
    return Ramp3;
}

// ── Eau ──────────────────────────────────────────────────────────────────────
float4 WaterPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float2 worldPos = WorldRect.xy + uv * WorldRect.zw;
    // Quantification sur la grille d'art-pixels alignée au canvas → eau chunky.
    float2 qWorld = floor(worldPos / WaterPixel) * WaterPixel;
    float2 baseUv = qWorld * NoiseScale;

    float n1 = tex2D(SpriteSampler, baseUv + Time * ScrollA).r;
    float n2 = tex2D(SpriteSampler, baseUv * 1.7 + Time * ScrollB).r;
    float current = saturate(n1 * 0.6 + n2 * 0.4);
    current = saturate((current - 0.5) * 1.8 + 0.5);

    return float4(PickRamp(current).rgb, 1.0);
}

// ── Ombre ────────────────────────────────────────────────────────────────────
// Masque échantillonné en LINÉAIRE (sampler imposé par Begin) → bords lisses, dégradé doux.
float Mask(float2 uv) { return tex2D(SpriteSampler, uv).r; }

// Occlusion floutée : moyenne de la présence du plateau sur un petit disque (2 anneaux + centre).
float Occlusion(float2 uv)
{
    float2 r1 = ShadowRadius;
    float2 r2 = ShadowRadius * 0.55;
    float2 d1 = r1 * 0.70710678;
    float2 d2 = r2 * 0.70710678;

    float s = Mask(uv);
    s += Mask(uv + float2( r1.x, 0)) + Mask(uv + float2(-r1.x, 0));
    s += Mask(uv + float2( 0, r1.y)) + Mask(uv + float2( 0,-r1.y));
    s += Mask(uv + float2( d1.x, d1.y)) + Mask(uv + float2(-d1.x, d1.y));
    s += Mask(uv + float2( d1.x,-d1.y)) + Mask(uv + float2(-d1.x,-d1.y));
    s += Mask(uv + float2( r2.x, 0)) + Mask(uv + float2(-r2.x, 0));
    s += Mask(uv + float2( 0, r2.y)) + Mask(uv + float2( 0,-r2.y));
    s += Mask(uv + float2( d2.x, d2.y)) + Mask(uv + float2(-d2.x, d2.y));
    s += Mask(uv + float2( d2.x,-d2.y)) + Mask(uv + float2(-d2.x,-d2.y));
    return s / 17.0;
}

float4 ShadowPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float here = Mask(uv);
    float occ  = Occlusion(uv);

    // Ombre CÔTÉ EAU : forte juste au bord du plateau (occ élevé, here ~0), s'estompe au large.
    // Sous le plateau (here ~1) elle s'annule (et le plateau sera dessiné par-dessus de toute façon).
    float shadow = saturate((occ - here) * 1.6);

    float a = shadow * ShadowStrength;
    return float4(ShadowColor.rgb * a, a);   // prémultiplié
}

technique Water  { pass P0 { PixelShader = compile PS_SHADERMODEL WaterPS(); } }
technique Shadow { pass P0 { PixelShader = compile PS_SHADERMODEL ShadowPS(); } }
