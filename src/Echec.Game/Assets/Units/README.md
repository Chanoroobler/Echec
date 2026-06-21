# Sprites d'unités

PNG **64×64** par classe. L'id d'asset (et les stats) se définit dans
`src/Echec.Game/Assets/Config/units.json` (champ `"asset"` de chaque classe).

## Variantes par camp et orientation

Pour un asset `soldat`, on attend jusqu'à 4 fichiers :

| Fichier | Camp | Orientation | Affiché en jeu ? |
|---------|------|-------------|------------------|
| `soldat_back.png` | Joueur | dos | ✅ (joueur) |
| `soldat_front.png` | Joueur | face | réservé (futur) |
| `soldat_ia_front.png` | IA | face | ✅ (IA) |
| `soldat_ia_back.png` | IA | dos | réservé (futur) |

En jeu : **le joueur affiche le `_back`**, **l'IA affiche le `_ia_front`**.

## Repli automatique

Pour chaque unité, l'ordre de recherche est :
1. la variante du camp (`<asset>_back` joueur / `<asset>_ia_front` IA) ;
2. sinon le PNG simple `<asset>.png` ;
3. sinon le **placeholder** (jeton coloré + initiale du domaine).

Tu peux donc ajouter les sprites progressivement. Un **liseré de camp**
(cyan = joueur, rouge = ennemi) est ajouté automatiquement autour du sprite,
avec la barre de PV en dessous.
