# Multijoueur : état de préparation du code

Faisabilité : **oui**. Le combat se prête au **lockstep** : les deux clients construisent le même `Match`
avec la même graine, puis s'échangent uniquement les coups (5 octets chacun). Chacun rejoue les coups de
l'autre et vérifie l'empreinte d'état. Le tour par tour permet de se passer de serveur et de synchronisation d'état.

Le game design (armées, classement, temps, cartes) n'est pas encore décidé. Ce document ne couvre que le code.

## Game design décidé (2026-11-05)

Réponses de l'auteur au questionnaire de cadrage. Elles font référence pour toutes les étapes.

**Ladder et rangs**
- Cote cachée (type Elo) pour le matchmaking ; rangs Bronze, Fer, Argent, Or, Master affichés comme paliers de
  cette cote, **avec divisions** (Argent III, II, I).
- Stockage **local**, mais données **obscurcies et encodées** (pas de JSON lisible ni modifiable à la main).
- Valeurs sensibles **obscurcies aussi en mémoire** (anti-triche par édition de la RAM, type Cheat Engine) :
  points de coffre, cote, rang, déblocages. Type dédié (valeur chiffrée par une clé aléatoire + contrôle
  d'intégrité), jamais la valeur en clair dans un champ.
  - Contre un **humain**, les valeurs de combat (PV, dégâts…) sont déjà protégées : une modification locale
    diverge de l'adversaire et déclenche la désynchronisation.
  - Contre un **bot**, il n'y a personne pour vérifier : le résultat de la partie sera revalidé en rejouant le
    journal des coups (graine + coups) avant d'accorder points et rang, plutôt que de chiffrer les PV dans le
    moteur (qui coûterait cher à chaque calcul).
- Recherche selon l'affluence : rang proche quand il y a du monde, élargie quand il y a peu de joueurs.
- Défis entre amis : **points de coffre seulement**, aucun effet sur le rang.

**Bot de secours (aucun adversaire au bout de 10 s, `botAfterSeconds`)**
- Course humain / bot (`Core/Pvp/PvpBotFallback.cs`) : un adversaire en poignée de main prime toujours ; une
  entrée de lobby en vol au moment du délai a 3 s de sursis. En basculant, on quitte le lobby (même une création
  ou une entrée encore en vol, quittée à sa réponse) : jamais de lobby fantôme ni de deux parties.
- Affiché comme bot : nom suivi de « (BOT) », et si son armée est copiée sur un vrai joueur, on dit laquelle.
- Une partie contre un bot **compte pour le rang et les points**. Le mode doit marcher même avec très peu de
  joueurs : bots **de plus en plus durs** selon le rang (IA de plus en plus intelligente, pas seulement plus précise).
- Armées des bots : une **réserve commune de 40 armées** d'Argent à Master (`botArmies`, composée par l'outil
  `--bot-armies`), toutes différentes, qui remplissent chacune exactement les 35 points d'armée. **Bronze et Fer**
  ont leur propre réserve de 20 armées « débutant » (`botArmiesBeginner`, outil `--beginner-armies`) : pions de
  base et au plus une pièce différente, pour qu'un nouveau joueur ne soit pas perdu. Dans chaque réserve, le rang
  ne change PAS l'armée : seule l'intelligence monte (voir « IA des bots »). Armées copiées sur des joueurs de même
  rang plus tard (stockage partagé nécessaire).

**Composition de l'armée**
- Budget **unique pour tous**. Chaque pion et chaque équipement coûte des points (équipement selon sa rareté).
- **L'armée de départ remplit exactement le budget** : un joueur qui a tout débloqué n'est pas plus fort, il a
  d'autres stratégies.
