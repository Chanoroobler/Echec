using System.Collections.Generic;
using System.Linq;
using Echec.Core.Campaign;
using Echec.Core.Command;
using Echec.Engine;
using Echec.Engine.Input;
using Echec.Engine.Localization;
using Echec.Engine.Rendering;
using Echec.Engine.UI;
using Echec.Engine.UI.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.UI;

/// <summary>
/// Écran modal de l'ARBRE DE COMMANDEMENT, ouvert pendant la phase de placement. Dessine l'arbre du
/// commandant courant (<see cref="Run.Tree"/>) : les branches en colonnes, les niveaux du bas vers le
/// haut, reliés en éventail (tout nœud d'un niveau ouvre tout nœud du niveau suivant de SA branche).
///
/// Chaque nœud n'affiche qu'une ICÔNE 32×32 (jamais de texte) et son coût ; le libellé et la description
/// vivent dans strings.csv (<c>tree.&lt;id&gt;</c> / <c>tree.&lt;id&gt;.desc</c>) et n'apparaissent qu'en
/// infobulle au survol / au focus. Toute la règle d'achat est dans <see cref="Run.CanUnlock"/> —
/// cette vue ne fait que présenter et transmettre le clic.
/// </summary>
public sealed class CommandTreeView
{
    // Géométrie de l'arbre (canevas virtuel, jamais mise à l'échelle fractionnaire).
    private const int NodeSize = 64;     // boîte d'un nœud
    private const int IconSize = 32;     // PNG natif, centré dans la boîte
    private const int NodeGapX = 28;     // entre les deux nœuds d'un même niveau
    private const int BranchGap = 76;    // entre deux colonnes
    private const int LevelGapY = 100;   // d'un niveau au suivant (haut de boîte à haut de boîte)
    private const int BranchWidth = 2 * NodeSize + NodeGapX;
    // L'en-tête loge : le titre, les points disponibles, puis le rappel des SOURCES de points
    // (une ligne par source : la mission, plus celle propre au commandant).
    private const int HeaderH = 100;
    // Le pied de page loge, DANS CET ORDRE : les libellés de branche, le bouton FERMER, puis l'indice.
    private const int FooterH = 92;
    private const int BranchLabelY = 8;    // sous le bas des nœuds de niveau 1
    private const int CloseButtonY = 28;   // sous les libellés de branche
    private const int CloseButtonH = 34;

    /// <summary>État d'achat d'un nœud — pilote la couleur du cadre et du coût.</summary>
    private enum NodeState
    {
        Owned,        // déjà acheté
        Buyable,      // prérequis satisfait ET assez de points
        TooExpensive, // prérequis satisfait, pas assez de points
        Locked,       // prérequis non satisfait
    }

    private readonly GameContext _ctx;
    private readonly Dictionary<string, Texture2D?> _icons = new();
    private readonly Dictionary<string, Rectangle> _rects = new();

    private float _pulse;

    /// <summary>Manette : niveau focus (1..MaxLevel), ou <see cref="CloseFocusLevel"/> pour le bouton FERMER.</summary>
    private int _focusLevel = 1;
    private int _focusCol;         // manette : rang dans la rangée de ce niveau

    /// <summary>« Niveau » fictif, sous le niveau 1 : le bouton FERMER (atteignable en descendant).</summary>
    private const int CloseFocusLevel = 0;

