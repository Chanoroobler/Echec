// ─────────────────────────────────────────────────────────────────────────────
// CombatFx.fx — feedback visuel des attaques.
//   • Dissolve : désintégration pixel-art du sprite d'une unité qui meurt, par seuil
//                de bruit chunky + liseré incandescent. Le sprite est la texture liée.
//   • Flash    : silhouette du sprite éclaircie (réaction « touché » au contact),
//                mélange ADDITIF.
//   • Burn     : mort par MAGIE, le sprite brûle du bas vers le haut (bandes de feu + calcination).
// (Le feedback d'impact lui-même = étincelles en particules + knockback, gérés côté C#.)
//
// Comme les sprites sont chargés en alpha DROIT (Texture2D.FromStream) puis dessinés
// par SpriteBatch en AlphaBlend, ces passes sortent elles aussi en alpha droit pour
// rester cohérentes avec le rendu normal des unités (pas de prémultiplication).
// ─────────────────────────────────────────────────────────────────────────────

#if OPENGL
    #define PS_SHADERMODEL ps_3_0
#else
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float  Progress;       // avancement de l'effet [0,1] (sens propre à chaque passe)
float  Intensity;      // intensité du flash [0,1]

// Pixel-art : on quantifie en ESPACE CANVAS (pas en UV du quad) pour aligner les blocs sur la
// grille de pixels du jeu, comme le shader d'eau. DestRect = rectangle canvas couvert (xy coin,
// zw taille) ; PixelSize = côté d'un bloc en pixels canvas.
float4 DestRect;
float  PixelSize;

// ── Dissolve ──
float4 DissolveEdge;   // teinte du liseré incandescent (braise)
float  DissolveCells;  // résolution de la grille de bruit (chunky → pixel-art)
float  EdgeWidth;      // épaisseur du liseré de combustion, en [0,1] de bruit
float2 Seed;           // graine par mort (chaque dissolution diffère)

// ── Burn ── (réutilise Progress, DissolveCells, Seed ; DissolveEdge = bande ORANGE)
float4 BurnHot;        // bande la plus chaude, collée au front (jaune)
float4 BurnChar;       // teinte de calcination (le pion noircit au-dessus du front)

// ── Flash ──
float4 FlashColor;     // teinte du flash (crème claire)

texture SpriteTexture;
sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };

// Bruit de valeur sur une grille (hash 2D → valeur lissée).
float Hash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// Ramène un uv au CENTRE de son bloc de la grille de pixels canvas (aligné à l'écran, taille
// PixelSize) → blocs nets façon pixel-art, cohérents avec le reste du jeu, quel que soit le zoom.
float2 Pixelate(float2 uv)
{
    float2 canvasPos = DestRect.xy + uv * DestRect.zw;
    float2 q = (floor(canvasPos / PixelSize) + 0.5) * PixelSize;
    return (q - DestRect.xy) / DestRect.zw;
}

// ── Dissolve ─────────────────────────────────────────────────────────────────────
float4 DissolvePS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(SpriteSampler, uv);

    float2 cell  = floor(uv * DissolveCells);
    float  noise = Hash(cell + Seed);

    // Dissipation VERS LE HAUT : on mélange le grain de bruit avec un gradient vertical (petit en
    // bas, grand en haut) → le front de dissolution balaie du bas vers le haut (0.55 = part de grain
    // vs balayage directionnel). Le seuil dépasse 1 pour effacer jusqu'au dernier pixel du sommet.
    float field = noise * 0.55 + (1.0 - uv.y) * 0.45;
    float th    = Progress * 1.05;

    clip(field - th);                             // sous le seuil : pixel désintégré

    // Liseré incandescent juste au-dessus du seuil (le front qui se consume, en montant).
    float edge = 1.0 - smoothstep(th, th + EdgeWidth, field);
    float3 rgb = lerp(tex.rgb, DissolveEdge.rgb, edge);

    // Sortie PRÉMULTIPLIÉE (rgb * a) : sur un pixel transparent (a=0) la couleur du liseré ne
    // peut pas baver sur le fond (le blend prémultiplié ajoute sinon la couleur source telle quelle).
    return float4(rgb * tex.a, tex.a);
}

// ── Burn ──────────────────────────────────────────────────────────────────────────
// Mort par MAGIE : le pion BRÛLE du bas vers le haut. Front de combustion plus directionnel que la
// dissolution (moins de grain) ; juste au-dessus du front, des bandes FRANCHES (pixel-art, pas de dégradé) :
// jaune → orange → calciné, et tout le pion s'assombrit à mesure qu'il se consume.
float4 BurnPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(SpriteSampler, uv);

    float2 cell  = floor(uv * DissolveCells);
    float  noise = Hash(cell + Seed);
    float field = noise * 0.30 + (1.0 - uv.y) * 0.70;   // monte du bas, bord irrégulier comme une flamme
    float th    = Progress * 1.08 - 0.04;

    clip(field - th);                                   // sous le front : consumé

    float d = field - th;                               // distance au-dessus du front
    float hot  = 1.0 - step(0.05, d);                   // bande jaune, collée au front
    float warm = (1.0 - step(0.11, d)) - hot;           // bande orange au-dessus
    float burnt = (1.0 - step(0.22, d)) - hot - warm;   // zone calcinée au-dessus

    // Noircissement global progressif (le pion « cuit ») puis bandes de feu par-dessus.
    float3 rgb = tex.rgb * (1.0 - 0.45 * saturate(Progress * 1.6));
    rgb = lerp(rgb, BurnChar.rgb, burnt * 0.85);
    rgb = lerp(rgb, DissolveEdge.rgb, warm);
    rgb = lerp(rgb, BurnHot.rgb, hot);

    return float4(rgb * tex.a, tex.a);                  // prémultiplié (cf. DissolvePS)
}

// ── Flash ─────────────────────────────────────────────────────────────────────────
// Silhouette DURE (seuil sur l'alpha → bords nets, pas d'anti-aliasing) et intensité POSTÉRISÉE
// → clignotement par paliers francs plutôt qu'un fondu lisse (rendu pixel-art).
float4 FlashPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    // Échantillonne la silhouette au centre du bloc canvas → flash en blocs alignés à la grille.
    float2 quv = Pixelate(uv);
    float sil = step(0.5, tex2D(SpriteSampler, quv).a);
    float a = floor(sil * Intensity * 4.0 + 0.5) / 4.0;
    return float4(FlashColor.rgb * a, a);         // additif : éclaircit la silhouette
}

technique Dissolve { pass P0 { PixelShader = compile PS_SHADERMODEL DissolvePS(); } }
technique Flash    { pass P0 { PixelShader = compile PS_SHADERMODEL FlashPS();    } }
technique Burn     { pass P0 { PixelShader = compile PS_SHADERMODEL BurnPS();     } }
