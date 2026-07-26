using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessArmy.Core.Campaign;

/// <summary>
/// Statistiques CUMULÉES d'une run, pour le récap de fin (victoire OU défaite). Alimentée par la scène de
/// jeu à chaque fin de combat et aux événements marquants (fusion, paysans sauvés, coffre, déblocage).
/// PERSISTÉE avec le slot (cf. <see cref="RunStatsSave"/>) : un « Continuer » REPREND les compteurs.
///
/// Les dégâts et les kills sont ventilés par NOM de classe (unique par PNG, cf. <see cref="Battle.UnitClass.Name"/>) :
/// clé stable et directement affichable, sans dépendre de l'identité d'instance d'une <c>UnitClass</c>.
/// </summary>
public sealed class RunStats
{
    private readonly Dictionary<string, int> _damageByClass = new();
    private readonly List<string> _unlockedCommanders = new();
    private readonly List<string> _discoveredClasses = new();
    private readonly List<string> _discoveredEquipment = new();

    /// <summary>Ennemis tués sur toute la run (kills des pions morts/fusionnés inclus, cf. accumulation par delta).</summary>
    public int TotalKills { get; private set; }

    /// <summary>Pions (hors commandant) perdus sur toute la run (permadeath).</summary>
    public int UnitsLost { get; private set; }

    /// <summary>Fusions réalisées.</summary>
    public int Fusions { get; private set; }

    /// <summary>Paysans sauvés/libérés cumulés (missions spéciales).</summary>
    public int PaysansSaved { get; private set; }

    /// <summary>Équipements ramassés (coffres).</summary>
    public int EquipmentFound { get; private set; }

    /// <summary>
    /// Chronomètre de la run : temps de jeu EFFECTIF cumulé, en secondes. Alimenté frame par frame par la
    /// scène de jeu, qui EXCLUT ce qui n'est pas du jeu (menu pause, codex, écran de fin). Persisté avec le
    /// slot, donc une reprise repart du temps déjà écoulé — mais le temps d'un combat quitté sans
    /// sauvegarde est perdu avec lui, la sauvegarde n'ayant lieu qu'en phase de placement.
    /// </summary>
    public double PlayTimeSeconds { get; private set; }

    /// <summary>Noms des commandants DÉBLOQUÉS pendant cette run (nouveaux uniquement), prêts à afficher.</summary>
    public IReadOnlyList<string> UnlockedCommanders => _unlockedCommanders;

    /// <summary>Noms des classes DÉCOUVERTES pendant cette run (évolutions obtenues à la fusion).</summary>
    public IReadOnlyList<string> DiscoveredClasses => _discoveredClasses;

    /// <summary>Noms des équipements DÉCOUVERTS pendant cette run.</summary>
    public IReadOnlyList<string> DiscoveredEquipment => _discoveredEquipment;

    /// <summary>Vrai si au moins un déblocage (commandant / classe / équipement) a eu lieu dans la run.</summary>
    public bool HasUnlocks =>
        _unlockedCommanders.Count + _discoveredClasses.Count + _discoveredEquipment.Count > 0;

    /// <summary>Dégâts infligés cumulés, toutes classes confondues.</summary>
    public int TotalDamage => _damageByClass.Values.Sum();

    /// <summary>Dégâts infligés ventilés par NOM de classe (lecture seule, pour la sauvegarde).</summary>
    public IReadOnlyDictionary<string, int> DamageByClass => _damageByClass;

    public void AddDamage(string className, int amount)
    {
        if (amount > 0 && !string.IsNullOrEmpty(className))
            _damageByClass[className] = _damageByClass.GetValueOrDefault(className) + amount;
    }

    public void AddKills(int count) => TotalKills += Math.Max(0, count);

    public void AddUnitsLost(int n) => UnitsLost += Math.Max(0, n);
    public void AddFusion() => Fusions++;
    public void AddPaysansSaved(int n) => PaysansSaved += Math.Max(0, n);
    public void AddEquipmentFound(int n = 1) => EquipmentFound += Math.Max(0, n);

    /// <summary>Ajoute le temps écoulé d'une frame au chronomètre (valeur négative ou non finie ignorée).</summary>
    public void AddPlayTime(double seconds)
    {
        if (seconds > 0 && double.IsFinite(seconds))
            PlayTimeSeconds += seconds;
    }

    public void AddUnlockedCommander(string id)
    {
        if (!string.IsNullOrEmpty(id)) _unlockedCommanders.Add(id);
    }

    public void AddDiscoveredClass(string asset)
    {
        if (!string.IsNullOrEmpty(asset)) _discoveredClasses.Add(asset);
    }

    public void AddDiscoveredEquipment(string id)
    {
        if (!string.IsNullOrEmpty(id)) _discoveredEquipment.Add(id);
    }

    /// <summary>Les <paramref name="n"/> classes ayant infligé le PLUS de dégâts (décroissant, nom en départage).</summary>
    public IReadOnlyList<(string Class, int Damage)> TopDamage(int n) =>
        _damageByClass.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(Math.Max(0, n)).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Recharge l'état depuis une sauvegarde de slot (récap persisté avec la run). Remplace le contenu
    /// courant ; les valeurs négatives / entrées à 0 sont ignorées, les listes nulles traitées comme vides.
    /// <paramref name="playTimeSeconds"/> a une valeur par défaut : une sauvegarde antérieure au chronomètre
    /// reprend simplement à 0.
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, int>? damage, int totalKills, int unitsLost, int fusions,
        int paysansSaved, int equipmentFound, IReadOnlyList<string>? unlockedCommanders,
        IReadOnlyList<string>? discoveredClasses, IReadOnlyList<string>? discoveredEquipment,
        double playTimeSeconds = 0)
    {
        _damageByClass.Clear();
        if (damage != null)
            foreach (var kv in damage)
                if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key))
                    _damageByClass[kv.Key] = kv.Value;

        TotalKills = Math.Max(0, totalKills);
        UnitsLost = Math.Max(0, unitsLost);
        Fusions = Math.Max(0, fusions);
        PaysansSaved = Math.Max(0, paysansSaved);
        EquipmentFound = Math.Max(0, equipmentFound);
        PlayTimeSeconds = playTimeSeconds > 0 && double.IsFinite(playTimeSeconds) ? playTimeSeconds : 0;

        _unlockedCommanders.Clear();
        _discoveredClasses.Clear();
        _discoveredEquipment.Clear();
        if (unlockedCommanders != null) _unlockedCommanders.AddRange(unlockedCommanders);
        if (discoveredClasses != null) _discoveredClasses.AddRange(discoveredClasses);
        if (discoveredEquipment != null) _discoveredEquipment.AddRange(discoveredEquipment);
    }
}
