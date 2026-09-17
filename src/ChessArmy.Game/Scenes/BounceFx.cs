using System;
using System.Collections.Generic;
using ChessArmy.Core.Map;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Animation de la « Balle rebondissante » (objet à lancer du commandant DUO) : la balle quitte la cible
/// directe et ricoche d'ennemi en ennemi, en ARC, une case à la fois. Les dégâts sont déjà résolus par
/// <see cref="ChessArmy.Core.Battle.Match"/> ; ce qui se joue ici, c'est la LECTURE du coup — on doit voir
/// où la balle est allée, dans quel ordre, et chaque chiffre ne sort qu'à l'arrivée de la balle sur sa
/// victime (cf. <see cref="Advance"/>).
///
/// Données + minuterie PURES, comme <see cref="StormFx"/> : le rendu de la balle vit dans la scène, qui
/// interroge <see cref="Segment"/>. Tant que <see cref="Active"/>, la scène gèle entrées, IA et fin de combat.
/// </summary>
public sealed class BounceFx
{
    /// <summary>Durée d'UN bond (s) : assez lent pour suivre l'arc des yeux, assez court pour ne pas lasser.</summary>
    private const double HopDuration = 0.22;

    private readonly List<Cell> _path = new();   // [0] = cible directe (départ), puis chaque rebond
    private double _elapsed;
    private int _landed;                          // rebonds DÉJÀ signalés à la scène (chiffre + son sortis)

    public bool Active { get; private set; }

    /// <summary>Trajectoire complète : la cible directe puis les cases touchées, dans l'ordre des rebonds.</summary>
    public IReadOnlyList<Cell> Path => _path;

    /// <summary>
    /// Lance la balle depuis <paramref name="from"/> (la cible directe) vers <paramref name="hops"/>, dans
    /// l'ordre. Sans effet s'il n'y a aucun rebond : une balle qui ne rebondit pas n'a rien à montrer de plus
    /// que le coup direct.
    /// </summary>
    public void Begin(Cell from, IEnumerable<Cell> hops)
    {
        _path.Clear();
        _path.Add(from);
        _path.AddRange(hops);
        _elapsed = 0;
        _landed = 0;
        Active = _path.Count > 1;
    }

    /// <summary>Réinitialise (nouveau combat).</summary>
    public void Clear()
    {
        _path.Clear();
        _elapsed = 0;
        _landed = 0;
        Active = false;
    }

    /// <summary>
    /// Avance l'animation. Renvoie l'INDICE du rebond qui vient d'être atteint (1..n dans <see cref="Path"/>)
    /// pour que la scène en sorte le chiffre de dégâts et le son AU BON MOMENT, ou <c>-1</c> si la balle est
    /// encore en vol. L'animation s'éteint une fois le dernier rebond atteint.
    /// </summary>
    public int Advance(double dt)
    {
        if (!Active)
            return -1;

        _elapsed += dt;
        // Le rebond n° i est atteint après i bonds ENTIERS : la balle vole d'abord, elle touche ensuite.
        var reached = Math.Min(_path.Count - 1, (int)(_elapsed / HopDuration));
        if (reached <= _landed)
            return -1;

        _landed = reached;
        if (_landed >= _path.Count - 1)
            Active = false;   // dernier rebond atteint : la balle s'arrête là
        return _landed;
    }

    /// <summary>
    /// Bond EN COURS : case de départ, case d'arrivée et avancement [0,1] de la balle entre les deux.
    /// <c>null</c> quand rien ne vole. La scène en tire la position de la balle et la hauteur de son arc.
    /// </summary>
    public (Cell From, Cell To, float T)? Segment
    {
        get
        {
            if (!Active || _path.Count < 2)
                return null;
            var hop = Math.Min(_path.Count - 2, (int)(_elapsed / HopDuration));
            var t = (float)Math.Clamp(_elapsed / HopDuration - hop, 0, 1);
            return (_path[hop], _path[hop + 1], t);
        }
    }
}