- **Deux budgets séparés** : **35 points d'armée** (commandant + pions) et **10 points d'équipement** (`equipmentBudget`), qui ne servent qu'aux équipements (l'un ne déborde jamais sur l'autre). La pièce la plus faible coûte **3 points**, pour qu'il y ait moins de pions sur le plateau. L'armée de départ (`startingArmy` dans `pvp.json` : 6 Soldats, Lancier, Cavalier, Mage + le Foudroyeur, 10 pions, posés centrés) remplit les 35 points d'armée.
- **Réglages propres au multi** (le solo ne bouge pas), dans `pvp.json` : stats de pions (`unitStats`) ajustées pour que **chaque pion coûte entre 3 et 8 points** (règle de l'auteur, vérifiée par un test), et équipements retirés (`excludedEquipment` : Séisme, Arc légendaire, Impact rare ; les autres restent dans toutes leurs raretés). Ajustement automatique : `dotnet run --project tools/ChessArmy.Balance -c Release -- --tune-stats --rounds 8 --battles 30000` (dichotomie sur un facteur de force par pièce, à partir des stats du solo, le Soldat restant l'étalon à 3 points), puis l'équilibrage des coûts.
- **En multi, le tier ne fixe pas le prix.** Chaque classe et chaque équipement coûte selon sa force MESURÉE par
  l'outil d'équilibrage `tools/ChessArmy.Balance`, en VRAIES BATAILLES : des dizaines de milliers d'armées complètes
  tirées au hasard (budget de 20 à 40 points, pièces, équipements et commandant au hasard) s'affrontent, jouées par
  deux bots tactiques avec toutes les règles (commandant à protéger). Une régression logistique attribue à chaque
  pièce, équipement et commandant sa contribution à la victoire ; les valeurs sont mises à l'échelle (armée de
  départ = 30) et arrondies (minimum 1). Plusieurs passes : chacune rejoue avec les coûts de la précédente.
  Le budget des armées tirées VARIE volontairement : à budget fixe, valeur et coût ne seraient pas séparables.
  L'outil réécrit `unitCost`, `equipmentCost` et `budget` dans `pvp.json` ; à relancer après tout changement de stats :
  `dotnet run --project tools/ChessArmy.Balance -c Release -- --battles 40000 --passes 3` (environ 3 min).
  L'ancienne mesure en escarmouches reste disponible avec `--skirmish`.
- **Équipements : de 1 à 5 points** (budget d'équipement 10). Mesure dédiée, qui ne réécrit que `equipmentCost` :
  `dotnet run --project tools/ChessArmy.Balance -c Release -- --equipment-only --equip-pct 80 --battles 60000 --passes 3` (environ 5 min).
  L'échelle est fixée par le DEUXIÈME objet le plus fort (à 5) ; un objet signalé « TROP FORT » s'affaiblit en multi
  dans `equipmentStats` (le solo ne bouge pas), puis on remesure.
- Les armées des bots doivent valoir exactement le budget (test) : relancer `--bot-armies` et `--beginner-armies` après une nouvelle mesure des coûts.
- Limites : la force mesurée est celle que la même IA tire de chaque pièce ; le modèle additionne les valeurs (les
  synergies et le placement ne sont pas pris en compte séparément).
- Cases vides permises, doublons permis : seul le budget limite. Deux lignes de 8 cases.
- Commandant obligatoire et **payant** : le plus faible coûte 0, chacun coûte son écart de force mesuré avec lui. Leurs stats multi sont resserrées pour que l'écart reste petit (0 à 6 points). Le **DUO vient avec Basile** (case réservée à gauche du commandant ; la mort de l'un OU de l'autre fait perdre), compté dans son coût. Sa case est **fixe au placement** (centre de la ligne du
  fond) ; il bouge normalement en combat. Sa mort = défaite.
- **1 équipement par pion, commandant exclu.**
- 3 builds sauvegardables ; la map est montrée, puis **30 s** pour choisir son build. Le build adverse reste caché.

**Commandants et progression**
- Ce sont **les commandants du solo** ; trois sont débloqués d'office : le Foudroyeur (`commandant`), le Bastion
  (`Commandant_lancier`) et le Maréchal (`Commandant_cavalier`). Les autres s'obtiennent par coffre.
- Stats propres au multi, **sans pouvoir pour l'instant** (ni arbre ni trait), l'archétype passant par les stats
  (Bastion = tank…).
- Progression **séparée** du solo. Départ : pions T1 + les 3 commandants.
- Deux coffres : **pions** (T2, T3, commandants) et **équipements**. Jamais de doublon. Tout pion obtenu est
  jouable directement (un T3 sans son T2). Catégorie entièrement débloquée = coffre grisé, points gardés.
- Gains : **5 points par pion tué** (le perdant garde les siens) + **30 points par victoire** (nulle : 5). **Pas de plafond** par jour.
  Points de départ suffisants pour ouvrir un coffre de chaque type (tuto multi).
