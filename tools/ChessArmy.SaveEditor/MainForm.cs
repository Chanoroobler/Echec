using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;

namespace ChessArmy.SaveEditor;

/// <summary>
/// Fenêtre unique de l'éditeur de sauvegardes : on charge un slot, on tripote l'état d'une run
/// (phase/mission, commandant, difficulté, roster, équipements, arbre de commandement), on réenregistre.
///
/// Le modèle édité est la <see cref="RunSave"/> ELLE-MÊME : chaque contrôle écrit directement dedans, et
/// « Enregistrer » ne fait que sérialiser. Pas de modèle intermédiaire à resynchroniser, donc pas de
/// divergence possible entre l'écran et le fichier. Les champs non exposés (récap de run) sont conservés
/// tels quels d'un chargement à l'autre.
/// </summary>
internal sealed class MainForm : Form
{
    // Fond sombre commun à l'outillage (cf. éditeur de maps).
    private static readonly Color Back = Color.FromArgb(38, 40, 46);
    private static readonly Color Panel = Color.FromArgb(45, 47, 54);
    private static readonly Color Ink = Color.Gainsboro;

    private RunSave _save = new();

    /// <summary>Vrai pendant qu'on remplit les contrôles depuis <see cref="_save"/> : neutralise les
    /// handlers de changement, qui écriraient sinon dans le modèle pendant sa propre lecture.</summary>
    private bool _loading;

    // ── Barre du haut ────────────────────────────────────────────────────────────
    private readonly ComboBox _slotBox = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolStripStatusLabel _status = new("Prêt.");

    // ── Partie ───────────────────────────────────────────────────────────────────
    private readonly ComboBox _phaseBox = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _missionBox = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _missionKind = new() { AutoSize = true, ForeColor = Color.Khaki };
    private readonly ComboBox _commanderBox = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _difficultyBox = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _seedNum = new() { Width = 180, Minimum = int.MinValue, Maximum = int.MaxValue };
    private readonly NumericUpDown _pointsNum = new() { Width = 70, Minimum = 0, Maximum = 999 };
    private readonly NumericUpDown _rerollNum = new() { Width = 70, Minimum = 0, Maximum = 99 };
    private readonly NumericUpDown _legendaryNum = new() { Width = 70, Minimum = 0, Maximum = 99 };
    private readonly NumericUpDown _rareNum = new() { Width = 70, Minimum = 0, Maximum = 99 };
    private readonly CheckBox _firstRunChk = new() { Text = "Première campagne (déblocage ennemi adouci)", AutoSize = true, ForeColor = Ink };

