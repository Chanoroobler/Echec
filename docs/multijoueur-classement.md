# Multijoueur : écran CLASSEMENT

Maquette validée le 2026-11-06. Complète `docs/multijoueur-interface.md` (même habillage, même barre du haut).
Captures (1280×720, échelle 1) dans `docs/maquettes/` :

| Capture | État |
|---|---|
| `multi-7-classement-mondial.png` | Onglet MONDIAL, page 1, ma ligne épinglée en bas |
| `multi-8-classement-amis.png` | Onglet AMIS |
| `multi-9-classement-autour.png` | Onglet AUTOUR DE MOI |

Les pseudos et chiffres des captures sont **simulés**.

## Accès

- Bouton **CLASSEMENT** dans la barre du haut, entre les points et COFFRES (icône coupe pixel 10×10 affichée ×2 = 20×20, puis le libellé). Il est à l'état sélectionné sur cet écran.
- La pastille de rang de la barre du haut affiche désormais l'**icône du rang** (20×24) devant « BRONZE II ».
- RETOUR ramène au build d'armée.

## Icône de rang

- **Une seule icône**, fournie par l'auteur, **source 10×12 px**, recolorée par palier.
- Affichage pixel perfect uniquement : **×2 = 20×24** (barre du haut, listes, paliers, historique) et **×8 = 80×96** (carte MON RANG).
- Dessin à fournir en niveaux de gris : contour noir (`Black1`, reste noir), corps en gris moyen, reflet en blanc. Le jeu la dessine avec la couleur du palier en teinte `SpriteBatch` (multiplication) : le blanc prend la couleur claire du palier, le gris sa couleur de base. Si la teinte simple ne rend pas bien, utiliser deux couches (corps + reflet) teintées séparément.

| Palier | Couleur de base | Couleur claire |
|---|---|---|
| Bronze | `Brown2` #b6834c | `Brown3` #e8b26f |
| Fer | `Grey` #9a9f87 | `White` #ede6cb |
| Argent | `White` #ede6cb | #ffffff |
| Or | `Brown3` #e8b26f | `Yellow2` #f5d893 |
| Master | `Cyan1` #699fad | `White` #ede6cb |

Le nom du rang s'écrit dans la couleur claire du palier.

## Disposition (sous la barre du haut, marges 16)

| Zone | Rectangle (x, y, w, h) |
|---|---|
| MON RANG | 16, 68, 360, 352 |
| PALIERS | 16, 436, 360, 268 |
| Classement | 392, 68, 872, 406 (hauteur FIXE, quel que soit l'onglet) |
| Dernières parties | 392, 490, 872, 214 |

### MON RANG

- Icône ×8 (80×96), à droite : rang (grand, couleur claire), « COTE 950 », « #1247 MONDIAL ».
- « VERS BRONZE I » + « 50 / 100 » (`Yellow2`), barre de 10 segments dans la couleur claire du palier (progression dans la division : `PvpRanks`).
- 8 cases (2 colonnes, hauteur 30) : PARTIES, VICTOIRES, DÉFAITES, NULLES, TAUX, SÉRIE (« 3 V », `Yellow2`), MEILLEURE (cote max), CETTE SEMAINE (« +48 », `Yellow2`).

### PALIERS

- Une ligne par palier (hauteur 36), du plus haut (MASTER) au plus bas (BRONZE) : icône ×2, nom, plage de cote à droite (« 1100 – 1399 », Bronze « 0 – 1099 », Master « 2000 + »), valeurs de `pvp.json` (`ranks`).
- Le palier courant : fond `Black5`, cadre 2 px `Yellow2`, nom avec la division (« BRONZE II ») en `Yellow2`.

### Classement

- En-tête : onglets **MONDIAL / AMIS / AUTOUR DE MOI** ; pagination ‹ 1/7 › à droite (MONDIAL seulement).
- Colonnes : `#` (64) · JOUEUR · RANG (150 : icône ×2 + nom du rang) · COTE (80, à droite) · V · D · N (120) · % (60).
- Lignes de 28 px, fond alterné `Black3` / `Black4` ; #1 en `Yellow2`, #2-#3 en `Brown3`.
- **Ma ligne** : fond `Black5`, cadre 2 px `Yellow2`, pseudo suivi de « (TOI) » en `Yellow2`.
- MONDIAL : 8 joueurs par page (top 50 → 7 pages) ; si je ne suis pas sur la page, ma ligne est **épinglée en bas** sous un filet pointillé.
- AMIS : amis Steam ayant une cote, triés, moi compris.
- AUTOUR DE MOI : les 3 au-dessus, moi, les 4 en dessous.

### Dernières parties

- 4 dernières parties, lignes de 32 : résultat (VICTOIRE `Cyan1` / DÉFAITE `Purple5` / NULLE gris), adversaire (+ étiquette « BOT »), icône + rang + cote de l'adversaire, « 6 PIONS TUÉS », variation de cote (+16 `Cyan1`, −17 `Purple5`, 0 gris), quand (« IL Y A 12 MIN », « HIER »).

## Données (point à décider)

- La cote est aujourd'hui **locale** (`PvpProfile`). Le classement mondial / amis / autour de moi nécessite les **leaderboards Steam** (`SteamUserStats.FindOrCreateLeaderboard`, tri décroissant, `UploadLeaderboardScore` avec la cote après chaque partie classée, téléchargement `Global` / `Friends` / `GlobalAroundUser`).
- Sans Steam : afficher seulement MON RANG, PALIERS et DERNIÈRES PARTIES, et dans le cadre du classement le message « CLASSEMENT INDISPONIBLE HORS LIGNE ».
- **Historique** : à ajouter dans `PvpProfile` (les 20 dernières parties : résultat, pseudo adverse, bot ou non, cote adverse, pions tués, variation de cote, date), dans le même fichier chiffré.
- Statistiques à ajouter au profil si absentes : meilleure cote, série en cours, variation sur 7 jours.
