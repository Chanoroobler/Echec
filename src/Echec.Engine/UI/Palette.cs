using Microsoft.Xna.Framework;

namespace Echec.Engine.UI;

/// <summary>
/// Palette de couleurs du jeu. TOUTES les couleurs du projet doivent venir d'ici.
/// Aucune couleur en dur ailleurs dans le code.
///
/// Palette maîtresse de 20 teintes (froide, pixel-art). Les NOMS de membres
/// historiques (Black1.., Brown1.., Purple1.., Cyan1..) sont conservés pour ne pas
/// toucher les appelants : ils sont remappés par RÔLE sur les 20 couleurs ci-dessous
/// (d'où quelques doublons quand deux rôles tombent sur la même teinte).
/// </summary>
public static class Palette
{
    // ── Les 20 couleurs de la palette ───────────────────────────────────────────
    // Bleus / sarcelles (eau)        Verts                     Crèmes / bruns chauds      Rouges
    //   #699fad clair                  #151d1a très sombre        #ede6cb crème              #ff8b85 corail clair
    //   #3a708e moyen                  #1d3230 sombre             #f5d893 jaune clair        #e65f55 rouge
    //   #2b454f ardoise                #314e3f moyen              #e8b26f tan                #a3412b rouge-orangé
    // Noirs                            #4f5d42 olive              #b6834c brun               #8a2c36 cramoisi
    //   #111215 froid                  #9a9f87 sauge claire       #704d2b brun foncé
    //   #151015 chaud                                             #40231e brun très foncé

    // ── Noirs / fonds (froids) ──────────────────────────────────────────────────
    public static readonly Color Black1 = Hex("#111215"); // le plus sombre : contours, voile, fonds de jauge
    public static readonly Color Black2 = Hex("#151015"); // ton sombre du tramage
    public static readonly Color Black3 = Hex("#151d1a"); // ton clair du tramage (texture des panneaux)
    public static readonly Color Black4 = Hex("#1d3230");
    public static readonly Color Black5 = Hex("#2b454f"); // arête éclairée des biseaux d'UI

    // ── Bruns chauds ────────────────────────────────────────────────────────────
    public static readonly Color Brown1 = Hex("#704d2b");
    public static readonly Color Brown2 = Hex("#b6834c");
    public static readonly Color Brown3 = Hex("#e8b26f"); // texte de dégâts (chaud)
    public static readonly Color Brown4 = Hex("#f5d893");
    public static readonly Color Brown5 = Hex("#ff8b85");

    // ── Rouges / danger (anciennement « pourpres ») ─────────────────────────────
    public static readonly Color Purple1 = Hex("#40231e");
    public static readonly Color Purple2 = Hex("#8a2c36");
    public static readonly Color Purple3 = Hex("#a3412b");
    public static readonly Color Purple4 = Hex("#a3412b");
    public static readonly Color Purple5 = Hex("#e65f55"); // ennemi / cible de tir / défaite

    // ── Oranges / jaunes ────────────────────────────────────────────────────────
    public static readonly Color Orange1 = Hex("#e8b26f");
    public static readonly Color Yellow1 = Hex("#e8b26f"); // or : liseré commandant, titres
    public static readonly Color Yellow2 = Hex("#f5d893"); // surbrillance vive : sélection, déplacement, victoire
    public static readonly Color Yellow3 = Hex("#9a9f87");
    public static readonly Color Yellow4 = Hex("#4f5d42");

    // ── Verts ───────────────────────────────────────────────────────────────────
    public static readonly Color Green1 = Hex("#4f5d42"); // jauge de PV, zone de déploiement
    public static readonly Color Green2 = Hex("#314e3f");
    public static readonly Color Green3 = Hex("#151d1a");
    public static readonly Color Green4 = Hex("#1d3230");
    public static readonly Color Green5 = Hex("#314e3f");

    // ── Bleus / sarcelles ───────────────────────────────────────────────────────
    public static readonly Color Cyan1 = Hex("#699fad"); // camp joueur, tour du joueur
    public static readonly Color Cyan2 = Hex("#699fad"); // accents positifs / infos
    public static readonly Color White = Hex("#ede6cb"); // texte clair (crème, pas de blanc pur)
    public static readonly Color Blue1 = Hex("#9a9f87"); // libellés discrets
    public static readonly Color Blue2 = Hex("#2b454f"); // traits (visualiseur d'arbres)

    // ── Bleus foncés (panneaux) ─────────────────────────────────────────────────
    public static readonly Color Navy1 = Hex("#2b454f"); // bord de panneau (plus clair)
    public static readonly Color Navy2 = Hex("#151d1a"); // fond de panneau (sombre)

    // ── Gris (jauges : segment vide) ────────────────────────────────────────────
    public static readonly Color Grey = Hex("#9a9f87");

    // ── Eau (fond animé pixel-art) ──────────────────────────────────────────────
    // Rampe du plus profond au plus clair : le shader postérise le courant sur ces
    // 4 tons FRANCS (aucune interpolation hors palette).
    public static readonly Color WaterDeep    = Hex("#1d3230");
    public static readonly Color WaterMid1     = Hex("#2b454f");
    public static readonly Color WaterMid2     = Hex("#3a708e");
    public static readonly Color WaterShallow = Hex("#699fad");
    public static readonly Color WaterShadow  = Hex("#111215"); // ombre portée du plateau sur l'eau

    private static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        var r = System.Convert.ToByte(hex[0..2], 16);
        var g = System.Convert.ToByte(hex[2..4], 16);
        var b = System.Convert.ToByte(hex[4..6], 16);
        return new Color(r, g, b);
    }
}
