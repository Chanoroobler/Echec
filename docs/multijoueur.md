# Multijoueur : état de préparation du code

Faisabilité : **oui**. Le combat se prête au **lockstep** : les deux clients construisent le même `Match`
avec la même graine, puis s'échangent uniquement les coups (5 octets chacun). Chacun rejoue les coups de
l'autre et vérifie l'empreinte d'état. Le tour par tour permet de se passer de serveur et de synchronisation d'état.

Le game design (armées, classement, temps, cartes) n'est pas encore décidé. Ce document ne couvre que le code.

## Déjà en place (2026-11-05)

| Brique | Où | Rôle |
|---|---|---|
| `BattleAction` | `Core/Battle/BattleAction.cs` | Un coup sous forme de données (type + 2 cases), sérialisable sur 5 octets sans allocation |
| `Match.Apply(action)` | `Core/Battle/Match.cs` | Point d'entrée unique d'un coup dans le moteur |
| `Match.ComputeStateHash()` | `Core/Battle/Match.cs` | Empreinte déterministe de l'état (FNV-1a), pour détecter une désynchronisation |
| Aléa unique | `Match` + `EnemyAi` | Tout l'aléa du combat, IA comprise, vient de la graine du `Match` |
| `IBattleController` | `Core/Battle/BattleController.cs` | Décide des coups d'un camp. `AiBattleController` aujourd'hui, un contrôleur distant demain |
| `ExecuteAction` | `GameplayScene` | Point d'entrée unique côté scène (souris, manette, IA), avec FX. Chaque coup validé va dans `_battleLog` |
| `_combatSeed` | `GameplayScene` | Graine neuve à chaque combat. Graine + `_battleLog` suffisent pour rejouer le combat |
| Tests | `tests/.../LockstepTests.cs` | Un combat IA rejoué depuis graine + coups sérialisés retrouve le même état à chaque coup |

## POC PvP (2026-11-05)

Bouton **MULTIJOUEUR** au menu principal, visible dans les builds PLAYTEST et Debug uniquement.

| Brique | Où | Rôle |
|---|---|---|
| `PvpSetup` | `Core/Battle/PvpSetup.cs` | Échiquier 8×8, même armée des deux côtés : 8 Soldats devant, puis Lancier, Cavalier, Mage, Archer, Commandant (roi, essentiel), Mage, Cavalier, Lancier |
| `PvpMatchmaker` | `Game/Net/PvpMatchmaker.cs` | Recherche par lobby Steam public (filtre `chessarmy_mode = pvp_poc_1`) : rejoint un lobby libre, sinon en crée un et attend |
| `PvpSession` | `Game/Net/PvpSession.cs` | Échange P2P (SteamNetworkingMessages, fiable) : poignée de main, coups + empreinte, départ de l'adversaire |
| `RemoteBattleController` | `Game/Net/RemoteBattleController.cs` | Camp adverse alimenté par le réseau |
| `PvpLobbyScene` | `Game/Scenes/PvpLobbyScene.cs` | Écran « recherche d'un adversaire », bouton Annuler |
| Mode PvP | `GameplayScene` (`_pvp`) | Combat sans campagne : pas de sauvegarde, de succès ni de méta-progression ; bandeau de tour, panneau de fin |

**Plateau retourné.** Chaque client joue en bas en `Faction.Player`. Les armées étant symétriques, l'hôte et
l'invité construisent le même plateau local ; seul change le premier joueur (l'hôte). Un coup reçu est retourné
(`PvpSetup.Mirror`) avant d'être appliqué, et l'empreinte se calcule dans le repère de l'hôte
(`ComputeStateHash(mirrored: true)` chez l'invité). Le test `Pvp_MirroredClients_StayInSync` le vérifie.

**Maps multi.** Type de map `Multi` (`CombatType.Multi`), créé dans l'éditeur par le bouton **Nouveau multi**
(échiquier 8×8 d'herbe, armées marquées en E/P à titre indicatif). La map par défaut est `multi_8_01.json`.
Le jeu tire la map de la partie par la graine de l'hôte, parmi les maps Multi jouables (hors brouillons, triées
par nom) : les deux clients tombent sur la même. Une map est jouable si elle fait 8×8 et si les deux lignes de
chaque bord sont franchissables. Elle devrait être **symétrique haut/bas** (mêmes règles sur chaque case et sa
case miroir, buissons compris) : l'invité la voit alors comme l'hôte. Sinon il la joue retournée, avec les bonnes
règles mais des tuiles orientées à l'envers. L'éditeur affiche « Sym » / « Ret » dans la colonne Obj, et le récap
a une section MULTI. Seuls le terrain et les buissons comptent : coffres, recrues et chutes sont ignorés.

**Limites connues du POC.**
- Le retournement ne tient que si aucun trait n'utilise l'aléa ou l'ordre de parcours du plateau (Orage, Esquive…).
  C'est le cas des pions de cette armée. Avec d'autres armées, il faudra faire jouer `Faction.Enemy` à l'invité.
- Le roi adverse n'a pas de sprite ennemi dédié (`commandant_ia_*`) : il reprend le sprite joueur.
- Pas de limite de temps, pas d'abandon dédié (quitter par le menu pause = défaite par abandon chez l'adversaire),
  pas de reconnexion.
- Tester demande deux comptes Steam sur deux PC (Steam n'ouvre qu'un compte par machine).