- Barème (budget, coûts, prix des coffres, gains) : proposé par Claude dans un JSON, réglé ensuite.

**En partie**
- Réserve de **10 min** par joueur + **1 min max par coup** ; dépasser l'un ou l'autre = défaite.
- **Pendule basse** (ajout 2026-11-07) : sous **4 min** de réserve, chaque tour joué fait tomber une tuile des
  **2 lignes derrière le joueur** (ligne du fond d'abord, case au hasard). Le pion dessus (quel que soit son camp)
  tombe et meurt ; le roi emporté = défaite. Les deux lignes parties, plus rien ne tombe. Seuil : `collapseBelowSeconds`.
  Le chrono est **arrêté pendant les animations**.
- Égalité : **proposition d'égalité** (l'autre accepte ou refuse) et **détection automatique** quand plus aucune
  pièce ne peut en atteindre une autre.
- Premier joueur **tiré au sort**.
- Déconnexion : **60 s** pour revenir, puis défaite.

## Implémentation (2026-11-05)

Bouton **MULTIJOUEUR** au menu principal, visible dans les builds PLAYTEST et Debug uniquement
(`PvpInvites.Compiled`). Le barème est dans `Assets/Config/pvp.json` (budget, coûts, coffres, gains, temps, rangs,
stats des commandants, bots).

### Parcours d'écrans

Interface décrite dans `docs/multijoueur-interface.md` (maquettes `docs/maquettes/multi-*.png`). Le bouton
MULTIJOUEUR du menu principal mène directement à l'écran Armée.

