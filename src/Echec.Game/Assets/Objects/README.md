# Objets de plateau

PNG des objets posés sur le plateau (calque `objects` des maps, cf. `Assets/Maps/*.json`).

| Fichier | Objet | Clé map | Affiché ? |
|---------|-------|---------|-----------|
| `coffre.png` | Coffre commun (sans clé) | `C` | ✅ |
| `<Nom>_front.png` | Recrutement (plusieurs looks possibles) | `R` | ✅ |
| `buisson.png` | Buisson (couvert, −4 dégâts reçus) | `B` | ✅ |
| `coffre_cle.png` | Coffre à clé (rare) | `K` | à venir |
| `cle.png` | Clé | `k` | à venir |

Taille : **64×64** — rendu sur la surface de la case en **carré** (jamais déformé / étiré),
exactement comme un sprite d'unité. Cadre ton art avec sa transparence dans ce 64×64.

## Objet recrutement : plusieurs looks

L'objet recrue (`R`) peut prendre **plusieurs apparences** : dépose autant de
`<Nom>_front.png` que tu veux dans ce dossier (p. ex. `Paysan_front.png`, `Mage_front.png`).
Chaque case recrue en tire **une au hasard mais STABLE** (même map = même look à chaque
partie ; deux cases voisines montrent des looks différents). Aucun `*_front.png` présent →
placeholder « ? » dessiné.

**Face / dos selon la mission** : par défaut c'est le `_front` (le jeton regarde la caméra).
En mission **« Protéger les paysans »**, c'est le `<Nom>_back.png` du même nom qui est pris
(le paysan fait alors face comme une unité alliée, vers l'ennemi) ; sans `_back`, on retombe
sur le `_front`.

## Associer ton PNG

Dépose simplement le fichier au **nom exact** ci-dessus dans ce dossier
(`src/Echec.Game/Assets/Objects/`) : il est copié à côté de l'exe au build et chargé
automatiquement (aucun code à toucher). Relance le jeu pour le voir.

## Repli automatique

Tant que le PNG n'existe pas, le jeu dessine un **placeholder** : coffre brun (couvercle clair +
serrure), pion-jeton avec un « ? » jaune pour le recrutement, touffe verte pour le buisson. Tu peux
donc ajouter l'art progressivement.

> Pour l'instant ces objets sont de simples PNG. L'**animation** (ouverture du coffre, etc.) viendra plus tard.
