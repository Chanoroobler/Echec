# Patch note : météo, décors, faune et déplacements des pions

## Nouveau : les déplacements des pions visibles dans les cartes

L'ancienne petite icône de domaine est remplacée par un **plateau 7×7 animé** sur toutes les cartes de pion.

- Le pion **se promène sur le plateau** : il part du centre vers chacune des cases qu'il peut atteindre, une par une, et revient au centre entre deux. Le tour se fait dans le sens des aiguilles d'une montre, pour bien lire le motif.
- **Code couleur :**
  - **Jaune** : cases où il peut se déplacer
  - **Rouge** : zones où il peut tirer
  - **Orange** : il peut à la fois y aller et y tirer
- Le plateau tient compte de tout : directions du domaine, portée de mouvement, portée de tir, **zone morte**, **saut en L** du cavalier (qui fait un petit bond au lieu de glisser), **Attaque libre** et **Repositionnement stratégique**.
- En combat, la carte détaillée utilise les **stats réelles du pion** (équipement et arbre de commandement compris). Dans le codex et hors run, ce sont les stats de base de la classe.
- Le pion du plateau est affiché **net, à sa résolution d'origine**, sans perte de pixels malgré sa taille réduite.

**Où le voir :**
- Carte détaillée en combat (survol / sélection)
- Cartes de recrutement, de draft, de récompense et de fusion (sprite à gauche, plateau à droite)
- Codex
- Écran de sélection du commandant : la fiche montre le plateau du commandant (plateau vide et silhouette tant qu'il est verrouillé)

Autres détails :
- Les cartes sont plus larges (même largeur que la carte de combat).
- Sous le plateau de la carte de combat, une **rangée d'icônes** montre les améliorations d'arbre (cadre cyan) et l'équipement (cadre doré) ; « +N » si tout ne tient pas.
- Le tutoriel explique le nouveau code couleur.

## Nouveau : météo et heure du jour

Chaque mission a désormais sa propre ambiance lumineuse, qui suit l'avancée de la run : **jour**, puis **coucher de soleil**, puis **nuit**, et le cycle recommence.

- **Jour** : des ombres de nuages glissent lentement sur le plateau et sur la mer, avec des rayons de soleil en diagonale.
- **Coucher de soleil** : lumière chaude, ombres de nuages violettes.
- **Nuit** : clair de pleine lune, ombres plus discrètes et **lucioles**. Un papillon qui se pose sur un buisson la nuit en fait sortir des lucioles.
- **Pluie** (environ une mission sur cinq, quelle que soit l'heure) : ciel couvert gris-bleu, gouttes et éclaboussures, **flaques** sur les chemins et l'herbe, et ronds de gouttes dans les flaques et dans l'eau.
- Entre deux missions, la météo passe de l'une à l'autre en **fondu enchaîné**.
- La météo d'une mission reste la même si on quitte la partie puis qu'on la reprend.

### Maps de neige

- **Chute de neige** : sur les maps de neige, la pluie est remplacée par des flocons qui tombent lentement en se balançant, poussés par un léger vent. Pas de flaques ni de ronds dans l'eau, et un ciel plus clair.
- **Bonhommes de neige** : au moins un rocher de chaque map de neige est remplacé par un bonhomme de neige (et les autres ont une chance de l'être aussi). Il se comporte exactement comme un rocher : il bloque le passage et le tir.

## Nouveau : décors aléatoires

Les plateaux sont maintenant parsemés de petits éléments de décor, placés au hasard à chaque combat. Ils sont **purement décoratifs** et ne changent rien aux règles.

- **Herbe** : champignons, fleurs, feuillages, branches, troncs, cailloux.
- **Terre** (chemins, haut des murs) : pierres.
- **Neige** : feuillages, feuilles, pierres et troncs enneigés, et des pierres sur les chemins de neige (jamais sur la glace).
- **Eau** : feuilles, fleurs et nénuphars qui flottent et **dérivent** avec les vagues, en laissant un sillage qui épouse leur forme.
- **Ressac** : de petites vagues partent du bord des rochers et des berges, et des poissons font de temps en temps un « bloop » en eau libre.
- Plusieurs tilesets ont été retouchés (neige, eaux, murs, escaliers).

## Vie sur le plateau

- **Renards** : un petit renard roux vit sur l'herbe, et un **renard des neiges** sur la neige. Ils restent assis, se lèvent de temps en temps et trottinent un peu, en passant derrière les buissons. Sur les grandes maps, il peut y en avoir deux.
  - **Clic sur un renard** : il se dresse, fait deux petits bonds et des cœurs s'envolent.
  - **Attention** : si un pion se pose sur un renard qui n'a aucune issue, il est écrasé... Un succès, « Ami des bêtes », récompense les runs gagnées sans en écraser aucun.
- **Papillons** : ils traversent l'écran par petites vagues de 1 à 5, en plusieurs groupes, avec une ombre au sol. De temps en temps, l'un d'eux se pose sur un rocher ou un buisson, puis repart (et s'envole si un pion arrive). Un clic sur un papillon en vol l'écrase.
- **Une grenouille** a élu domicile sur un nénuphar. Elle sort la tête de l'eau, saute sur sa feuille, s'y repose, puis replonge, avec un rond dans l'eau à chaque fois. Assise, elle suit le nénuphar quand les vagues le poussent.
  - **Clic sur la grenouille** : elle sautille avec un petit « boing » et des cœurs.

## Effets de combat

- **Éclair** redessiné : un zigzag plus fin et plus nerveux, cœur blanc bordé de jaune, avec un léger flash de la case touchée.

## Nouveau : bilan de boss vaincu

Après chaque boss (le boss final compris, avant le récap de fin de run), une fenêtre **BOSS VAINCU** affiche :
- le portrait et le nom du boss ;
- le **bilan du combat** : tours, ennemis tués, pertes, dégâts infligés au boss, **coup fatal** (le pion qui l'a achevé) et MVP ;
- l'avancement de la run, phase par phase.

Un clic sur **Continuer** (ou A à la manette) passe au recrutement.
