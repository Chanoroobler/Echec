# Chess Army

Jeu vidéo en C# / [MonoGame](https://monogame.net) (DesktopGL, .NET 9).

## Architecture

Architecture en couches avec dépendances dirigées vers l'intérieur — le domaine
ne connaît pas le moteur, le moteur ne connaît pas le point d'entrée.

```
ChessArmy.Game  ──►  ChessArmy.Engine  ──►  ChessArmy.Core
(MonoGame)       (MonoGame)         (C# pur, testable)
```

| Projet | Rôle | Dépend de MonoGame ? |
|--------|------|----------------------|
| `src/ChessArmy.Core`   | Domaine : règles, état, entités du jeu (échiquier, pièces). Pur C#, 100 % testable. | ❌ |
| `src/ChessArmy.Engine` | Briques réutilisables au-dessus de MonoGame : gestion de scènes, input, contexte de jeu. | ✅ |
| `src/ChessArmy.Game`   | Point d'entrée et *composition root* : crée la fenêtre, câble les services, héberge les scènes et le contenu. | ✅ |
| `tests/ChessArmy.Core.Tests` | Tests unitaires xUnit du domaine. | ❌ |

### Concepts clés (Engine)

- **`IScene` / `Scene` / `SceneManager`** — chaque écran (menu, partie, pause) est une scène ; le `SceneManager` gère l'écran actif et les transitions.
- **`InputManager`** — état clavier/souris avec détection de fronts (`WasKeyPressed`, `WasLeftClicked`).
- **`GameContext`** — conteneur de services injecté dans les scènes (évite la dépendance directe à la classe `Game`).

## Démarrer

```bash
# Compiler
dotnet build

# Lancer le jeu
dotnet run --project src/ChessArmy.Game

# Tests
dotnet test
```

`Échap` ferme le jeu. La scène de jeu affiche l'échiquier dessiné à partir de
l'état du domaine `ChessArmy.Core`.

## Pour aller plus loin

- Ajouter une `MenuScene` et basculer via `Context.Scenes.Change(...)`.
- Charger des sprites de pièces via le **MonoGame Content Pipeline** (`Content/Content.mgcb`).
- Implémenter les règles de déplacement dans un service de `ChessArmy.Core` (gardé hors du rendu).
