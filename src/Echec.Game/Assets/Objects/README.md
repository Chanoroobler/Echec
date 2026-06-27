# Objets de plateau

PNG des objets posés sur le plateau (calque `objects` des maps, cf. `Assets/Maps/*.json`).

| Fichier | Objet | Clé map | Affiché ? |
|---------|-------|---------|-----------|
| `coffre.png` | Coffre commun (sans clé) | `C` | ✅ |
| `coffre_cle.png` | Coffre à clé (rare) | `K` | à venir |
| `cle.png` | Clé | `k` | à venir |

Taille : **64×64** — rendu sur la surface de la case en **carré** (jamais déformé / étiré),
exactement comme un sprite d'unité. Cadre ton art avec sa transparence dans ce 64×64.

## Repli automatique

Tant que `coffre.png` n'existe pas, le jeu dessine un **placeholder** (coffre brun + couvercle clair
+ serrure dorée). Tu peux donc ajouter l'art progressivement.

> Pour l'instant le coffre est un simple PNG. L'**animation** d'ouverture viendra plus tard.
