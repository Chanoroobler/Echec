# Multijoueur : nouvelle interface (écran unique Armée + Coffres)

Maquette validée le 2026-11-05. Elle remplace le parcours actuel « Accueil multi → Mes armées → Choix d'armée ».
Captures de référence (résolution virtuelle du jeu, 1280×720, échelle 1) dans `docs/maquettes/` :

| Capture | État |
|---|---|
| `multi-1-build-pions.png` | Écran d'arrivée, onglet PIONS, survol d'un pion équipé (fiche avec bonus) |
| `multi-2-build-equipements.png` | Onglet ÉQUIPEMENTS, survol d'un équipement |
| `multi-3-recherche.png` | Recherche en cours (l'armée reste modifiable) |
| `multi-4-coffres.png` | Écran COFFRES |
| `multi-5-coffre-recompense.png` | Fenêtre « NOUVEAU ! » : équipement (bonus + trait donné) |
| `multi-5b-coffre-recompense-pion.png` | Fenêtre « NOUVEAU ! » : pion à 3 traits (sprite ×2) |
| `multi-5c-coffre-recompense-commandant.png` | Fenêtre « NOUVEAU ! » : commandant |
| `multi-6-fiche-commandant.png` | Survol du commandant sur le plateau (fiche avec le fonctionnement de ses traits) |

## Principes

- **On arrive directement sur le build d'armée** depuis le bouton MULTIJOUEUR du menu principal. Plus de menu intermédiaire.
- **Tout est classé** : pas de bouton « Partie classée ». Seule option à côté : DÉFIER UN AMI (bascule).
- **La vraie map au centre** : le plateau multi 8×8 rendu comme en combat (tuiles `multi_8_01`, vrais sprites `_back` des pions), pas un schéma.
- **Glisser-déposer** partout (souris), avec un mode clic équivalent (clic sur l'objet, puis clic sur la case), et la manette via `PvpFocus`.
- **Pixel perfect** : sprites et icônes affichés à leur taille native ou à un multiple ENTIER (×2, ×3, ×6), `SamplerState.PointClamp`. Aucune taille fractionnaire.
- **Aucun texte d'ambiance inutile** : pas de phrase de règles dans la bande du bas, pas d'« armée adverse cachée » (aucun adversaire au moment du build).

## Disposition (1280×720)

Toutes les positions sont en pixels virtuels (`Context.VirtualResolution` = 1280×720).

| Zone | Rectangle (x, y, w, h) | Contenu |
|---|---|---|
| Barre du haut | 0, 0, 1280, 52 | Fond `Black1`, filet bas 2 px `Black5` |
| Onglets d'armée | 16, 64, 320, 48 | 3 boutons (103×48, écart 6) : « ARMÉE n » + coût « 81/100 » |
| Panneau commandant | 16, 120, 320, 516 | Voir plus bas |
| Plateau | 352, 64, 512, 528 | 8×8 cases de 64 + 16 px d'épaisseur de tuile |
| Panneau réserve | 880, 64, 384, 572 | Onglets PIONS / ÉQUIPEMENTS, grille paginée, fiche |
| Bande du bas | 0, 644, 1280, 76 | Défier un ami, budget + aide (ou état de recherche), bouton de lancement |

Panneaux : fond `Black4` (#1d3230), bordure 3 px `Brown2` (#b6834c) + liseré intérieur 2 px `Brown1` (#704d2b) (même habillage que l'écran de sélection du commandant).
Boutons : fond `Black5`/`Navy1`, bordure 2 px `Brown1` ; sélectionné = fond `Blue3` (#3a708e) + bordure `Yellow2` (#f5d893).
Bouton principal (LANCER UNE PARTIE, OUVRIR, CONTINUER) : fond `Brown2`, texte `Black1`, bordure 3 px `Yellow2`, ombre pleine 4 px `Brown1` en dessous.

### Barre du haut

- À gauche : « MULTIJOUEUR » (titre, `Yellow2`), puis 3 pastilles : rang (`PvpUi.RankName`, bordure `Brown2`, texte `Yellow1`), « COTE 950 », bilan « 12V 8D 2N ».
- À droite, dans cet ordre : **points** (icône pièce + nombre, texte `Yellow2`), **COFFRES** (icône `Objects/coffre.png` 32×32 + libellé), **RETOUR**.
- Les points ne sont PLUS dans les pastilles de profil : ils sont juste à gauche du bouton COFFRES.
- Icône pièce : pas d'asset existant → `Assets/Icons/points.png`, dessin pixel art **10×10**, affiché **×2 = 20×20** (barre du haut et bouton OUVRIR).
- Sur l'écran Coffres, le bouton COFFRES est à l'état sélectionné et RETOUR ramène au build. Sur le build, RETOUR ramène au menu principal.

### Panneau commandant (gauche)

De haut en bas :
1. « COMMANDANT » (gris `Blue1`) à gauche, « GRATUIT · CASE FIXE » (`Cyan1`) à droite.
2. Flèches ‹ › (40×48) de part et d'autre du **portrait `<id>_front` à ×3 (192×192)**.
3. Nom (`PvpUi.CommanderName`, grand, espacé), puis des points de pagination (un par commandant débloqué, l'actif plus large en `Yellow1`).
4. 4 barres segmentées (10 segments) comme l'écran de sélection du solo, stats **MULTI** (`PvpCatalog.CommanderClass`) :
   PV (crème, max 40), PUISSANCE (`Brown3`, max 25), MOUVEMENT (`Cyan1`, max 4), PORTÉE (`Yellow2`, max 4), valeur à droite.
5. Icône `Icons/domaine_<x>.png` (39×39, natif) + « DÉPLACEMENT » / « DAME · 8 DIRECTIONS » (Tour · lignes, Fou · diagonales, Cavalier · saut en L).
6. Traits en **étiquettes** (une par trait, retour à la ligne automatique). Ne pas écrire de libellé « TRAITS » : le texte doit tenir dans le panneau pour tous les commandants (le Maréchal a deux traits).

Seuls les commandants débloqués (`Profile.Owns`) sont proposés par les flèches.

### Plateau (centre)

- Le plateau multi complet (8×8), tuiles de la map Multi par défaut (`multi_8_01` : damier `Y`/`j`, feuille `TilesMurs.png`), épaisseur de tuile visible en bas.
- Rangées 6 et 7 (les deux lignes du joueur) : voile `Blue3` à ~32 % ; survol pendant un placement : voile `Cyan1` ~55 % + cadre 3 px `Yellow2`. Les autres rangées restent sans voile (pas de zone adverse affichée).
- Pions : sprite `<asset>_back` 64×64 décalé de −8 px vers le haut (comme en combat) ; survolé sans rien tenir : −14 px (petit soulèvement).
- Commandant : sprite `<id>_back` sur la case fixe (colonne 4, ligne du fond), jamais déplaçable.
- Équipement porté : **badge 32×32 en bas à droite de la case** (fond `Equipment/background.png` + icône, natifs), liseré 1 px de la couleur de rareté autour d'un contour 2 px `Black1`.
- Aperçu de dépôt : le sprite tenu en fantôme à 55 % sur la case visée.

### Réserve (droite)

- En-tête : onglets **PIONS** / **ÉQUIPEMENTS** à gauche, **pagination à droite** (‹ 1/2 ›, boutons 32×32, grisés en bout de liste). Pendant qu'on tient un pion/équipement venu du plateau, la pagination est remplacée par un bouton rouge « REMETTRE EN RÉSERVE ». (Le budget restant est déjà dans la bande du bas : pas de « RESTE » ici.)
- **Grille paginée : 4 colonnes × 2 rangées = 8 cartes par page**, juste sous l'en-tête. Page mémorisée par onglet. Molette sur la grille = page suivante / précédente. Tout le reste du panneau est pour la fiche.
- Carte pion (≈ 88×96) : case 64×64 (fond herbe alterné `Yellow4`/`#56664a`) avec le sprite `_back` natif, icône `Icons/tier_n.png` (23×9) en haut à gauche, coût en bas à droite (pastille `Black1`, texte `Yellow2`) ; nom en dessous (tronqué « … » s'il est trop long).
- Carte équipement : `background.png` + icône à ×2 (64×64), liseré intérieur 2 px couleur de rareté, coût en bas à droite, nom en couleur de rareté.
- Non débloqué : voile sombre + cadenas pixel (affiché ×2), carte à 40 % ; clic → message « à débloquer dans le coffre de pions / d'équipements ». Trop cher pour le budget restant : carte à 40 % mais reste prenable (le dépôt sera refusé).
- **Fiche** sous la grille (tout le reste du panneau, ≈ 300 px de haut, séparée par un filet `Black5`). Elle s'affiche au survol d'une carte de la réserve, d'un pion du plateau, **ou du commandant sur le plateau**, et pendant qu'on tient un objet.
  - **Pion** (voir `multi-1-build-pions.png`) — **ni nom ni coût** (déjà visibles sur la carte de la réserve et sur le plateau) :
    - en haut, côte à côte : à gauche **le plateau 7×7 animé du jeu, `MoveAttackGrid.DrawBoardWithPawn` réutilisé tel quel** (115×115, sprite `_front` à moitié de sa taille en couche écran, il fait le tour de ses cases de déplacement ; jaune = déplacement, rouge = attaque, orange = les deux ; saut en cloche du cavalier ; zone morte et attaque « Dame » de l'archer monté respectées ; bonus de déplacement d'équipement inclus) ; à droite, **les 4 stats empilées** (PV, PUISSANCE, MOUVEMENT, PORTÉE), une ligne de 28 px chacune sur fond `Black3` : icône `stat_pv` / `stat_deg` / `stat_dep` / `stat_tir` 32×32 native, libellé gris, puis à droite le bonus « +5 » en `Yellow2` (si équipement) et la valeur (en `Yellow2` si bonifiée, sinon couleur de la stat).
    - dessous, **sur toute la largeur du panneau** : l'encart de l'équipement porté (fond `Black3`, bordure `Brown1`, badge 32×32, nom en couleur de rareté, effet), puis **chaque trait avec son fonctionnement** : NOM DU TRAIT (`Cyan1` ; `Yellow2` s'il vient de l'équipement) puis la description en gris (`kw.<trait>.desc` de `strings.csv`, valeurs du moteur injectées : `Match.BaseRempartReduction` = 4, `BaseFormationBonus` = 2, `BaseImpactDamage` = 5). Zone défilante si ça ne tient pas.
  - **Commandant** (survol de sa case) : même fiche (plateau animé + stats multi à droite, traits dessous).
  - **Équipement** : case 64×64 (`background.png` + icône ×2, liseré de rareté), NOM, rareté en couleur, coût en pastille ; une ligne encadrée par effet (« PV  +4 », « PUISSANCE  +1 », « TRAIT  Rempart ») ; si l'équipement donne un trait, son fonctionnement.
  - Rien survolé : une phrase d'aide propre à l'onglet.

Couleurs de rareté : Commun `White` (#ede6cb), Rare `Cyan1` (#699fad), Légendaire `Brown3` (#e8b26f).

### Bande du bas

- Gauche : bouton bascule **DÉFIER UN AMI** (sélectionné = partie privée sans effet sur le rang).
- Centre, au repos : **BUDGET** + barre (remplissage `Brown3`) + « 81/100 » ; dessous, une ligne d'aide/erreur (gris, ou `Purple5` pour une erreur : « Pas assez de points pour Paladin. », « Case fixe du commandant. »…).
- Centre, en recherche : carré clignotant `Cyan1` + « Recherche d'un adversaire… » / « bot dans 38 s si personne » (ami : « En attente de ton ami… » / « overlay Steam ouvert »), et un bouton **ANNULER** à gauche du bouton principal.
- Centre, adversaire trouvé : pastille « ADVERSAIRE : <nom> <rang> » + « combat dans 5 s ».
- Droite : bouton principal 340×60 « LANCER UNE PARTIE » / sous-titre « ARMÉE 1 · LE FOUDROYEUR ». États : « RECHERCHE 0:12 » (fond `Black5`, bordure `Cyan1`) ; « PARTIE TROUVÉE » (fond `Cyan1`) ; désactivé si l'armée est vide (« pose au moins un pion »). Ami : « INVITER » puis « EN ATTENTE ».

## Interactions du build

Glisser-déposer (souris) — chaque geste a son équivalent au clic :

| Geste | Résultat |
|---|---|
| Pion de la réserve → case bleue libre | Posé (refusé si le budget est dépassé) |
| Pion de la réserve → case occupée | Remplace le pion (l'équipement de l'ancien est retiré) |
| Pion posé → autre case bleue | Déplacé, **son équipement le suit** ; case occupée = échange (équipements échangés aussi) |
| Pion posé → hors des lignes du joueur / sur la réserve | Retiré (avec son équipement) |
| Équipement → pion posé | Équipé ; un autre équipement déjà porté est remplacé |
| Badge d'équipement → autre pion | Déplacé ; si ce pion était équipé, les deux s'échangent |
| Badge → hors du plateau / sur la réserve | Retiré |
| Équipement → commandant / case vide / hors zone | Refusé avec message |
| N'importe quoi → case du commandant | Refusé (« Case fixe du commandant. ») |

- Au clic depuis la réserve, l'objet reste en main pour en poser plusieurs d'affilée (tant que le budget le permet). Clic droit / Échap / B : lâcher ce qu'on tient (un pion venu du plateau retourne à sa case).
- Chaque changement est enregistré (`PvpProfileStore.Save` comme aujourd'hui) ; le budget compte pions + équipements (`PvpCatalog.Cost`).
- Changer d'onglet d'armée ou d'écran remet d'abord en place ce qu'on tient.
- Manette : `PvpFocus` sur tous les éléments (onglets, flèches, cases, cartes, pagination, boutons) ; A = prendre/poser, B = lâcher/retour, LB/RB = page de la réserve, gâchettes = onglet PIONS/ÉQUIPEMENTS.

## Lancement et recherche

- LANCER UNE PARTIE démarre `PvpMatchmaker.Start()` **sans quitter l'écran** ; l'armée reste modifiable pendant la recherche.
- Au bout de `BotAfterSeconds` sans adversaire (ou sans Steam) : bot du niveau du joueur (comme `PvpLobbyScene` aujourd'hui).
- Adversaire trouvé (`session.Started`) : l'armée sélectionnée est **verrouillée**, compte à rebours de 5 s, puis le combat démarre avec elle. L'étape `PvpPrepScene` (choix parmi 3 armées devant la map tirée) n'est plus utilisée dans ce parcours (garder le code, ne plus y passer) — **point à confirmer**.
- DÉFIER UN AMI activé : le bouton lance `StartFriendLobby()` ; « Réinviter » reste accessible pendant l'attente. Une invitation Steam acceptée (`PvpInvites.TryTake`) bascule directement en attente de connexion.
- Le tuto multi (première visite) s'affiche par-dessus ce nouvel écran.

## Écran Coffres

Même barre du haut ; contenu à la place du build (pas de nouvelle scène nécessaire, ou `PvpChestScene` refaite avec cette disposition).

- Titre « COFFRES » + « Jamais de doublon · tout ce que tu obtiens est jouable tout de suite ».
- Deux panneaux côte à côte (≈ 596×530) : **COFFRE DE PIONS** (« Pions T2, T3 et commandants », `UnitChestCost`) et **COFFRE D'ÉQUIPEMENTS** (« Un équipement par pion, commandant exclu », `EquipmentChestCost`).
  - En-tête : titre + « 4 À DÉBLOQUER » (`Cyan1`) ou « TOUT EST DÉBLOQUÉ » (gris).
  - Coffre `Objects/coffreAnimate.png` (frame 0) à ×3 (192×192) sur une ombre ovale ; coffre vidé = frame 3 grisée.
  - « CONTENU » : une case 64×64 par objet du coffre ; déjà obtenu = en couleur sur fond herbe, bordure `Brown2` ; pas encore obtenu = **silhouette noire** (sprite / icône assombri) sur fond `Black5`.
  - Bouton OUVRIR pleine largeur : « [pièce] OUVRIR · 60 » ; grisé + « IL MANQUE 20 POINTS » en `Purple5` si pas assez ; « COMPLET » quand tout est débloqué.
- Ligne du bas : « GAGNE DES POINTS EN PARTIE : +2 PAR PION TUÉ · +15 PAR VICTOIRE · +5 PAR NULLE » (valeurs de `pvp.json`).
- Ouverture : points débités, animation des 4 frames de `coffreAnimate.png` (~170 ms par frame), puis `Profile.OpenChest` et **fenêtre de révélation** (voir `multi-5`, `multi-5b`, `multi-5c`) :
  - voile `Black1` 80 % ; panneau 460 px de large, centré, habillage des panneaux ;
  - « NOUVEAU ! » (`Cyan1`), puis l'objet : équipement = `background.png` + icône ×6 (192×192) ; pion / commandant = sprite `_front` ×3 (192×192), **×2 (128×128) si l'objet a 3 traits ou plus** pour que tout tienne ;
  - nom en grand (pion `Yellow2`, commandant `Yellow1`, équipement = couleur de rareté), puis une ligne « ÉQUIPEMENT · LÉGENDAIRE » / « PION · TIER 3 » / « COMMANDANT » (le mot de rareté ou de tier en couleur) ;
  - filet `Black5`, puis deux blocs bien séparés, chacun avec un **titre gris aligné à gauche** :
    - **« BONUS DU PION ÉQUIPÉ »** (équipement) : une ligne par bonus, hauteur 32, fond `Black3`, bordure `Black5` : icône de stat 32×32 native (`stat_pv`, `stat_deg`, `stat_dep`, `stat_tir`), libellé gris à gauche, **valeur « +4 » en `Yellow2` à droite** ;
      **« STATISTIQUES »** (pion / commandant) : les 4 stats en grille 2×2, même format, valeurs en crème ;
    - **« TRAIT DONNÉ AU PION »** (équipement) / « TRAIT » / « TRAITS » (pion) : un encadré par trait (fond `Black3`, bordure `Black5`), **nom du trait en haut** (`Yellow2` s'il vient d'un équipement, `Cyan1` sinon), **description dessous en crème, alignée à gauche** (`kw.*.desc`) ;
  - bouton CONTINUER.
  - Plus de texte centré empilé : chaque information a sa place (titre de bloc → ligne ou encadré), on lit d'abord le nom, puis les bonus, puis ce que fait le trait.
- Ce qui est obtenu apparaît immédiatement dans la réserve du build (cadenas retiré) ou dans les flèches du commandant.

## Textes (strings.csv)

Réutiliser les clés existantes (`pvp.chests`, `pvp.chest_units`, `pvp.chest_equipment`, `pvp.chest_left`, `pvp.chest_empty`, `pvp.chest_got`, `pvp.searching`, `pvp.bot_in`, `pvp.found`, `pvp.challenge`, `commander.*`, `unit.*`, `equip.*`, `equip.rarity.*`…) et ajouter les nouvelles dans les 9 langues (FR, EN, IT, DE, ES, PL, TR, ZH) : onglets PIONS / ÉQUIPEMENTS, « RESTE {0} », « REMETTRE EN RÉSERVE », « GRATUIT · CASE FIXE », « DÉPLACEMENT », libellés des domaines, « BUDGET », messages d'aide et d'erreur du placement, « ÉQUIPÉ : {0} », « LANCER UNE PARTIE », « PARTIE TROUVÉE », « combat dans {0} s », « NOUVEAU ! », « IL MANQUE {0} POINTS », « COMPLET », « OUVRIR · {0} », ligne des gains.
