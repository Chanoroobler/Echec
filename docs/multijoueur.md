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

## Notes pour la future scène PvP

Le PvP sera un **mode séparé**, avec sa propre scène. La campagne solo reste telle quelle.

- **Construire le `Match` sans bonus de campagne.** Laisser les bonus d'arbre (`RempartBonus`, `FormationBonus`,
  `TueurDeGeantsBonus`, `ImpactBonus`, `ExtraTurnDomaine`) et `playerBlockedCells` à leur valeur par défaut :
  les règles sont alors symétriques entre les deux camps.
- **Les deux clients simulent le même match.** L'hôte joue `Faction.Player`, l'invité `Faction.Enemy`. La scène
  PvP de l'invité affiche donc le plateau retourné et sélectionne les pions `Enemy`.
- **Contrôleur distant.** Il faudra une classe `RemoteBattleController : IBattleController` alimentée par le
  réseau (Steam P2P), qui renvoie `false` tant que le coup n'est pas arrivé. Chaque coup local validé est envoyé,
  puis on compare `ComputeStateHash()` des deux côtés après chaque coup.
- **Pas de sacoche, de coffres ni de tuiles recrue en PvP.** Ces effets sont propres à la campagne et utilisent
  leur propre hasard.