    // ── Roster ───────────────────────────────────────────────────────────────────
    private readonly ListView _rosterList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = false, BackColor = Panel, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly ComboBox _domaineBox = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _classBox = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _equipBox = new() { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _killsNum = new() { Width = 70, Minimum = 0, Maximum = 9999 };
    private readonly Label _rosterCount = new() { AutoSize = true, ForeColor = Color.Khaki };

    // ── Inventaire ───────────────────────────────────────────────────────────────
    private readonly ListBox _inventoryList = new()
    {
        Dock = DockStyle.Fill, BackColor = Panel, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly ComboBox _invPickBox = new() { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };

    // ── Arbre de commandement ────────────────────────────────────────────────────
    private readonly CheckedListBox _nodeList = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Panel, ForeColor = Ink,
        BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
    };
    private readonly Label _treeLabel = new() { AutoSize = true, ForeColor = Color.Khaki };

    public MainForm()
    {
        Text = "Éditeur de sauvegardes — Chess Army";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Back;
        ForeColor = Ink;

        foreach (var warning in Catalogs.LoadAll())
            _status.Text = warning;   // le dernier avertissement reste affiché ; l'outil reste utilisable

        BuildLayout();
        FillStaticChoices();
        WireEvents();

        _slotBox.SelectedIndex = 0;
        LoadSlot();
    }

    // ── Construction de l'interface ──────────────────────────────────────────────

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));

        root.Controls.Add(BuildLeftColumn(), 0, 0);
        root.Controls.Add(BuildRosterBox(), 1, 0);
        root.Controls.Add(BuildRightColumn(), 2, 0);

        Controls.Add(root);
        Controls.Add(BuildToolbar());
        Controls.Add(new StatusStrip { Items = { _status }, BackColor = Panel, ForeColor = Ink });
    }

    private ToolStrip BuildToolbar()
    {
        var bar = new ToolStrip { Dock = DockStyle.Top, BackColor = Panel, ForeColor = Ink, GripStyle = ToolStripGripStyle.Hidden };
        bar.Items.Add(new ToolStripLabel("Slot :") { ForeColor = Ink });
        bar.Items.Add(new ToolStripControlHost(_slotBox));
        bar.Items.Add(Tool("Charger", (_, _) => LoadSlot()));
        bar.Items.Add(Tool("Enregistrer", (_, _) => SaveSlot()));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Tool("Nouvelle run", (_, _) => ResetSave()));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Tool("Tout débloquer (profil)", (_, _) => UnlockEverything()));
        bar.Items.Add(Tool("Ouvrir le dossier", (_, _) => OpenSaveDir()));
        return bar;
    }

    private static ToolStripButton Tool(string text, EventHandler onClick)
    {
        var button = new ToolStripButton(text) { ForeColor = Ink };
        button.Click += onClick;
        return button;
    }

    private Control BuildLeftColumn()
    {
        var box = Group("Partie");
        var y = 26;
        Row(box, ref y, "Phase / mission", _phaseBox, _missionBox, _missionKind);
        Row(box, ref y, "Commandant", _commanderBox);
        Row(box, ref y, "Difficulté", _difficultyBox);
        Row(box, ref y, "Graine", _seedNum);
        Row(box, ref y, "Points de commandement", _pointsNum);
        Row(box, ref y, "Relances", _rerollNum);
        Row(box, ref y, "Pitié légendaire / rare", _legendaryNum, _rareNum);

        _firstRunChk.Location = new Point(12, y + 6);
        box.Controls.Add(_firstRunChk);
        y += 32;

        var hint = new Label
        {
            Text = "La run reprend en phase de PLACEMENT du combat choisi.\n"
                   + "Ferme le jeu avant d'enregistrer : sa sauvegarde\nautomatique écraserait ce fichier.",
            Location = new Point(12, y + 8), Size = new Size(310, 60), ForeColor = Color.Gray,
        };
        box.Controls.Add(hint);
        return box;
    }

    private Control BuildRosterBox()
    {
        var box = Group("Roster (pions de la run)");

        _rosterList.Columns.Add("Rôle", 70);
        _rosterList.Columns.Add("Domaine", 80);
        _rosterList.Columns.Add("Classe", 150);
        _rosterList.Columns.Add("Équipement", 190);
        _rosterList.Columns.Add("Tués", 55);

        var editor = new Panel { Dock = DockStyle.Bottom, Height = 120, BackColor = Back };
        var y = 4;
        Row(editor, ref y, "Domaine", _domaineBox);
        Row(editor, ref y, "Classe", _classBox);
        Row(editor, ref y, "Équipement", _equipBox, _killsNum);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Back, Padding = new Padding(8, 4, 0, 0) };
        buttons.Controls.Add(Button("Ajouter", (_, _) => AddUnit()));
        buttons.Controls.Add(Button("Dupliquer", (_, _) => DuplicateUnit()));
        buttons.Controls.Add(Button("Supprimer", (_, _) => RemoveUnit()));
        buttons.Controls.Add(_rosterCount);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 24, 8, 8) };
        host.Controls.Add(_rosterList);
        host.Controls.Add(editor);
        host.Controls.Add(buttons);
        box.Controls.Add(host);
        return box;
    }

    private Control BuildRightColumn()
    {
        var column = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

        var invBox = Group("Inventaire (équipements non portés)");
        var invHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 24, 8, 8) };
        var invBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Back };
        invBar.Controls.Add(_invPickBox);
        invBar.Controls.Add(Button("+", (_, _) => AddToInventory()));
        invBar.Controls.Add(Button("−", (_, _) => RemoveFromInventory()));
        invHost.Controls.Add(_inventoryList);
        invHost.Controls.Add(invBar);
        invBox.Controls.Add(invHost);

        var treeBox = Group("Arbre de commandement");
        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 24, 8, 8) };
        var treeBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Back };
        treeBar.Controls.Add(Button("Tout", (_, _) => SetAllNodes(true)));
        treeBar.Controls.Add(Button("Rien", (_, _) => SetAllNodes(false)));
        treeBar.Controls.Add(_treeLabel);
        treeHost.Controls.Add(_nodeList);
        treeHost.Controls.Add(treeBar);
        treeBox.Controls.Add(treeHost);

        column.Controls.Add(invBox, 0, 0);
        column.Controls.Add(treeBox, 0, 1);
        return column;
    }

    private static GroupBox Group(string title) => new()
    {
        Text = title, Dock = DockStyle.Fill, ForeColor = Color.Khaki, Margin = new Padding(4),
    };

    private static Button Button(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true, ForeColor = Ink, BackColor = Panel, FlatStyle = FlatStyle.Flat };
        button.Click += onClick;
        return button;
    }

    /// <summary>Une ligne « libellé : contrôles » empilée verticalement à partir de <paramref name="y"/>.</summary>
    private static void Row(Control host, ref int y, string label, params Control[] fields)
    {
        host.Controls.Add(new Label { Text = label, Location = new Point(12, y + 4), Size = new Size(150, 18), ForeColor = Ink });
        var x = 168;
        foreach (var field in fields)
        {
            field.Location = new Point(x, y);
            host.Controls.Add(field);
            x += field.Width + 8;
        }
        y += 30;
    }

    // ── Remplissage des listes fixes ─────────────────────────────────────────────

    private void FillStaticChoices()
    {
        for (var i = 0; i < GamePaths.SlotCount; i++)
            _slotBox.Items.Add($"Slot {i + 1}");

        for (var phase = 1; phase <= Run.PhaseCount; phase++)
            _phaseBox.Items.Add(phase);
        for (var mission = 1; mission <= Run.MissionsPerPhase; mission++)
            _missionBox.Items.Add(mission);

        foreach (var commander in Commandes.Playable)
            _commanderBox.Items.Add(new Item(commander.Id, $"{commander.Name} ({commander.Id})"));
        foreach (var level in DifficultySettings.AllLevels)
            _difficultyBox.Items.Add(new Item(level.ToString(), level.ToString()));

        foreach (var domaine in Domaines.All)
            _domaineBox.Items.Add(new Item(domaine.Id.ToString(), domaine.Id.ToString()));

        _equipBox.Items.Add(new Item("", "— aucun —"));
        foreach (var equipment in Catalogs.EquipmentsSorted())
        {
            _equipBox.Items.Add(new Item(equipment.Id, Describe(equipment)));
            _invPickBox.Items.Add(new Item(equipment.Id, Describe(equipment)));
        }
        if (_invPickBox.Items.Count > 0)
            _invPickBox.SelectedIndex = 0;
    }

    private static string Describe(Equipment equipment) => $"{equipment.Name} [{equipment.Rarity}]";

    private void WireEvents()
    {
        _phaseBox.SelectedIndexChanged += (_, _) => OnCombatChanged();
        _missionBox.SelectedIndexChanged += (_, _) => OnCombatChanged();
        _commanderBox.SelectedIndexChanged += (_, _) => OnCommanderChanged();
        _difficultyBox.SelectedIndexChanged += (_, _) => Edit(s =>
            s.Difficulty = Enum.Parse<Difficulty>(Key(_difficultyBox)!));
        _seedNum.ValueChanged += (_, _) => Edit(s => s.Seed = (int)_seedNum.Value);
        _pointsNum.ValueChanged += (_, _) => Edit(s => s.CommandPoints = (int)_pointsNum.Value);
        _rerollNum.ValueChanged += (_, _) => Edit(s => s.Rerolls = (int)_rerollNum.Value);
        _legendaryNum.ValueChanged += (_, _) => Edit(s => s.LegendaryPity = (int)_legendaryNum.Value);
        _rareNum.ValueChanged += (_, _) => Edit(s => s.RarePity = (int)_rareNum.Value);
        _firstRunChk.CheckedChanged += (_, _) => Edit(s => s.FirstRun = _firstRunChk.Checked);

        _rosterList.SelectedIndexChanged += (_, _) => BindSelectedUnit();
        _domaineBox.SelectedIndexChanged += (_, _) => OnDomaineChanged();
        _classBox.SelectedIndexChanged += (_, _) => EditUnit(u => u.Class = Key(_classBox)!);
        // Multi-slot (arbre du Marchand) : la liste déroulante édite le PREMIER slot ; les éventuels autres
        // équipements du pion sont conservés tels quels.
        _equipBox.SelectedIndexChanged += (_, _) => EditUnit(u =>
        {
            var ids = u.EquipmentIds ??= new List<string>();
            var id = Key(_equipBox) is { Length: > 0 } k ? k : null;
            if (id is null)
            {
                if (ids.Count > 0)
                    ids.RemoveAt(0);
            }
            else if (ids.Count > 0)
            {
                ids[0] = id;
            }
            else
            {
                ids.Add(id);
            }
        });
        _killsNum.ValueChanged += (_, _) => EditUnit(u => u.Kills = (int)_killsNum.Value);

        // ItemCheck part AUSSI depuis Items.Add(item, état) — d'où le garde _loading dans le handler. On lit
        // l'état visé dans l'événement plutôt que CheckedItems, qui n'est pas encore à jour à cet instant.
        _nodeList.ItemCheck += (_, e) => OnNodeChecked(e.Index, e.NewValue == CheckState.Checked);
    }

    // ── Chargement / enregistrement ──────────────────────────────────────────────

    private int SlotIndex => Math.Max(0, _slotBox.SelectedIndex);

    private void LoadSlot()
    {
        try
        {
            var loaded = SaveIo.LoadSlot(SlotIndex);
            if (loaded is null)
            {
                _save = NewSave();
                _status.Text = $"Slot {SlotIndex + 1} vide — nouvelle run préparée (non enregistrée).";
            }
            else
            {
                _save = loaded;
                _status.Text = $"Chargé : {GamePaths.SlotPath(SlotIndex)}";
            }
        }
        catch (Exception ex)
        {
            _save = NewSave();
            _status.Text = $"Lecture impossible ({ex.Message}) — nouvelle run préparée.";
        }
        BindAll();
    }

    private void SaveSlot()
    {
        if (!ConfirmTreeCoherence())
            return;
        try
        {
            _save.Version = 5;
            SaveIo.SaveSlot(SlotIndex, _save);
            _status.Text = $"Enregistré : {GamePaths.SlotPath(SlotIndex)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Enregistrement impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Le jeu ignore un nœud dont le prérequis n'est pas tenu ? Non : il le charge tel quel (seuls les ids
    /// INCONNUS sont écartés). Un arbre incohérent est donc jouable mais affiché de travers — on prévient
    /// sans bloquer, l'outil sert justement à forcer des états.
    /// </summary>
    private bool ConfirmTreeCoherence()
    {
        var tree = CurrentTree();
        var orphans = _save.CommandNodes
            .Select(tree.ById).Where(n => n != null).Select(n => n!)
            .Where(n => !tree.PrerequisiteMet(n, _save.CommandNodes))
            .Select(n => n.Id)
            .ToList();
        if (orphans.Count == 0)
            return true;

        return MessageBox.Show(this,
            "Ces nœuds n'ont pas leur prérequis de niveau inférieur :\n\n  "
            + string.Join("\n  ", orphans)
            + "\n\nLa run reste jouable, mais l'arbre s'affichera de travers.\nEnregistrer quand même ?",
            "Arbre incohérent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    /// <summary>Run neuve : le commandant par défaut + les pions de départ de sa définition.</summary>
    private static RunSave NewSave()
    {
        var commander = Commandes.Commander;
        var save = new RunSave { CombatNumber = 1, Seed = Environment.TickCount, FirstRun = false };
        save.Roster.Add(new UnitSpecSave
        {
            Domaine = commander.Movement, Class = commander.BaseClass.Asset, Essential = true,
        });
        foreach (var domaine in commander.StartingUnits)
            save.Roster.Add(new UnitSpecSave { Domaine = domaine, Class = Domaines.Of(domaine).BaseClass.Asset });
        save.CommanderId = commander.Id;
        return save;
    }

    private void ResetSave()
    {
        _save = NewSave();
        _status.Text = "Nouvelle run préparée (pas encore enregistrée).";
        BindAll();
    }

    // ── Liaison modèle → écran ───────────────────────────────────────────────────

    private void BindAll()
    {
        _loading = true;

        var combat = Math.Clamp(_save.CombatNumber, 1, Run.TotalCombats);
        _save.CombatNumber = combat;
        _phaseBox.SelectedIndex = (combat - 1) / Run.MissionsPerPhase;
        _missionBox.SelectedIndex = (combat - 1) % Run.MissionsPerPhase;
        // Une sauvegarde d'avant la v3 n'a pas d'id : on fige celui que le jeu déduirait, sinon l'écran
        // montrerait un commandant que le fichier ne nomme pas.
        _save.CommanderId = ResolvedCommander().Id;
        Select(_commanderBox, _save.CommanderId);
        Select(_difficultyBox, _save.Difficulty.ToString());
        _seedNum.Value = _save.Seed;
        _pointsNum.Value = Clamp(_pointsNum, _save.CommandPoints);
        _rerollNum.Value = Clamp(_rerollNum, _save.Rerolls);
        _legendaryNum.Value = Clamp(_legendaryNum, _save.LegendaryPity);
        _rareNum.Value = Clamp(_rareNum, _save.RarePity);
        _firstRunChk.Checked = _save.FirstRun;

        _loading = false;

        RefreshMissionKind();
        RefreshRoster();
        RefreshInventory();
        RefreshNodes();
    }

    private static decimal Clamp(NumericUpDown field, int value) =>
        Math.Clamp(value, (int)field.Minimum, (int)field.Maximum);

    /// <summary>Commandant du save : par id, sinon par l'asset de l'unité essentielle, sinon le défaut
    /// (même cascade que <c>Run.Restore</c>, pour que l'écran montre ce que le jeu chargerait).</summary>
    private CommandeDef ResolvedCommander()
    {
        if (Commandes.ById(_save.CommanderId) is { } byId)
            return byId;
        var asset = _save.Roster.FirstOrDefault(u => u.Essential)?.Class;
        return Commandes.Playable.FirstOrDefault(c => c.BaseClass.Asset == asset) ?? Commandes.Commander;
    }

    private CommandTree CurrentTree() => CommandTrees.For(ResolvedCommander());

    private void RefreshMissionKind()
    {
        var phase = _phaseBox.SelectedIndex + 1;
        var mission = _missionBox.SelectedIndex + 1;
        if (phase < 1 || mission < 1)
            return;   // une des deux listes n'a pas encore de sélection
        _missionKind.Text = $"{Run.MissionKindAt(phase, mission)} · combat {_save.CombatNumber}/{Run.TotalCombats}";
    }

    private void RefreshRoster()
    {
        var selected = _rosterList.SelectedIndices.Count > 0 ? _rosterList.SelectedIndices[0] : 0;
        _rosterList.BeginUpdate();
        _rosterList.Items.Clear();
        foreach (var unit in _save.Roster)
        {
            var equipment = unit.EquipmentIds is { Count: > 0 } ids
                ? string.Join(" + ", ids.Select(id => Equipments.ById(id)?.Name ?? $"? {id}"))
                : "—";
            _rosterList.Items.Add(new ListViewItem(new[]
            {
                unit.Essential ? "Commandant" : "Pion",
                unit.Domaine.ToString(),
                ClassNameOf(unit),
                equipment,
                unit.Kills.ToString(),
            }));
        }
        _rosterList.EndUpdate();

        if (_rosterList.Items.Count > 0)
            _rosterList.Items[Math.Clamp(selected, 0, _rosterList.Items.Count - 1)].Selected = true;

        var pawns = _save.Roster.Count(u => !u.Essential);
        var limit = ResolvedCommander().ReserveSize;
        _rosterCount.Text = $"    {pawns} pion(s) hors commandant — réserve de base : {limit}";
        _rosterCount.ForeColor = pawns > limit ? Color.IndianRed : Color.Khaki;
        BindSelectedUnit();
    }

    /// <summary>Nom lisible d'une entrée du roster, en repassant par le catalogue (l'asset seul ne parle pas).</summary>
    private static string ClassNameOf(UnitSpecSave unit)
    {
        if (unit.Essential)
            return Commandes.All.FirstOrDefault(c => c.BaseClass.Asset == unit.Class)?.Name ?? unit.Class;
        var cls = Catalogs.ClassesOf(unit.Domaine).FirstOrDefault(c => c.Asset == unit.Class);
        return cls is null ? $"? {unit.Class}" : $"T{cls.Tier} {cls.Name}";
    }

    private UnitSpecSave? Selected =>
        _rosterList.SelectedIndices.Count > 0 && _rosterList.SelectedIndices[0] < _save.Roster.Count
            ? _save.Roster[_rosterList.SelectedIndices[0]]
            : null;

    private void BindSelectedUnit()
    {
        var unit = Selected;
        var editable = unit is { Essential: false };
        // Le COMMANDANT peut porter un équipement depuis l'arbre du Marchand : sa case équipement reste éditable.

        _loading = true;
        _domaineBox.Enabled = editable;
        _classBox.Enabled = editable;
        _equipBox.Enabled = unit is not null;
        _killsNum.Enabled = unit is not null;

        if (unit is not null)
        {
            Select(_domaineBox, unit.Domaine.ToString());
            FillClassChoices(unit.Domaine);
            Select(_classBox, unit.Class);
            Select(_equipBox, unit.EquipmentIds is { Count: > 0 } worn ? worn[0] : "");
            _killsNum.Value = Clamp(_killsNum, unit.Kills);
        }
        _loading = false;
    }

    private void FillClassChoices(Domaine domaine)
    {
        _classBox.Items.Clear();
        foreach (var cls in Catalogs.ClassesOf(domaine))
            _classBox.Items.Add(new Item(cls.Asset, $"T{cls.Tier} {cls.Name}"));
    }

    private void RefreshInventory()
    {
        _inventoryList.Items.Clear();
        foreach (var id in _save.Inventory)
            _inventoryList.Items.Add(new Item(id, Equipments.ById(id) is { } e ? Describe(e) : $"? {id}"));
    }

    private void RefreshNodes()
    {
        var tree = CurrentTree();
        var owned = new HashSet<string>(_save.CommandNodes);

        _loading = true;
        _nodeList.Items.Clear();
        foreach (var node in tree.Nodes.OrderBy(n => n.Branch).ThenBy(n => n.Level))
            _nodeList.Items.Add(new Item(node.Id, $"B{node.Branch} · N{node.Level} · {node.Id} ({node.Cost} pts)"),
                owned.Contains(node.Id));
        _loading = false;

        _treeLabel.Text = $"    arbre « {tree.Id} » — {tree.Nodes.Count} nœuds";
    }

    // ── Écran → modèle ───────────────────────────────────────────────────────────

    private void Edit(Action<RunSave> change)
    {
        if (_loading)
            return;
        change(_save);
    }

    private void EditUnit(Action<UnitSpecSave> change)
    {
        if (_loading || Selected is not { } unit)
            return;
        change(unit);
        RefreshRoster();
    }

    /// <summary>
    /// Sortie ANTICIPÉE pendant un chargement : régler la phase déclenche l'événement AVANT que la mission
    /// soit posée (elle vaut encore -1), et le couple serait alors hors grille. <see cref="BindAll"/>
    /// rafraîchit lui-même le libellé une fois les deux listes réglées.
    /// </summary>
    private void OnCombatChanged()
    {
        if (_loading)
            return;
        _save.CombatNumber = _phaseBox.SelectedIndex * Run.MissionsPerPhase + _missionBox.SelectedIndex + 1;
        RefreshMissionKind();
    }

    /// <summary>
    /// Changer de commandant réécrit l'unité ESSENTIELLE du roster (classe + domaine de déplacement) : c'est
    /// elle qui est posée sur le plateau. L'arbre change avec lui, donc les nœuds achetés d'un autre arbre
    /// n'ont plus de sens — on les vide plutôt que de laisser une liste que le jeu ignorerait en silence.
    /// </summary>
    private void OnCommanderChanged()
    {
        if (_loading || Key(_commanderBox) is not { } id || Commandes.ById(id) is not { } def)
            return;

        _save.CommanderId = def.Id;
        var essential = _save.Roster.FirstOrDefault(u => u.Essential);
        if (essential is null)
        {
            essential = new UnitSpecSave { Essential = true };
            _save.Roster.Insert(0, essential);
        }
        essential.Domaine = def.Movement;
        essential.Class = def.BaseClass.Asset;
        essential.EquipmentIds = new List<string>();

        _save.CommandNodes.Clear();
        RefreshRoster();
        RefreshNodes();
    }

    private void OnDomaineChanged()
    {
        if (_loading || Selected is not { Essential: false } unit || Key(_domaineBox) is not { } name)
            return;

        unit.Domaine = Enum.Parse<Domaine>(name);
        unit.Class = Domaines.Of(unit.Domaine).BaseClass.Asset;   // l'ancienne classe n'existe pas dans le nouvel arbre
        RefreshRoster();
    }

    private void AddUnit()
    {
        var domaine = Domaines.All[0].Id;
        _save.Roster.Add(new UnitSpecSave { Domaine = domaine, Class = Domaines.Of(domaine).BaseClass.Asset });
        RefreshRoster();
        _rosterList.Items[^1].Selected = true;
    }

    private void DuplicateUnit()
    {
        if (Selected is not { Essential: false } unit)
            return;
        _save.Roster.Add(new UnitSpecSave
        {
            Domaine = unit.Domaine, Class = unit.Class, Kills = unit.Kills,
            EquipmentIds = unit.EquipmentIds is { } src ? new List<string>(src) : null,
        });
        RefreshRoster();
        _rosterList.Items[^1].Selected = true;
    }

    private void RemoveUnit()
    {
        if (Selected is not { Essential: false } unit)
        {
            _status.Text = "Le commandant ne peut pas être retiré du roster.";
            return;
        }
        _save.Roster.Remove(unit);
        RefreshRoster();
    }

    private void AddToInventory()
    {
        if (Key(_invPickBox) is { } id)
        {
            _save.Inventory.Add(id);
            RefreshInventory();
        }
    }

    private void RemoveFromInventory()
    {
        if (_inventoryList.SelectedIndex >= 0)
        {
            _save.Inventory.RemoveAt(_inventoryList.SelectedIndex);
            RefreshInventory();
        }
    }

    private void OnNodeChecked(int index, bool owned)
    {
        if (_loading || index < 0 || _nodeList.Items[index] is not Item node)
            return;
        _save.CommandNodes.Remove(node.Key);
        if (owned)
            _save.CommandNodes.Add(node.Key);
    }

    /// <summary>Coche/décoche tout. Chaque bascule remonte par <see cref="OnNodeChecked"/> : rien à
    /// resynchroniser derrière (une case déjà dans l'état visé ne lève pas d'événement, et c'est correct).</summary>
    private void SetAllNodes(bool on)
    {
        for (var i = 0; i < _nodeList.Items.Count; i++)
            _nodeList.SetItemChecked(i, on);
    }

    // ── Profil (méta-progression) ────────────────────────────────────────────────

    /// <summary>
    /// Ouvre tout le codex et rend tous les commandants jouables. Sans ça, tester une évolution ou un
    /// commandant récent impose de le débloquer en jeu — c'est justement ce qu'on veut éviter.
    /// </summary>
    private void UnlockEverything()
    {
        try
        {
            var profile = SaveIo.LoadProfile();
            profile.HasPlayedBefore = true;
            profile.DiscoveredUnits = Domaines.All
                .SelectMany(d => Catalogs.ClassesOf(d.Id))
                .Select(c => c.Asset)
                .Concat(Commandes.All.Select(c => c.BaseClass.Asset))
                .Distinct()
                .ToList();
            profile.DiscoveredEquipment = Equipments.All.Select(e => e.Id).ToList();
            profile.UnlockedCommanders = Commandes.Playable.Select(c => c.Id).ToList();
            SaveIo.SaveProfile(profile);
            _status.Text = $"Profil débloqué : {profile.DiscoveredUnits.Count} unités, "
                           + $"{profile.DiscoveredEquipment.Count} équipements, "
                           + $"{profile.UnlockedCommanders.Count} commandants.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Profil non écrit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSaveDir()
    {
        try
        {
            System.IO.Directory.CreateDirectory(GamePaths.SaveDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GamePaths.SaveDir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _status.Text = $"Ouverture du dossier impossible : {ex.Message}";
        }
    }

    // ── Entrées de liste (clé technique + libellé lisible) ───────────────────────

    private sealed record Item(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static string? Key(ComboBox box) => (box.SelectedItem as Item)?.Key;

    private static void Select(ComboBox box, string key)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is Item item && item.Key == key)
            {
                box.SelectedIndex = i;
                return;
            }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }
}