| Écran | Fichier | Rôle |
|---|---|---|
| Armée (écran unique) | `Game/Scenes/PvpArmyScene.cs` (+ `.Layout`, `.Draw`, `.Match`, `.Chests`) | Barre du haut (rang, cote, bilan, points, COFFRES, RETOUR) ; 3 armées ; commandant (portrait, stats multi, déplacement, traits) ; plateau multi 8×8 réel (`UI/PvpBoardRenderer.cs`) ; réserve paginée PIONS / ÉQUIPEMENTS (‹ 1/2 › dans l'en-tête, molette, LB/RB) et fiche au survol (case du pion, 4 stats avec bonus, plateau 7×7 animé `MoveAttackGrid` + légende, encart de l'équipement, fonctionnement de chaque trait, défilant) ; glisser-déposer, clic ou manette ; budget ; LANCER UNE PARTIE (recherche sans quitter l'écran, armée figée pendant la recherche, bot après `BotAfterSeconds`), DÉFIER UN AMI, invitations Steam ; adversaire trouvé : armée verrouillée, 5 s, combat ; tuto à la première visite |
| Coffres | vue de `PvpArmyScene` (`PvpArmyScene.Chests.cs`) | Coffre de pions et coffre d'équipements, contenu en silhouettes, OUVRIR avec le prix, animation, fenêtre « NOUVEAU ! » ; l'objet est jouable tout de suite |
| Combat | `GameplayScene` + `GameplayScene.Pvp.cs` | Pendules, égalité, déconnexion, gains, panneau de fin (retour à l'écran Armée) |
| Choix d'armée (hors parcours) | `Game/Scenes/PvpPrepScene.cs` | Ancienne étape « choisir une des 3 armées devant la map tirée » : code gardé, plus utilisé |

Les gestes de placement (poser, remplacer, déplacer avec l'équipement, échanger, retirer, équiper, refus sur la
case du commandant, budget) sont dans le Core : `Core/Pvp/PvpPlacement.cs`, testés par `PvpPlacementTests.cs`.

Disposition : celle des maquettes sur un canevas 1280×720 (écrans 1440p) ; variante compacte sur 960×540
(1080p) : portrait ×2, réserve en 3 colonnes (6 cartes par page), plateau limité à ses 6 rangées du bas. Le
contenu des coffres (des dizaines d'objets) est en cases de 32 et passe à droite du coffre quand il ne tient pas
dessous.

Sans Steam (lancement depuis Visual Studio), LANCER UNE PARTIE joue directement contre un bot.

**Tuto** (`PvpArmyScene.Tutorial.cs`, étapes dans `Core/Pvp/PvpTutorial.cs`) : 15 bulles courtes, une idée par
bulle (voile sauf sur la zone expliquée), puis la pendule et les règles du combat. S'ouvre seul tant que
`PvpProfile.TutorialVersion` est plus ancienne que `CurrentTutorialVersion` (monter ce nombre pour le remontrer à
tous) ; bouton « ? » de la barre du haut pour le relancer. Suivant : clic, Entrée, Espace, →, A, RB ; précédent :
←, LB ; passer : Échap, B. Textes `pvp.tuto.*`, chiffres lus dans `pvp.json`.

Captures de développement (build Debug) : `ChessArmy.Game.exe -pvpshots <dossier>` enregistre chaque état de
l'écran Armée et des Coffres aux deux tailles et dans plusieurs langues, avec un profil en mémoire (le vrai
profil n'est ni lu ni écrit), puis ferme le jeu.

### Moteur (Core)

| Brique | Rôle |
|---|---|
| `Match` (`mirrorRows`, `pvpRules`) | Le client RETOURNÉ parcourt le plateau dans l'ordre du repère commun (`Units()`, voisins, points de chute, tirages au sort) : les deux clients résolvent chaque coup à l'identique, pour toutes les classes et tous les traits. Destruction mutuelle = nulle |
| `Match.IsDeadPosition` | Égalité automatique : plus aucun contact possible entre les deux camps, même en plusieurs coups (estimation prudente) |
| `BattleAction` Resign / AgreeDraw | Fins de partie (temps écoulé, abandon, égalité acceptée), journalisées comme les coups |
| `PvpSetup` | Création du plateau, pose des armées, règles de fin de coup (Séisme symétrique, position morte), rejeu, maps |
| `Pvp/PvpConfig` | Lecture de `pvp.json` |
| `Pvp/PvpBuild` + `PvpCatalog` | Armée (commandant + pions + équipements), coût, validation, commandants aux stats multi |
| `Pvp/PvpRanks` | Cote Elo, rangs à divisions |
| `Pvp/PvpProfile` | Profil multi : points, cote, bilan, déblocages, 3 armées ; fichier chiffré (AES) et signé (HMAC), lié au compte Steam |
| `Pvp/Obscured` | `ObscuredInt` / `ObscuredSet` : valeurs jamais en clair en mémoire, toute retouche détectée |
| `Pvp/PvpClock` | Réserve 10 min + 1 min par coup |
| `Pvp/PvpBotAi` | IA des bots par rang : heuristique rapide (rangs bas), recherche alpha-bêta sur des copies de la partie (rangs hauts), évaluation aux poids entraînés |
| `Pvp/PvpForker` | Copies exactes d'une partie (rejouée depuis graine + armées + journal) pour que le bot essaie des coups |
| `Pvp/PvpLeaderboard` | Classement : bilan + formation encodés dans les détails d'une entrée Steam ; choix d'une formation de joueur pour un bot |

### Playtest et jeu complet : progressions séparées (2026-11-06)

Règle : la progression multi du playtest (cote, points, coffres, collection, armée, historique) n'est JAMAIS
reprise dans le jeu complet. À la sortie du multi dans le jeu complet, tout le monde repart du profil par défaut.
Aucune migration.

- **Save** (`Core/Pvp/PvpSaveSlot`, choisie à la COMPILATION dans `PvpProfileStore`, jamais à l'exécution) :
  - PLAYTEST et Debug gardent `pvp_<steamid>.dat`, signé avec la clé `<steamid>`. C'est le fichier historique,
    et le reset F9 continue de marcher.
  - Le jeu complet (FolderProfileFull, et la démo) lit `pvpfull_<steamid>.dat`, signé avec la clé
    `<steamid>:full`. Un fichier playtest copié ou renommé est donc refusé (`.rejete`), et le jeu repart d'un
    profil neuf.
- **Classement, lobbies** : l'app Playtest est une app Steam séparée (`SteamService.PlaytestAppId`), donc ses
  `pvp_cote` et ses lobbies sont distincts de ceux du jeu complet d'office. Attention : une build Debug utilise
  l'AppID du jeu complet, donc les parties de test du dev arrivent dans le `pvp_cote` du jeu complet. Il faut
  vider ce classement dans Steamworks avant la sortie.
- La save SOLO n'est pas concernée.

### Classement (2026-11-06)

- **Stockage** : tableau de classement Steam `pvp_cote` (créé par le jeu, `FindOrCreateLeaderboard`, trié par cote
  décroissante), choix de l'auteur (sans serveur ; limite assumée : c'est le jeu du joueur qui envoie sa cote).
- **Envoi** (`Game/Net/PvpLeaderboardService`) après chaque partie classée, bots compris (abandon aussi) : score =
  cote (remplace toujours, elle peut baisser) ; détails (64 entiers au plus, `k_cLeaderboardDetailsMax`, vérifié
  dans Steamworks.NET 2024.8.0) = version, V/D/N, commandant, puis 2 entiers par pion (case + classe, équipement).
  Les noms sont stockés par EMPREINTE (FNV) et relus dans le catalogue courant ; une formation qui ne se relit plus
  (pièce retirée, barème changé) est ignorée, le bilan reste.
- **Lecture** : le haut du classement (100) et les 50 joueurs au-dessus et au-dessous de soi (même niveau).
- **Écran** : bouton CLASSEMENT de la barre du haut (`PvpArmyScene.Ranking.cs`) : place, nom Steam, rang, cote,
  bilan à gauche (soi en jaune) ; à droite la dernière formation classée du joueur survolé ou choisi, avec son
  commandant, ses équipements (icône, ou point de couleur de rareté en 960×540) et son taux de victoire.
  Molette ou gâchettes hautes pour défiler. Captures : `-pvpshots` (classement factice).
- **Bots** (`PvpMatchInfo.ForBot`) : EN PRIORITÉ la formation exacte d'un vrai joueur classé du même rang (jamais
  la sienne) ; chance de copie 15 % par formation disponible, plafonnée à 75 % (décision de l'auteur : avec peu
  de joueurs, une armée habituelle revient souvent pour ne pas croiser toujours la même).

### IA des bots (2026-11-06)

- **Un niveau d'IA par division**, 13 en tout (`bots` dans `pvp.json` : `bronze-3` … `or-1`, `master`), choisi par
  `PvpRanks.BotFor(cote)`. Rappel : la division 3 est la plus basse, Bronze 3 est donc le bot le plus faible.
- **Plancher commun** : jusqu'à Fer 1, le bot attaque dès qu'un de ses pions le peut (`forceAttack`) ; il ne choisit
  pas toujours la mise à mort (`killChance`). Les rangs bas foncent parfois sans regarder le danger (`recklessness`,
  pente douce : ignorer le danger tout le temps est une falaise) et se trompent parfois de coup (`accuracy`).
- **Rangs hauts** : recherche alpha-bêta (`depth` demi-coups de plus, `breadth` / `replyBreadth` coups examinés) sur
  des COPIES exactes de la partie : `Match.Clone` (copie instantanée, aléa compris grâce à `SeededRandom`, même suite
  que `System.Random`), la position de départ étant rejouée une fois par `PvpForker`. Table de transposition (une
  position déjà calculée n'est pas recalculée, son meilleur coup est essayé en premier) et coups de la racine
  répartis sur tous les cœurs. Environ 10 fois plus rapide que l'ancien rejeu complet à chaque position (mesure
  `--ai-bench`), ce qui a permis un demi-coup de plus en Or et Master. Le calcul tourne en tâche de fond.
- **Réflexion affichée** : chaque coup attend un délai tiré au hasard (`minDelaySeconds` / `maxDelaySeconds`, de
  3-8 s en Bronze à 2-5 s en Master) pour avoir l'air d'un joueur ; le bot calcule vraiment pendant ce temps
  (approfondissement progressif, il garde la dernière profondeur terminée si le temps est écoulé).
  **Coup évident** (`PvpBotAi.Obvious`) : seul coup possible, mise à mort du commandant adverse (joués sans
  calculer), ou prise qui supprime une menace sur son propre commandant. Le délai tombe alors à 0,5-1,2 s
  (`obviousMinDelaySeconds` / `obviousMaxDelaySeconds`, réglables par bot), seulement si le bot joue bien ce
  coup-là ; s'il en préfère un autre, il garde sa réflexion normale.
- **Évaluation** d'une position (`botWeights`) : matériel, pression sur les commandants, pièces exposées,
  initiative, avance, mobilité, soutien. Poids entraînés par auto-jeu (`--ai-train`, puis SPSA `--ai-spsa`) : le
  second entraînement n'a plus rien gagné, la force vient surtout de la profondeur de calcul.
- **Armées « débutant » en Bronze et Fer** (`"beginnerArmies": true` sur ces 6 niveaux, réserve
  `botArmiesBeginner`) : 20 armées différentes, faites des pions de BASE de chaque domaine (Soldat, Lancier,
  Cavalier, Mage) et, pour trois sur quatre, d'UNE pièce d'une autre classe tirée au hasard (jamais la même
  partout). Budget plein (35 points), sans équipement, commandants variés, posées comme les autres (mêlée devant,
  tireurs derrière, depuis le centre). Recomposer : `--beginner-armies [--count 20] [--seed 2026]`. Argent, Or et
  Master gardent `botArmies`. Le premier bot qui équipe et sort des pièces variées est donc Argent 3.
- **Échelle mesurée** (`--ai-ladder`, chaque niveau contre le précédent ; chacun joue SES armées : tirées dans sa
  réserve et, entre niveaux de même réserve, jouées dans les quatre combinaisons ; Fer 1 contre Argent 3 compare
  donc aussi les armées) : voir le tableau ci-dessous. Réglages en duel : `--ai-pair --a "..." --b "..."`.

| Montée (Bronze et Fer : 400 parties par duel ; au-dessus : 160) | Le niveau du dessus gagne |
|---|---|
| Bronze 3 → 2 → 1 | 58 %, 57 % |
| Bronze 1 → Fer 3 → 2 → 1 | 60 %, 61 %, 64 % |
| Fer 1 → Argent 3 → 2 → 1 | 66 % (400 parties, armées libres + équipements contre débutant), 67 %, 58 % |
| Argent 1 → Or 3 → 2 → 1 | 58 %, 68 %, 66 % |
| Or 1 → Master | 76 % |
| Master contre Argent 1 / Argent 3 | 93 % / 98 % |

Profondeurs depuis 2026-11-06 : Argent 1 et Or 3 à 2, Or 2 et Or 1 à 3, Master à 4 (≈ 0,5 s par coup sur tous les
cœurs). Profondeur 5 testée : +18 points contre la 4, mais ≈ 16 s par coup sur un seul cœur, trop lourd pour un PC
modeste. En jeu, si le calcul dépasse le délai de réflexion, le bot joue le coup de la profondeur précédente.

- **Équipements des bots** : à partir d'Argent, les équipements sont TIRÉS AU HASARD à chaque partie dans les
  10 points d'équipement (`PvpBotDef.ArmyFor`), pour que la même armée ne revienne pas toujours pareille. Bronze et
  Fer jouent sans équipement (`"equipment": false`). La réserve `botArmies` ne contient que les pions. En Or et
  Master (`"smartEquipment": true`), chaque objet va au pion qui en profite le plus (`PvpBotEquipment.Fit` : PV et
  traits défensifs à la mêlée du front, tir aux tireurs, auras au centre, déplacement aux pièces lentes), avec les
  restrictions du solo (monture, Dame). MESURÉ : le placement ne change pas la force (49 % contre le hasard sur
  400 parties, et toutes les variantes essayées entre 47 et 54 %) ; il sert à avoir l'air d'un vrai joueur.
- **Styles de jeu** (`botStyles`) : chaque bot nommé joue toujours le même style, tiré avec son nom
  (`PvpMatchInfo.ForBot`) : équilibré, agressif (avance et cherche les cibles), prudent (pèse plus le danger),
  garde (ses pièces restent près du commandant). Ils jouent sur l'heuristique des coups (rangs bas et choix des
  coups examinés), la garde aussi sur l'évaluation. Mesurés contre le neutre au même niveau (400 parties) :
  agressif 46-48 %, prudent 55-56 %, garde 51-57 %, soit moins qu'un palier de division. Plus marqué, l'agressif
  s'effondre (26 % en Or) : foncer est simplement mauvais dans ce jeu.

### Réseau (Game/Net)

| Brique | Rôle |
|---|---|
| `PvpMatchmaker` | Recherche par cote : lobbies triés par proximité, écart toléré qui s'élargit avec l'attente ; lobby privé pour les défis |
| `PvpSession` | Protocole P2P : bonjour/départ (graine + cotes), armées, coups (+ empreinte + réserve), égalité, battements de cœur, départ |
| `PvpInvites` | Invitations Steam acceptées (jeu lancé ou `+connect_lobby`) |
| `PvpProfileStore` | Fichier `%AppData%/Echec/save/pvp_<steamid>.dat` (PLAYTEST, Debug) ou `pvpfull_<steamid>.dat` (jeu complet) ; un fichier retouché est mis de côté (`.rejete`) |
| `PvpMatchInfo` | Description d'une partie (humain ou bot, armées, graine, premier joueur, classée ou non) |

### Règles appliquées

- **Premier joueur** : tiré de la graine de l'hôte (`PvpSetup.HostStarts`), connu des deux.
- **Pendules** : ne tournent que plateau posé (jamais pendant les animations ni une déconnexion). Chacun fait foi
  pour sa propre pendule et s'annonce perdant (`Resign`) ; la réserve part avec chaque coup pour recaler l'affichage
  adverse. Un adversaire dont la pendule tombe et qui ne se déclare pas est déclaré perdant 15 s plus tard.
- **Pendule basse** : jamais envoyée sur le réseau. Chaque client la refait après le coup (`PvpSetup.CollapseAfterTurn`)
  avec la réserve ANNONCÉE (ms entières, même valeur des deux côtés) ; le tirage passe par l'aléa du Match, le trou
  vit dans le Match (`IsCollapsed`, copié par `Clone`, dans l'empreinte), jamais dans le terrain partagé. Le journal
  garde un coup `Collapse` pour le rejeu de validation contre les bots.
- **Égalité** : bouton (ou Y) ; l'adversaire accepte ou refuse. Un bot accepte s'il est nettement en retard au
  matériel. Égalité automatique en position morte ou destruction mutuelle.
- **Déconnexion** : au-delà de 4 s de silence, compte à rebours de 60 s (pendules arrêtées), puis victoire.
  Quitter (menu pause) = départ immédiat = défaite, comptée même si on ferme la scène (anti rage-quit).
- **Gains** : points par pion tué (perdant compris) + bonus de victoire ou de nulle. Cote Elo si la partie est
  classée (pas pour les défis entre amis). Contre un bot, la partie est REJOUÉE depuis la graine et le journal
  des coups avant tout gain : un état final différent (mémoire modifiée) = partie non validée.
- **Désynchronisation** (empreintes différentes) : signalée en rouge, et la partie ne rapporte rien.

### Maps multi

Type de map `Multi` (`CombatType.Multi`), créé dans l'éditeur par le bouton **Nouveau multi** (échiquier 8×8,
armées marquées en E/P à titre indicatif). Map par défaut : `multi_8_01.json`. Le jeu tire la map par la graine de
l'hôte, parmi les maps Multi jouables (hors brouillons, triées par nom). Jouable = 8×8 et deux lignes
franchissables de chaque côté. **Symétrique haut/bas** recommandé : l'invité la voit alors comme l'hôte ; sinon il
la joue retournée (règles justes, tuiles orientées à l'envers). L'éditeur affiche « Sym » / « Ret » et le récap a
une section MULTI. Seuls le terrain et les buissons comptent.

### Tests (`tests/ChessArmy.Core.Tests/PvpTests.cs`)

- 60 parties au hasard entre un hôte et un invité retourné, armées tirées dans TOUTES les classes, commandants et
  équipements : empreintes identiques à chaque coup.
- Rejeu identique à la partie jouée ; abandon et égalité retournés ; position morte ; budget, armées des bots ;
  rangs et Elo ; profil chiffré (retouche et autre compte refusés) ; coffres sans doublon ; `ObscuredInt` qui
  détecte une retouche mémoire ; bot tactique qui prend le roi.
- `PvpPlacementTests.cs` : pose et remplacement (l'équipement de l'ancien est retiré), déplacement (l'équipement
  suit), échange, retrait, équipement remplacé / déplacé / échangé, budget (refus, points libérés), case du
  commandant et hors zone refusées.

### Limites connues

- Tout est stocké côté client : ces protections arrêtent la triche facile, pas un tricheur déterminé (il faudrait un serveur).
- La reconnexion couvre une coupure réseau ; un joueur dont le jeu plante ne peut pas reprendre la partie.
- Les bots utilisent une réserve commune de 40 armées ; copier les armées de vrais joueurs demande un stockage partagé.
- Tester à deux demande deux comptes Steam sur deux PC.
