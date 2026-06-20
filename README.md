# Echec

Jeu vidéo en C# / [MonoGame](https://monogame.net) (DesktopGL, .NET 9).

## Architecture

Architecture en couches avec dépendances dirigées vers l'intérieur — le domaine
ne connaît pas le moteur, le moteur ne connaît pas le point d'entrée.

```
Echec.Game  ──►  Echec.Engine  ──►  Echec.Core
(MonoGame)       (MonoGame)         (C# pur, testable)
```

| Projet | Rôle | Dépend de MonoGame ? |
|--------|------|----------------------|
| `src/Echec.Core`   | Domaine : règles, état, entités du jeu (échiquier, pièces). Pur C#, 100 % testable. | ❌ |
| `src/Echec.Engine` | Briques réutilisables au-dessus de MonoGame : gestion de scènes, input, contexte de jeu. | ✅ |
| `src/Echec.Game`   | Point d'entrée et *composition root* : crée la fenêtre, câble les services, héberge les scènes et le contenu. | ✅ |
| `tests/Echec.Core.Tests` | Tests unitaires xUnit du domaine. | ❌ |

### Concepts clés (Engine)

- **`IScene` / `Scene` / `SceneManager`** — chaque écran (menu, partie, pause) est une scène ; le `SceneManager` gère l'écran actif et les transitions.
- **`InputManager`** — état clavier/souris avec détection de fronts (`WasKeyPressed`, `WasLeftClicked`).
- **`GameContext`** — conteneur de services injecté dans les scènes (évite la dépendance directe à la classe `Game`).

## Démarrer

```bash
# Compiler
dotnet build

# Lancer le jeu
dotnet run --project src/Echec.Game

# Tests
dotnet test
```

`Échap` ferme le jeu. La scène de jeu affiche l'échiquier dessiné à partir de
l'état du domaine `Echec.Core`.

## Pour aller plus loin

- Ajouter une `MenuScene` et basculer via `Context.Scenes.Change(...)`.
- Charger des sprites de pièces via le **MonoGame Content Pipeline** (`Content/Content.mgcb`).
- Implémenter les règles de déplacement dans un service de `Echec.Core` (gardé hors du rendu).