    public CommandTreeView(GameContext context) => _ctx = context;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        _focusLevel = 1;
        _focusCol = 0;
        _ctx.Sounds.Play("menu_open");
    }

    public void Close()
    {
        IsOpen = false;
        _ctx.Sounds.Play("menu_close");
    }

    /// <summary>Libère les icônes chargées (appelé par <c>Scene.Unload</c>).</summary>
    public void Unload()
    {
        foreach (var tex in _icons.Values)
            tex?.Dispose();
        _icons.Clear();
    }

    /// <summary>
    /// Traite une image de l'écran ouvert : survol/clic souris, navigation + A à la manette. Le bouton
    /// FERMER et la touche Échap (traitée par la scène) referment. Renvoie vrai si l'écran doit rester
    /// ouvert (faux = le joueur vient de le fermer par un bouton de cette vue).
    /// </summary>
    public bool Update(Run run, Rectangle area, float dt)
    {
        _pulse += dt;
        Layout(run.Tree, area);

        if (_ctx.Input.UsingGamepad)
        {
            if (_ctx.Input.WasCancelPressed) { Close(); return false; }
            MoveFocus(run.Tree);
            if (_ctx.Input.WasConfirmPressed)
            {
                if (_focusLevel == CloseFocusLevel) { Close(); return false; }
                if (FocusedNode(run.Tree) is { } focused)
                    TryUnlock(run, focused);
            }
            return true;
        }

        var mouse = _ctx.Input.MousePosition;
        if (_ctx.Input.WasLeftClicked)
        {
            if (CloseButtonRect(area).Contains(mouse)) { Close(); return false; }
            if (NodeAt(run.Tree, mouse) is { } clicked)
                TryUnlock(run, clicked);
        }
        return true;
    }

    private void TryUnlock(Run run, CommandNode node)
    {
        if (run.Unlock(node))
            _ctx.Sounds.Play("recruit");
        else
            _ctx.Sounds.Play("menu_close");   // achat refusé (points / prérequis) : retour sonore sec
    }

    // ── Navigation manette ───────────────────────────────────────────────────────────────────────
    // Une « rangée » = tous les nœuds d'un niveau, toutes branches confondues, de gauche à droite.
    // Gauche/droite parcourt la rangée ; haut/bas change de niveau en gardant au mieux la colonne.

    private static IReadOnlyList<CommandNode> Row(CommandTree tree, int level) =>
        tree.Nodes.Where(n => n.Level == level).OrderBy(n => n.Branch).ToList();

    private CommandNode? FocusedNode(CommandTree tree)
    {
        if (_focusLevel == CloseFocusLevel)
            return null;   // le focus est sur le bouton FERMER, pas sur un nœud
        var row = Row(tree, _focusLevel);
        return row.Count == 0 ? null : row[System.Math.Clamp(_focusCol, 0, row.Count - 1)];
    }

    private void MoveFocus(CommandTree tree)
    {
        // Bouton FERMER : seul Haut en sort (retour sur la rangée du niveau 1).
        if (_focusLevel == CloseFocusLevel)
        {
            if (_ctx.Input.Nav(NavDir.Up))
                _focusLevel = 1;
            return;
        }

        var row = Row(tree, _focusLevel);
        if (row.Count == 0)
            return;

        if (_ctx.Input.Nav(NavDir.Right)) _focusCol = (_focusCol + 1) % row.Count;
        if (_ctx.Input.Nav(NavDir.Left)) _focusCol = (_focusCol - 1 + row.Count) % row.Count;
        if (_ctx.Input.Nav(NavDir.Up)) _focusLevel = System.Math.Min(CommandTree.MaxLevel, _focusLevel + 1);
        // Bas depuis le niveau 1 : on descend sur le bouton FERMER, juste sous l'arbre.
        if (_ctx.Input.Nav(NavDir.Down)) _focusLevel--;
        if (_focusLevel == CloseFocusLevel)
            return;

        _focusCol = System.Math.Clamp(_focusCol, 0, System.Math.Max(0, Row(tree, _focusLevel).Count - 1));
    }

    // ── Géométrie ────────────────────────────────────────────────────────────────────────────────

    private static int TreeWidth(CommandTree tree) =>
        tree.BranchCount * BranchWidth + System.Math.Max(0, tree.BranchCount - 1) * BranchGap;

    private static int TreeHeight() => (CommandTree.MaxLevel - 1) * LevelGapY + NodeSize;

    /// <summary>
    /// Panneau centré dans <paramref name="area"/> — la zone LIBRE que la scène lui réserve (sous la frise
    /// des missions, à gauche du panneau d'inventaire), et non le viewport entier : sinon la modale paraît
    /// décalée par rapport au plateau qu'elle recouvre. Bornée à l'aire si celle-ci est trop courte.
    /// </summary>
    private static Rectangle PanelRect(CommandTree tree, Rectangle area)
    {
        var w = TreeWidth(tree) + 80;
        var h = TreeHeight() + HeaderH + FooterH;
        var y = area.Y + System.Math.Max(0, (area.Height - h) / 2);
        return new Rectangle(area.X + (area.Width - w) / 2, y, w, h);
    }

    private static Rectangle CloseButtonRect(Rectangle area)
    {
        // Ancré sous les libellés de branche ; la largeur du panneau dépend de l'arbre, mais il est centré.
        var h = TreeHeight() + HeaderH + FooterH;
        var top = area.Y + System.Math.Max(0, (area.Height - h) / 2);
        return new Rectangle(area.X + area.Width / 2 - 60,
            top + HeaderH + TreeHeight() + CloseButtonY, 120, CloseButtonH);
    }

    /// <summary>(Re)calcule le rectangle de chaque nœud pour cette image. Clé = id du nœud.</summary>
    private void Layout(CommandTree tree, Rectangle area)
    {
        _rects.Clear();
        var panel = PanelRect(tree, area);
        var left = panel.X + (panel.Width - TreeWidth(tree)) / 2;
        var top = panel.Y + HeaderH;

        for (var branch = 0; branch < tree.BranchCount; branch++)
        {
            var bx = left + branch * (BranchWidth + BranchGap);
            for (var level = 1; level <= CommandTree.MaxLevel; level++)
            {
                var nodes = tree.At(branch, level);
                if (nodes.Count == 0)
                    continue;

                // Le niveau 4 (un seul nœud) se centre ; un niveau à deux nœuds occupe toute la colonne.
                var rowW = nodes.Count * NodeSize + (nodes.Count - 1) * NodeGapX;
                var x0 = bx + (BranchWidth - rowW) / 2;
                var y = top + (CommandTree.MaxLevel - level) * LevelGapY;   // niveau 1 en bas
                for (var i = 0; i < nodes.Count; i++)
                    _rects[nodes[i].Id] = new Rectangle(x0 + i * (NodeSize + NodeGapX), y, NodeSize, NodeSize);
            }
        }
    }

    private CommandNode? NodeAt(CommandTree tree, Point p) =>
        tree.Nodes.FirstOrDefault(n => _rects.TryGetValue(n.Id, out var r) && r.Contains(p));

    // ── Rendu ────────────────────────────────────────────────────────────────────────────────────

    /// <summary><paramref name="area"/> = zone où centrer le panneau ; <paramref name="viewport"/> = voile plein canvas.</summary>
    public void Draw(SpriteBatch sb, Viewport viewport, Rectangle area, Run run)
    {
        var tree = run.Tree;
        Layout(tree, area);
        var panel = PanelRect(tree, area);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Fill(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * 0.62f);
        _ctx.Style.DrawPanel(sb, panel);

        DrawHeader(sb, panel, run);
        DrawLinks(sb, tree, run);

        var hovered = _ctx.Input.UsingGamepad ? FocusedNode(tree) : NodeAt(tree, _ctx.Input.MousePosition);
        foreach (var node in tree.Nodes)
            DrawNode(sb, node, run, node == hovered);

        DrawBranchLabels(sb, tree, panel);
        DrawFooter(sb, panel, area);
        if (hovered != null)
            DrawTooltip(sb, hovered, viewport);
        sb.End();
    }

    private void DrawHeader(SpriteBatch sb, Rectangle panel, Run run)
    {
        _ctx.Font.DrawCentered(sb, Loc.T("tree.title"), new Rectangle(panel.X, panel.Y + 12, panel.Width, 24), 3, Palette.Yellow2);
        var points = Loc.T("tree.points", run.CommandPoints);
        DrawPointTotal(sb, _ctx.Font, points, new Rectangle(panel.X, panel.Y + 44, panel.Width, 12),
            run.CommandPoints > 0 ? Palette.Cyan1 : Palette.Blue1);

        // Comment on gagne des points : la mission (commun à tous), puis la source PROPRE au commandant
        // (fusion pour le commandant de départ) — sa clé de texte vit sous l'id de son arbre.
        DrawIncomeLine(sb, panel, 64, Loc.T("tree.income_mission", Run.PointsPerMission));
        if (run.CommanderDef.FusionPoints > 0)
            DrawIncomeLine(sb, panel, 78, Loc.T($"tree.{run.Tree.Id}.income", run.CommanderDef.FusionPoints));
    }

    /// <summary>Ligne de gain : en MINUSCULES (preserveCase), pour la détacher des libellés capitalisés de l'UI.</summary>
    private void DrawIncomeLine(SpriteBatch sb, Rectangle panel, int dy, string text) =>
        _ctx.Font.DrawCentered(sb, text, new Rectangle(panel.X, panel.Y + dy, panel.Width, 10), 1, Palette.Blue1,
            preserveCase: true);

    /// <summary>Libellé de chaque branche sous sa colonne (clé <c>tree.&lt;treeId&gt;.branch&lt;N&gt;</c>).</summary>
    private void DrawBranchLabels(SpriteBatch sb, CommandTree tree, Rectangle panel)
    {
        var left = panel.X + (panel.Width - TreeWidth(tree)) / 2;
        var y = panel.Y + HeaderH + TreeHeight() + BranchLabelY;
        for (var branch = 0; branch < tree.BranchCount; branch++)
        {
            var bx = left + branch * (BranchWidth + BranchGap);
            _ctx.Font.DrawCentered(sb, Loc.T($"tree.{tree.Id}.branch{branch}"),
                new Rectangle(bx, y, BranchWidth, 12), 1, Palette.Blue1);
        }
    }

    /// <summary>
    /// Liens en ÉVENTAIL : chaque nœud d'un niveau est relié à chaque nœud du niveau suivant de sa branche
    /// — c'est exactement la règle de prérequis. Le trait s'allume dès que le nœud du BAS est acquis
    /// (le prérequis est alors satisfait pour tout le niveau au-dessus).
    /// </summary>
    private void DrawLinks(SpriteBatch sb, CommandTree tree, Run run)
    {
        for (var level = 1; level < CommandTree.MaxLevel; level++)
            foreach (var lower in tree.Nodes.Where(n => n.Level == level))
                foreach (var upper in tree.Nodes.Where(n => n.Level == level + 1 && n.Branch == lower.Branch))
                    DrawLink(sb, _rects[lower.Id], _rects[upper.Id],
                        run.IsUnlocked(lower.Id) ? Palette.Yellow1 : Palette.Blue2);
    }

    /// <summary>Trait en équerre (montée verticale, palier horizontal, montée finale) entre deux nœuds.</summary>
    private void DrawLink(SpriteBatch sb, Rectangle from, Rectangle to, Color c)
    {
        const int t = 2;
        var midY = (from.Top + to.Bottom) / 2;
        Fill(sb, new Rectangle(from.Center.X - t / 2, midY, t, from.Top - midY), c);
        Fill(sb, new Rectangle(to.Center.X - t / 2, to.Bottom, t, midY - to.Bottom + t), c);
        var x0 = System.Math.Min(from.Center.X, to.Center.X);
        var x1 = System.Math.Max(from.Center.X, to.Center.X);
        Fill(sb, new Rectangle(x0, midY, x1 - x0 + t, t), c);
    }

    private NodeState StateOf(Run run, CommandNode node)
    {
        if (run.IsUnlocked(node.Id))
            return NodeState.Owned;
        if (!run.Tree.PrerequisiteMet(node, run.UnlockedNodes))
            return NodeState.Locked;
        return run.CommandPoints >= node.Cost ? NodeState.Buyable : NodeState.TooExpensive;
    }

    private void DrawNode(SpriteBatch sb, CommandNode node, Run run, bool hovered)
    {
        var rect = _rects[node.Id];
        var state = StateOf(run, node);

        _ctx.Style.DrawRecessed(sb, rect);

        // Cadre : or (acquis), cyan pulsé (achetable), bleu (trop cher), sombre (verrouillé).
        var border = state switch
        {
            NodeState.Owned => Palette.Yellow1,
            NodeState.Buyable => Color.Lerp(Palette.Cyan1, Palette.Yellow2, Pulse()),
            NodeState.TooExpensive => Palette.Navy1,
            _ => Palette.Black5,
        };
        Border(sb, rect, border, state == NodeState.Owned || state == NodeState.Buyable ? 2 : 1);
        if (hovered)
            Border(sb, Inflate(rect, 3), Palette.Yellow2, 2);

        // Icône 32×32 NATIVE (jamais redimensionnée), assombrie tant que le nœud est hors de portée. Un nœud
        // ACQUIS n'affiche plus son coût : l'icône se recentre alors dans tout le cadre (DrawNodeIcon centre
        // dans la boîte qu'on lui donne) au lieu de rester poussée vers le haut.
        var owned = state == NodeState.Owned;
        var box = owned ? rect : new Rectangle(rect.X, rect.Y + 8, rect.Width, IconSize);
        DrawNodeIcon(sb, node, box, state == NodeState.Locked ? new Color(90, 90, 90) : Color.White);

        // Coût sous l'icône, suivi du jeton de points ; rien pour un nœud déjà acquis (le cadre or suffit).
        if (owned)
            return;
        var color = state switch
        {
            NodeState.Buyable => Palette.White,
            NodeState.TooExpensive => Palette.Purple5,
            _ => Palette.Grey,
        };
        // Le jeton s'assombrit avec le nœud verrouillé, pour ne pas attirer l'œil sur un coût hors de portée.
        var iconTint = state == NodeState.Locked ? new Color(90, 90, 90) : Color.White;
        DrawPointTotal(sb, _ctx.Font, node.Cost.ToString(),
            new Rectangle(rect.X, rect.Bottom - 16, rect.Width, 10), color, iconTint);
    }

    /// <summary>
    /// Icône d'un nœud, à 32×32 natif centré dans <paramref name="box"/> (repli coloré si le PNG manque).
    /// Public : les cartes de pion s'en servent pour montrer les améliorations actives sur l'unité.
    /// </summary>
    public void DrawNodeIcon(SpriteBatch sb, CommandNode node, Rectangle box, Color tint)
    {
        var icon = new Rectangle(box.Center.X - IconSize / 2, box.Center.Y - IconSize / 2, IconSize, IconSize);
        if (Icon(node.Icon) is { } tex)
            sb.Draw(tex, icon, tint);
        else
            DrawIconFallback(sb, node, icon, tint);
    }

    /// <summary>Côté de l'icône des POINTS de commandement (le jeton doré affiché à côté d'un total).</summary>
    public const int PointIconSize = 8;

    /// <summary>Nom du PNG 8×8 du jeton de points, dans le même dossier que les icônes de nœuds.</summary>
    private const string PointIconName = "point";

    /// <summary>
    /// Dessine le jeton de points de commandement (8×8 natif) dont le coin haut-gauche est à
    /// (<paramref name="x"/>, <paramref name="y"/>). Repli sur un losange plein si le PNG manque.
    /// Public : le panneau de placement s'en sert aussi, à côté de son compteur de points.
    /// </summary>
    public void DrawPointIcon(SpriteBatch sb, int x, int y, Color? tint = null)
    {
        var box = new Rectangle(x, y, PointIconSize, PointIconSize);
        if (Icon(PointIconName) is { } tex)
        {
            sb.Draw(tex, box, tint ?? Color.White);
            return;
        }
        for (var row = 0; row < PointIconSize; row++)
        {
            var half = System.Math.Min(row, PointIconSize - 1 - row) + 1;   // losange plein
            Fill(sb, new Rectangle(x + PointIconSize / 2 - half, y + row, half * 2, 1), Palette.Yellow1);
        }
    }

    /// <summary>
    /// Écrit <paramref name="text"/> suivi du jeton de points, l'ensemble CENTRÉ dans <paramref name="area"/>.
    /// Le jeton est aligné sur la hauteur du texte (7 px de glyphe contre 8 px d'icône) et peut être teinté
    /// (nœud verrouillé) pour rester cohérent avec le libellé.
    /// </summary>
    public void DrawPointTotal(SpriteBatch sb, PixelFont font, string text, Rectangle area, Color color,
        Color? iconTint = null)
    {
        const int gap = 4;
        var textW = font.Measure(text, 1);
        var x = area.X + (area.Width - (textW + gap + PointIconSize)) / 2;
        var y = area.Y + (area.Height - PixelFont.GlyphH) / 2;
        font.Draw(sb, text, new Vector2(x, y), 1, color);
        DrawPointIcon(sb, x + textW + gap, y, iconTint);
    }

    /// <summary>Repli sans PNG : aplat coloré par branche + initiale, pour que l'arbre reste jouable.</summary>
    private void DrawIconFallback(SpriteBatch sb, CommandNode node, Rectangle icon, Color tint)
    {
        var branchColor = node.Branch switch
        {
            0 => Palette.Yellow1,
            1 => Palette.Cyan1,
            _ => Palette.Green1,
        };
        Fill(sb, icon, new Color(branchColor.ToVector3() * tint.ToVector3()));
        Border(sb, icon, Palette.Black1, 1);
        _ctx.Font.DrawCentered(sb, node.Id[..1].ToUpperInvariant(), icon, 2, Palette.Black1);
    }

    /// <summary>Infobulle : libellé + description localisés, posés au-dessus ou sous le nœud selon la place.</summary>
    private void DrawTooltip(SpriteBatch sb, CommandNode node, Viewport vp)
    {
        var name = Loc.T(node.NameKey);
        var lines = Wrap(Loc.T(node.DescKey), 260);

        var w = System.Math.Max(_ctx.Font.Measure(name, 2), lines.Max(l => _ctx.Font.Measure(l, 1))) + 24;
        var h = 22 + lines.Count * 12 + 16;

        var anchor = _rects[node.Id];
        var x = System.Math.Clamp(anchor.Center.X - w / 2, 8, vp.Width - w - 8);
        var y = anchor.Top - h - 10;
        if (y < 8)
            y = anchor.Bottom + 10;   // pas la place au-dessus : on bascule dessous

        var box = new Rectangle(x, y, w, h);
        _ctx.Style.DrawPanel(sb, box);
        _ctx.Font.Draw(sb, name, new Vector2(box.X + 12, box.Y + 10), 2, Palette.Yellow2);
        for (var i = 0; i < lines.Count; i++)
            _ctx.Font.Draw(sb, lines[i], new Vector2(box.X + 12, box.Y + 32 + i * 12), 1, Palette.White);
    }

    private void DrawFooter(SpriteBatch sb, Rectangle panel, Rectangle area)
    {
        var btn = CloseButtonRect(area);
        var focused = _ctx.Input.UsingGamepad && _focusLevel == CloseFocusLevel;
        var hover = !_ctx.Input.UsingGamepad && btn.Contains(_ctx.Input.MousePosition);
        var dy = _ctx.Style.DrawButton(sb, btn, UiStyle.StateOf(hover || focused, hover && _ctx.Input.IsLeftDown));
        var label = btn; label.Offset(0, dy);
        if (focused)
            Border(sb, Inflate(label, 2), Palette.Yellow2, 2);
        _ctx.Font.DrawCentered(sb, Loc.T("tree.close"), label, 1, hover || focused ? Palette.Yellow2 : Palette.White);

        // L'indice suit TOUJOURS le périphérique courant (manette vs clavier/souris).
        var hint = Loc.T(_ctx.Input.UsingGamepad ? "tree.hint_gp" : "tree.hint");
        _ctx.Font.DrawCentered(sb, hint, new Rectangle(panel.X, btn.Bottom + 6, panel.Width, 10), 1, Palette.Blue1);
    }

    /// <summary>Découpe <paramref name="text"/> en lignes tenant dans <paramref name="maxWidth"/> pixels (échelle 1).</summary>
    private List<string> Wrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (_ctx.Font.Measure(candidate, 1) > maxWidth && line.Length > 0)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
            lines.Add(line);
        return lines.Count > 0 ? lines : new List<string> { "" };
    }

    /// <summary>PNG 32×32 du nœud (cache ; null si absent → repli coloré).</summary>
    private Texture2D? Icon(string name)
    {
        if (_icons.TryGetValue(name, out var tex))
            return tex;
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, $"Assets/CommandTree/{name}.png");
        tex = Textures.LoadPngOrNull(_ctx.GraphicsDevice, path);
        _icons[name] = tex;
        return tex;
    }

    /// <summary>Oscillation 0→1→0 pour le cadre des nœuds achetables.</summary>
    private float Pulse() => (float)(System.Math.Sin(_pulse * 4.0) * 0.5 + 0.5);

    private void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(_ctx.Pixel, r, c);

    private void Border(SpriteBatch sb, Rectangle r, Color c, int t)
    {
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Y, t, r.Height), c);
        Fill(sb, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
