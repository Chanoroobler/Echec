// ─────────────────────────────────────────────────────────────────────────────
// CombatFx.fx — feedback visuel des attaques au corps à corps.
//   • Slash    : estafilade diagonale lumineuse (╱) qui balaie une case. Dessinée
//                sur le pixel blanc plein → couleur procédurale, mélange ADDITIF.
//   • Dissolve : désintégration pixel-art du sprite d'une unité qui meurt, par seuil
//                de bruit chunky + liseré incandescent. Le sprite est la texture liée.
//   • Flash    : silhouette du sprite éclaircie (réaction « touché » des survivants),
//                mélange ADDITIF.
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

// ── Slash ──
float4 SlashColor;     // teinte de l'estafilade (crème lumineuse)
float  SlashWidth;     // demi-largeur de la lame, en fraction de case

// ── Dissolve ──
float4 DissolveEdge;   // teinte du liseré incandescent (braise)
float  DissolveCells;  // résolution de la grille de bruit (chunky → pixel-art)
float  EdgeWidth;      // épaisseur du liseré de combustion, en [0,1] de bruit
float2 Seed;           // graine par mort (chaque dissolution diffère)

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

// ── Slash ──────────────────────────────────────────────────────────────────────
// Diagonale ╱ : la droite x + y = const ; on fait glisser sa position de coupe avec
// Progress, en tapant les extrémités pour obtenir un coup de lame plutôt qu'un voile.
// CONFINÉE à la silhouette : multipliée par l'alpha du sprite (la texture liée = la
// victime) → l'estafilade ne déborde pas sur le fond transparent de la case.
float4 SlashPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float silhouette = tex2D(SpriteSampler, uv).a;

    float p      = (uv.x + uv.y) * 0.5;          // 0 (coin haut-gauche) … 1 (coin bas-droit)
    float center = lerp(-0.15, 1.15, Progress);  // la coupe traverse la case
    float band   = smoothstep(SlashWidth, 0.0, abs(p - center));

    float along = uv.x + uv.y - 1.0;             // axe le long de la lame [-1,1]
    float taper = 1.0 - smoothstep(0.65, 1.0, abs(along));

    float env = sin(Progress * 3.14159265);      // apparition / disparition douce
    float a   = saturate(band * taper * env) * silhouette;

    return float4(SlashColor.rgb * a, a);        // additif, confiné au sprite
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

// ── Flash ─────────────────────────────────────────────────────────────────────────
float4 FlashPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float a = tex2D(SpriteSampler, uv).a * Intensity;
    return float4(FlashColor.rgb * a, a);         // additif : éclaircit la silhouette
}

technique Slash    { pass P0 { PixelShader = compile PS_SHADERMODEL SlashPS();    } }
technique Dissolve { pass P0 { PixelShader = compile PS_SHADERMODEL DissolvePS(); } }
technique Flash    { pass P0 { PixelShader = compile PS_SHADERMODEL FlashPS();    } }
