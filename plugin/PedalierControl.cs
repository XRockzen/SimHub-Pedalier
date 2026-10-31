using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace User.PedalierDIY
{
    /// <summary>
    /// Page du plugin dans SimHub. Toute la logique (calibration, courbes, filtres) est dans le
    /// pédalier : cette page lit et écrit ses réglages, lance les calibrations et affiche le direct.
    ///
    /// _device = dernier état connu du pédalier, _draft = état affiché et modifié ici.
    /// Une modification part en RAM du pédalier (aperçu immédiat) ; seul "Enregistrer" écrit l'EEPROM.
    /// </summary>
    internal sealed class PedalierControl : UserControl
    {
        private readonly PedalierPlugin _plugin;
        private PedalConfig _draft, _device;
        private bool _busy, _unsaved, _suppress, _diagInFlight, _visible;
        private LiveData _lastLive;
        private LinkState _lastState = LinkState.Disconnected;
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly DispatcherTimer _tick, _pushTimer, _diagTimer;

        // Barre du haut
        private readonly Ellipse _dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _status = Ui.T("", 13);
        private readonly ComboBox _portBox = new ComboBox { Width = 130, Margin = new Thickness(12, 0, 8, 0) };
        private Button _connectBtn;

        // Barre du bas
        private CheckBox _liveApply;
        private Button _sendBtn, _saveBtn, _undoBtn, _factoryBtn;
        private readonly TextBlock _msg = Ui.T("", 12, Ui.Muted);
        private readonly TextBlock _unsavedLbl = Ui.T("● Modifications non enregistrées dans le pédalier", 12, Ui.Amber);

        // Onglets
        private TabControl _tabs;
        private TabItem _liveTab, _calTab, _setTab, _diagTab;

        // En direct
        private readonly TextBlock[] _bigPct = new TextBlock[2];
        private readonly TextBlock[] _sensorState = new TextBlock[2];
        private readonly LevelBar[] _rawBar = new LevelBar[2], _calBar = new LevelBar[2], _outBar = new LevelBar[2];
        private readonly TextBlock _streamInfo = Ui.T("", 12, Ui.Muted);

        // Réglages
        private readonly CurveEditor[] _curves = new CurveEditor[2];
        private readonly TextBlock[] _curveName = new TextBlock[2];

        // Calibration
        private readonly TextBlock _calSummary = Ui.T("", 12, Ui.Muted);
        private readonly TextBlock _peakInfo = Ui.T("", 12, Ui.Amber);
        private readonly StackPanel _linRows = new StackPanel();
        private readonly ComboBox _linCount = new ComboBox { Width = 150 };
        private readonly TextBlock _linState = Ui.T("", 12);
        private DateTime _peakUntil;
        private bool _peakActive;
        private int _peakValue;

        // Profils
        private readonly ListBox _profiles = new ListBox { Height = 220, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _profileName = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 0) };

        // Diagnostic
        private readonly Grid _diagGrid = new Grid();

        // Mises à jour
        private Border _banner;
        private readonly TextBlock _bannerText = Ui.T("", 13);
        private Button _bannerAction, _bannerLater;
        private readonly TextBlock _verPlugin = Ui.T("", 12), _verFw = Ui.T("", 12), _updStatus = Ui.T("", 12, Ui.Muted);
        private Button _checkBtn, _flashBtn;

        public PedalierControl(PedalierPlugin plugin)
        {
            _plugin = plugin;
            Content = BuildLayout();

            // État de la liaison : 10 fois par seconde suffit. Le direct, lui, est redessiné à
            // chaque image de l'écran (OnFrame), et seulement quand une nouvelle mesure est arrivée.
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += (s, e) => OnTick();
            _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _pushTimer.Tick += (s, e) => { _pushTimer.Stop(); PushDraft(false); };
            _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _diagTimer.Tick += (s, e) => PollDiag();

            Loaded += (s, e) =>
            {
                _tick.Start();
                _diagTimer.Start();
                CompositionTarget.Rendering += OnFrame;
                _visible = true;
                UpdateStreamRate();
            };
            Unloaded += (s, e) =>
            {
                _tick.Stop();
                _diagTimer.Stop();
                CompositionTarget.Rendering -= OnFrame;
                _visible = false;
                UpdateStreamRate();
            };
            Updater.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshUpdateUi));
            RefreshProfiles();
            FillUi();
            RefreshUpdateUi();
        }

        private PedalLink Link { get { return _plugin.Link; } }

        // ═══════════════════════════ Construction ═══════════════════════════

        private UIElement BuildLayout()
        {
            var root = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

            // Haut : état de la liaison
            _portBox.DropDownOpened += (s, e) => FillPorts();
            _portBox.SelectionChanged += (s, e) =>
            {
                if (_suppress || _portBox.SelectedItem == null) return;
                var p = _portBox.SelectedIndex == 0 ? "AUTO" : (string)_portBox.SelectedItem;
                _plugin.Settings.Port = p;
                Link.PreferredPort = p;
                _plugin.SaveSettings();
            };
            FillPorts();
            _connectBtn = Ui.Btn("Connecter", ToggleConnect, 110);
            var top = Ui.Row(_dot, _status, _portBox, _connectBtn);
            top.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // Bandeau de mise à jour (plugin ou firmware)
            _bannerAction = Ui.Btn("Installer", BannerAction);
            _bannerLater = Ui.Btn("Plus tard", () => { Updater.Dismissed = true; RefreshUpdateUi(); });
            var bannerRow = new DockPanel();
            var bannerBtns = Ui.Row(_bannerAction, _bannerLater);
            DockPanel.SetDock(bannerBtns, Dock.Right);
            bannerRow.Children.Add(bannerBtns);
            bannerRow.Children.Add(_bannerText);
            _banner = new Border
            {
                Background = Ui.Frozen(Color.FromArgb(40, 0xFF, 0xC8, 0x57)), BorderBrush = Ui.Amber, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 10),
                Child = bannerRow, Visibility = Visibility.Collapsed,
            };
            DockPanel.SetDock(_banner, Dock.Top);
            root.Children.Add(_banner);

            // Bas : appliquer / enregistrer
            _liveApply = new CheckBox { Content = "Appliquer en direct", IsChecked = _plugin.Settings.LiveApply, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), Foreground = Ui.Text };
            _liveApply.Click += (s, e) => { _plugin.Settings.LiveApply = _liveApply.IsChecked == true; _plugin.SaveSettings(); UpdateButtons(); };
            _sendBtn = Ui.Btn("Envoyer", () => PushDraft(true));
            _saveBtn = Ui.Btn("Enregistrer dans le pédalier", SaveToDevice);
            _undoBtn = Ui.Btn("Annuler", UndoChanges);
            _factoryBtn = Ui.Btn("Valeurs d'usine", FactoryReset);
            _sendBtn.ToolTip = "Envoie les réglages au pédalier pour les essayer (en mémoire vive : perdus au débranchement)";
            _saveBtn.ToolTip = "Écrit les réglages dans la mémoire permanente du pédalier : ils restent actifs SimHub fermé";
            _undoBtn.ToolTip = "Revient aux réglages enregistrés dans le pédalier";
            _factoryBtn.ToolTip = "Remet les réglages d'usine (à enregistrer ensuite pour les garder)";
            var bottom = Ui.Stack(Ui.Row(_liveApply, _sendBtn, _saveBtn, _undoBtn, _factoryBtn), Ui.Row(_unsavedLbl), _msg);
            ((StackPanel)bottom.Children[1]).Margin = new Thickness(0, 8, 0, 0);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            // Fond transparent : le texte clair reste lisible même si le thème de SimHub ne peint pas les onglets
            _tabs = new TabControl { Background = Brushes.Transparent, BorderBrush = Ui.Line };
            _liveTab = Tab("En direct", BuildLiveTab());
            _calTab = Tab("Calibration", BuildCalibrationTab());
            _setTab = Tab("Réglages", BuildSettingsTab());
            Tab("Profils", BuildProfilesTab());
            _diagTab = Tab("Diagnostic", BuildDiagTab());
            // Les SelectionChanged des listes et menus internes remontent aussi jusqu'ici : on filtre
            _tabs.SelectionChanged += (s, e) => { if (e.OriginalSource == _tabs) UpdateStreamRate(); };
            root.Children.Add(_tabs);
            return root;
        }

        private TabItem Tab(string header, UIElement content)
        {
            var t = new TabItem
            {
                Header = header,
                Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 10, 4, 4) },
            };
            _tabs.Items.Add(t);
            return t;
        }

        private UIElement AxisTitle(int axis)
        {
            return Ui.Row(new Border { Width = 4, Height = 18, Background = Ui.AxisBrush(axis), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) },
                          Ui.T(Ui.AxisLabel(axis), 15, Ui.Text, true));
        }

        private UIElement BuildLiveTab()
        {
            Func<int, UIElement> col = a =>
            {
                _bigPct[a] = Ui.T("–", 34, Ui.AxisBrush(a), true);
                _sensorState[a] = Ui.T("", 12, Ui.Muted);
                _rawBar[a] = new LevelBar("Brut", Ui.Faint);
                _calBar[a] = new LevelBar("Calibré", Ui.Muted);
                _outBar[a] = new LevelBar("Envoyé au jeu", Ui.AxisBrush(a));
                return Ui.Card(Ui.Stack(AxisTitle(a), _bigPct[a], _sensorState[a], _rawBar[a], _calBar[a], _outBar[a]));
            };
            return Ui.Stack(Ui.TwoColumns(col(0), col(1)), _streamInfo,
                Ui.T("Brut : valeur du capteur. Calibré : position après calibration, linéarisation et zones mortes. Envoyé au jeu : après la courbe de réponse (0-4095).", 11, Ui.Faint));
        }

        private UIElement BuildCalibrationTab()
        {
            // Assistant : trois points, chacun refaisable seul
            var steps = Ui.Stack(
                Ui.T("Assistant de calibration", 15, Ui.Text, true),
                Ui.T("Chaque étape peut être refaite seule. Rien n'est gardé au débranchement tant que tu n'as pas cliqué « Enregistrer dans le pédalier ».", 12, Ui.Muted),
                Step("1", "Relâche complètement les deux pédales, pieds levés.", "Capturer le zéro", () => CalCommands("Zéro des deux pédales", "CAL ZERO ACC", "CAL ZERO BRK")),
                Step("2", "Appuie l'accélérateur à fond, contre la butée, et maintiens-le.", "Capturer le 100 % accélérateur", () => CalCommands("100 % accélérateur", "CAL MAX ACC")),
                Step("3", "Appuie le frein à la force qui doit donner 100 % et maintiens-la.", "Capturer le 100 % frein", () => CalCommands("100 % frein", "CAL MAX BRK")),
                Ui.Row(Ui.T("…ou, si la force est difficile à tenir :", 12, Ui.Muted), Ui.Btn("Mesurer le pic du frein (3 s)", StartPeak)),
                _peakInfo);
            ((StackPanel)steps.Children[steps.Children.Count - 2]).Margin = new Thickness(26, 2, 0, 4);

            // Valeurs actuelles, modifiables à la main
            var values = Ui.Stack(Ui.T("Valeurs de calibration", 15, Ui.Text, true), _calSummary,
                RawEdit(0, "Accélérateur 0 %", c => c.Acc.Min, (c, v) => c.Acc.Min = v, 0, 4095),
                RawEdit(0, "Accélérateur 100 %", c => c.Acc.Max, (c, v) => c.Acc.Max = v, 0, 4095),
                RawEdit(1, "Frein 0 %", c => c.Brk.Min, (c, v) => c.Brk.Min = v, -8388607, 8388607),
                RawEdit(1, "Frein 100 %", c => c.Brk.Max, (c, v) => c.Brk.Max = v, -8388607, 8388607),
                Ui.T("Le zéro du frein se recale tout seul quand le frein est relâché et stable (re-tare automatique, onglet Réglages).", 11, Ui.Faint));

            // Linéarisation de l'accélérateur
            for (int n = 2; n <= 11; n++) _linCount.Items.Add(n == 2 ? "2 (aucune correction)" : n + " points");
            var lin = Ui.Stack(
                Ui.T("Linéarisation de l'accélérateur", 15, Ui.Text, true),
                Ui.T("Corrige la non-linéarité de la bielle. Choisis le nombre de points, crée la table, puis amène la pédale à chaque angle repère (gabarit, rapporteur…) et capture. Les points sont répartis régulièrement sur la course angulaire de la pédale. À refaire après une nouvelle calibration de l'accélérateur.", 12, Ui.Muted),
                Ui.Row(_linCount, Ui.Btn("Créer la table", NewLinTable)),
                _linState, _linRows);
            ((StackPanel)lin.Children[2]).Margin = new Thickness(0, 6, 0, 6);
            _linCount.Margin = new Thickness(0, 0, 8, 0);

            return Ui.Stack(Ui.Card(steps), Ui.TwoColumns(Ui.Card(values), Ui.Card(lin)));
        }

        private UIElement Step(string num, string text, string button, Action click)
        {
            var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Ui.Track, Margin = new Thickness(0, 0, 8, 0), Child = Ui.T(num, 11, Ui.Text, true) };
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 460 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Ui.T(text, 13);
            var b = Ui.Btn(button, click, 230);
            Grid.SetColumn(t, 1);
            Grid.SetColumn(b, 2);
            g.Children.Add(badge);
            g.Children.Add(t);
            g.Children.Add(b);
            return g;
        }

        private UIElement RawEdit(int axis, string label, Func<PedalConfig, int> get, Action<PedalConfig, int> set, int min, int max)
        {
            var box = new TextBox { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
            Action commit = () =>
            {
                if (_suppress || _draft == null) return;
                int v;
                if (int.TryParse(box.Text.Trim(), out v) && v >= min && v <= max)
                {
                    if (v != get(_draft)) { set(_draft, v); OnEdited(); }
                }
                else { Msg(label + " : valeur entre " + min + " et " + max, Ui.Red); box.Text = get(_draft).ToString(); }
            };
            box.LostFocus += (s, e) => commit();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            _refreshers.Add(() => box.Text = _draft == null ? "" : get(_draft).ToString());
            var l = Ui.T(label, 12, Ui.Muted);
            l.Width = 130;
            var row = Ui.Row(l, box);
            row.Margin = new Thickness(0, 4, 0, 0);
            return row;
        }

        private UIElement BuildSettingsTab()
        {
            return Ui.TwoColumns(AxisSettings(0), AxisSettings(1));
        }

        private UIElement AxisSettings(int a)
        {
            _curves[a] = new CurveEditor(Ui.AxisBrush(a), 270);
            _curves[a].HorizontalAlignment = HorizontalAlignment.Left;
            _curves[a].CurveChanged += c => { if (_draft == null) return; _draft.Axis(a).Curve = c; UpdateCurveName(a); OnEdited(); };
            _refreshers.Add(() => { if (_draft != null) _curves[a].Curve = _draft.Axis(a).Curve; UpdateCurveName(a); });
            _curveName[a] = Ui.T("", 12, Ui.Muted);

            var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
            for (int p = 0; p < Curves.Names.Length; p++)
            {
                int preset = p;
                var b = Ui.Btn(Curves.Names[p], () => ApplyPreset(a, preset));
                b.Margin = new Thickness(0, 0, 6, 6);
                b.Padding = new Thickness(8, 2, 8, 2);
                presets.Children.Add(b);
            }

            var s = Ui.Stack(AxisTitle(a),
                Ui.T("Courbe de réponse", 13, Ui.Text, true), _curveName[a], _curves[a], presets,
                Ui.T("Glisse les points à la souris. Le point jaune montre la pédale en direct.", 11, Ui.Faint),
                Ui.T("Zones mortes et filtrage", 13, Ui.Text, true),
                SliderRow("Zone morte début", 0, 20, 0.1, c => c.Axis(a).Dzl / 100.0, (c, v) => c.Axis(a).Dzl = (int)Math.Round(v * 100), v => v.ToString("0.0") + " %"),
                SliderRow("Zone morte fin", 0, 20, 0.1, c => c.Axis(a).Dzh / 100.0, (c, v) => c.Axis(a).Dzh = (int)Math.Round(v * 100), v => v.ToString("0.0") + " %"),
                SliderRow("Lissage", 0, 100, 1, c => c.Axis(a).Tau, (c, v) => c.Axis(a).Tau = (int)v, v => v == 0 ? "aucun" : v.ToString("0") + " ms"),
                SliderRow("Hystérésis", 0, 50, 1, c => c.Axis(a).Hys, (c, v) => c.Axis(a).Hys = (int)v, v => v.ToString("0") + " pas"));
            ((TextBlock)s.Children[6]).Margin = new Thickness(0, 14, 0, 4);
            ((TextBlock)s.Children[1]).Margin = new Thickness(0, 8, 0, 0);

            if (a == PedalConfig.BrkIndex)
            {
                var t = Ui.T("Frein : capteur", 13, Ui.Text, true);
                t.Margin = new Thickness(0, 14, 0, 4);
                s.Children.Add(t);
                s.Children.Add(ComboRow("Filtre médian", new[] { 1, 3, 5 }, new[] { "aucun", "3 mesures", "5 mesures" }, c => c.BrkMed, (c, v) => c.BrkMed = v));
                s.Children.Add(ComboRow("Mesures par seconde", new[] { 80, 320 }, new[] { "80 (moins de bruit)", "320 (plus réactif)" }, c => c.BrkSps, (c, v) => c.BrkSps = v));
                s.Children.Add(SliderRow("Re-tare : écart max", 0, 10, 0.5, c => c.TareBand / 100.0, (c, v) => c.TareBand = (int)Math.Round(v * 100), v => v == 0 ? "désactivée" : v.ToString("0.0") + " %"));
                s.Children.Add(SliderRow("Re-tare : stable pendant", 0.5, 20, 0.5, c => c.TareMs / 1000.0, (c, v) => c.TareMs = (int)Math.Round(v * 1000), v => v.ToString("0.0") + " s"));
                s.Children.Add(Ui.T("Re-tare : quand le frein est relâché, stable et à moins de cet écart du zéro, le zéro se recale (fluage des élastomères).", 11, Ui.Faint));
            }
            else
            {
                s.Children.Add(Ui.T("Hystérésis : 1 pas du capteur ≈ 6 pas de sortie ; au-dessus de 6, la valeur reste figée quand la pédale est immobile.", 11, Ui.Faint));
            }
            return Ui.Card(s);
        }

        private UIElement SliderRow(string label, double min, double max, double step, Func<PedalConfig, double> get, Action<PedalConfig, double> set, Func<double, string> fmt)
        {
            var sl = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Width = 200, VerticalAlignment = VerticalAlignment.Center, SmallChange = step, LargeChange = step * 10 };
            var v = Ui.T(fmt(min), 12);
            v.Margin = new Thickness(10, 0, 0, 0);
            sl.ValueChanged += (s, e) =>
            {
                v.Text = fmt(sl.Value);
                if (_suppress || _draft == null) return;
                set(_draft, sl.Value);
                OnEdited();
            };
            _refreshers.Add(() => { sl.Value = _draft == null ? min : get(_draft); v.Text = fmt(sl.Value); });
            var l = Ui.T(label, 12, Ui.Muted);
            l.Width = 150;
            var row = Ui.Row(l, sl, v);
            row.Margin = new Thickness(0, 3, 0, 3);
            return row;
        }

        private UIElement ComboRow(string label, int[] values, string[] names, Func<PedalConfig, int> get, Action<PedalConfig, int> set)
        {
            var cb = new ComboBox { Width = 200 };
            foreach (var n in names) cb.Items.Add(n);
            cb.SelectionChanged += (s, e) =>
            {
                if (_suppress || _draft == null || cb.SelectedIndex < 0) return;
                set(_draft, values[cb.SelectedIndex]);
                OnEdited();
            };
            _refreshers.Add(() => cb.SelectedIndex = _draft == null ? -1 : Array.IndexOf(values, get(_draft)));
            var l = Ui.T(label, 12, Ui.Muted);
            l.Width = 150;
            var row = Ui.Row(l, cb);
            row.Margin = new Thickness(0, 3, 0, 3);
            return row;
        }

        private UIElement BuildProfilesTab()
        {
            _profiles.SelectionChanged += (s, e) => { if (_profiles.SelectedItem != null) _profileName.Text = (string)_profiles.SelectedItem; };
            _profiles.MouseDoubleClick += (s, e) => LoadProfile();
            var save = Ui.Row(_profileName, Ui.Btn("Enregistrer les réglages actuels", SaveProfile));
            save.Margin = new Thickness(0, 8, 0, 8);
            var actions = Ui.Row(Ui.Btn("Charger", LoadProfile), Ui.Btn("Supprimer", DeleteProfile), Ui.Btn("Exporter…", ExportProfile), Ui.Btn("Importer…", ImportProfile));
            actions.Margin = new Thickness(0, 8, 0, 0);
            return Ui.Card(Ui.Stack(
                Ui.T("Profils (sur ce PC)", 15, Ui.Text, true),
                Ui.T("Un profil garde le ressenti : courbes, zones mortes, lissage, hystérésis et filtres du frein. La calibration et la linéarisation restent celles du pédalier. Charger un profil l'envoie au pédalier ; « Enregistrer dans le pédalier » pour qu'il reste actif SimHub fermé.", 12, Ui.Muted),
                save, _profiles, actions));
        }

        private UIElement BuildDiagTab()
        {
            _diagGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            _diagGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _checkBtn = Ui.Btn("Vérifier les mises à jour", () => { Updater.Dismissed = false; Updater.CheckInBackground(); });
            _flashBtn = Ui.Btn("Mettre à jour le pédalier", FlashFirmware);
            var btns = Ui.Row(_checkBtn, _flashBtn);
            btns.Margin = new Thickness(0, 8, 0, 4);
            var updates = Ui.Card(Ui.Stack(Ui.T("Versions et mises à jour", 15, Ui.Text, true), _verPlugin, _verFw, btns, _updStatus,
                Ui.T("La mise à jour du pédalier passe par le câble USB, comme depuis l'IDE Arduino. Tes réglages enregistrés sont conservés. En cas d'échec, relance-la : le pédalier ne risque rien.", 11, Ui.Faint)));
            return Ui.Stack(updates, Ui.Card(Ui.Stack(Ui.T("Diagnostic (mis à jour chaque seconde)", 15, Ui.Text, true), _diagGrid)));
        }

        // ═══════════════════════════ Rafraîchissement ═══════════════════════════

        private void FillPorts()
        {
            _suppress = true;
            _portBox.Items.Clear();
            _portBox.Items.Add("Automatique");
            string[] names;
            try { names = SerialPort.GetPortNames(); } catch { names = new string[0]; }
            foreach (var n in names.Distinct().OrderBy(n => n.Length).ThenBy(n => n)) _portBox.Items.Add(n);
            var pref = _plugin.Settings.Port;
            if (pref != "AUTO" && !_portBox.Items.Contains(pref)) _portBox.Items.Add(pref);
            _portBox.SelectedItem = pref == "AUTO" ? (object)"Automatique" : pref;
            _suppress = false;
        }

        /// <summary>Recopie _draft dans tous les contrôles, sans déclencher d'envoi.</summary>
        private void FillUi()
        {
            _suppress = true;
            foreach (var r in _refreshers) r();
            _suppress = false;
            UpdateCalSummary();
            RebuildLinRows();
            UpdateButtons();
        }

        private void UpdateCurveName(int a)
        {
            if (_draft == null) { _curveName[a].Text = ""; return; }
            int p = Curves.Match(_draft.Axis(a).Curve);
            _curveName[a].Text = p < 0 ? "Courbe libre" : "Préréglage : " + Curves.Names[p];
        }

        private void UpdateCalSummary()
        {
            if (_draft == null) { _calSummary.Text = "Pédalier non connecté."; return; }
            int accSpan = Math.Abs(AngleDelta(_draft.Acc.Max, _draft.Acc.Min));
            long brkSpan = Math.Abs((long)_draft.Brk.Max - _draft.Brk.Min);
            _calSummary.Text = "Course accélérateur : " + accSpan + " pas du capteur.   Course frein : " + brkSpan.ToString("N0") + " points.";
        }

        private void RebuildLinRows()
        {
            _linRows.Children.Clear();
            if (_draft == null) { _linState.Text = ""; return; }
            var lin = _draft.Lin;
            int n = lin.Length;
            _suppress = true;
            _linCount.SelectedIndex = n - 2;
            _suppress = false;
            bool valid = _draft.LinValid();
            _linState.Text = n == 2 ? "Aucune correction : la pédale suit directement l'angle de l'aimant."
                           : valid ? "Table valide : la correction est active." : "Table incomplète ou non croissante : correction ignorée.";
            _linState.Foreground = n == 2 || valid ? Ui.Muted : Ui.Amber;
            if (n == 2) return;
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var l = Ui.T("Point " + i + " : pédale à " + (i * 100.0 / (n - 1)).ToString("0.#") + " %", 12);
                l.Width = 170;
                var v = Ui.T("mesuré " + (lin[i] / 100.0).ToString("0.00") + " %", 12, Ui.Muted);
                v.Width = 120;
                var b = Ui.Btn("Capturer", () => CalCommands("point " + idx + " de linéarisation", "CAL LIN " + idx));
                b.Padding = new Thickness(8, 2, 8, 2);
                var row = Ui.Row(l, v, b);
                row.Margin = new Thickness(0, 3, 0, 3);
                _linRows.Children.Add(row);
            }
        }

        private void UpdateButtons()
        {
            bool on = Link.State == LinkState.Connected && _draft != null;
            _sendBtn.IsEnabled = on && !_busy && _liveApply.IsChecked != true;
            _saveBtn.IsEnabled = on && !_busy;
            _undoBtn.IsEnabled = on && !_busy;
            _factoryBtn.IsEnabled = on && !_busy;
            _unsavedLbl.Visibility = _unsaved && on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTick()
        {
            var st = Link.State;
            if (st != _lastState)
            {
                _lastState = st;
                // Pendant une mise à jour du firmware, c'est elle qui relit les réglages à la fin
                if (st == LinkState.Connected && !_busy) ReloadFromDevice("Pédalier connecté.");
                UpdateButtons();
                RefreshUpdateUi();
            }
            _dot.Fill = st == LinkState.Connected ? Ui.Green : st == LinkState.Searching ? Ui.Amber : Ui.Red;
            if (st == LinkState.Connected) _status.Text = "Connecté · " + Link.PortName + " · firmware " + Link.Firmware;
            else if (st == LinkState.Searching) _status.Text = "Recherche du pédalier…";
            else _status.Text = "Déconnecté" + (Link.LastError != null ? " (" + Link.LastError + ")" : "");
            _connectBtn.Content = Link.WantConnected ? "Déconnecter" : "Connecter";
            _streamInfo.Text = st == LinkState.Connected ? "Données en direct : " + Link.StreamHz + " mesures/s" : "";
        }

        /// <summary>
        /// Flux rapide (100 mesures/s) seulement quand un écran qui montre la pédale en direct est
        /// affiché ; partout ailleurs, flux de repos (30/s) pour les propriétés SimHub.
        /// </summary>
        private void UpdateStreamRate()
        {
            bool fast = _visible && (_tabs.SelectedItem == _liveTab || _tabs.SelectedItem == _setTab || _peakActive);
            Link.SetStreamRate(fast ? PedalLink.FastStreamHz : PedalLink.IdleStreamHz);
        }

        /// <summary>Appelé à chaque image de l'écran : ne redessine que si une mesure est arrivée.</summary>
        private void OnFrame(object sender, EventArgs e)
        {
            var d = Link.Live;
            if (ReferenceEquals(d, _lastLive)) return;
            _lastLive = d;
            UpdateLive(d);
        }

        private void UpdateLive(LiveData d)
        {
            bool live = Link.State == LinkState.Connected;
            for (int a = 0; a < 2; a++)
            {
                bool ok = live && (a == 0 ? d.AccOk : d.BrkOk);
                int raw = a == 0 ? d.AccRaw : d.BrkRaw, cal = a == 0 ? d.AccCal : d.BrkCal, outv = a == 0 ? d.AccOut : d.BrkOut;
                _bigPct[a].Text = live ? Ui.Pct(outv / 4095.0) : "–";
                _sensorState[a].Text = !live ? "Pas de données" : !ok ? "Capteur absent ou en défaut : sortie à 0 %"
                                     : a == 0 && d.MagnetWarning ? "Capteur OK · aimant trop loin ou trop près" : "Capteur OK";
                _sensorState[a].Foreground = !live ? Ui.Muted : !ok ? Ui.Red : a == 0 && d.MagnetWarning ? Ui.Amber : Ui.Green;
                double rawFrac = _draft == null ? 0 : RawFraction(a, raw);
                _rawBar[a].Set(rawFrac, live ? raw.ToString("N0") : "–");
                _calBar[a].Set(cal / 10000.0, live ? Ui.Pct(cal / 10000.0) : "–");
                _outBar[a].Set(outv / 4095.0, live ? outv + " / 4095" : "–");
                if (_tabs.SelectedItem == _setTab) _curves[a].SetLive(live ? cal / 10000.0 : double.NaN, outv / 4095.0);
            }

            if (_peakActive)
            {
                if (live && _draft != null && Math.Abs((long)d.BrkRaw - _draft.Brk.Min) > Math.Abs((long)_peakValue - _draft.Brk.Min)) _peakValue = d.BrkRaw;
                double left = (_peakUntil - DateTime.Now).TotalSeconds;
                if (left > 0) _peakInfo.Text = "Appuie fort sur le frein… " + left.ToString("0.0") + " s   (pic : " + _peakValue.ToString("N0") + ")";
                else FinishPeak();
            }
        }

        // ═══════════════════════════ Échanges avec le pédalier ═══════════════════════════

        private void Msg(string text, Brush color)
        {
            _msg.Text = text;
            _msg.Foreground = color ?? Ui.Muted;
        }

        /// <summary>
        /// Lance "work" hors du fil de l'interface (les commandes série attendent leur réponse).
        /// work renvoie null si tout va bien, sinon le message d'erreur.
        /// </summary>
        private void Run(string busyMsg, Func<string> work, Action after)
        {
            if (_busy) { Msg("Patiente : une opération est en cours…", Ui.Amber); return; }
            if (Link.State != LinkState.Connected) { Msg("Pédalier non connecté.", Ui.Red); return; }
            _busy = true;
            Msg(busyMsg, Ui.Muted);
            UpdateButtons();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                try { err = work(); }
                catch (Exception e) { err = e.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _busy = false;
                    if (err != null) Msg("Erreur : " + err, Ui.Red);
                    else if (after != null) after();
                    UpdateButtons();
                }));
            });
        }

        /// <summary>Commande simple (fil de fond). Renvoie null si OK.</summary>
        private string Cmd(string c, int timeout = 1500)
        {
            var r = Link.Request(c, timeout);
            return r.Ok ? null : c + " : " + r.Error;
        }

        /// <summary>Lit toute la configuration du pédalier (fil de fond).</summary>
        private PedalConfig ReadConfig(out string err)
        {
            var r = Link.Request("GET", 2000);
            if (!r.Ok) { err = "GET : " + r.Error; return null; }
            var c = new PedalConfig();
            foreach (var l in r.Lines) c.ApplyLine(l);
            err = null;
            return c;
        }

        private void ReloadFromDevice(string doneMsg)
        {
            PedalConfig cfg = null;
            Run("Lecture des réglages du pédalier…", () => { string e; cfg = ReadConfig(out e); return e; }, () =>
            {
                _device = cfg;
                _draft = cfg.Clone();
                _unsaved = false;
                FillUi();
                Msg(doneMsg, Ui.Green);
            });
        }

        private void OnEdited()
        {
            if (_draft == null) return;
            UpdateCalSummary();
            if (_liveApply.IsChecked == true)
            {
                _pushTimer.Stop();
                _pushTimer.Start();
            }
            else
            {
                _unsaved = true;
                Msg("Modifié ici : clique « Envoyer » pour essayer, ou « Enregistrer » pour garder.", Ui.Amber);
            }
            UpdateButtons();
        }

        /// <summary>Envoie au pédalier (RAM) ce qui diffère entre _draft et _device.</summary>
        private void PushDraft(bool explicitSend)
        {
            if (_draft == null || _device == null) return;
            if (_busy) { _pushTimer.Stop(); _pushTimer.Start(); return; }   // on réessaie juste après
            var draft = _draft.Clone();
            var cmds = draft.DiffCommands(_device);
            if (cmds.Count == 0) { if (explicitSend) Msg("Rien à envoyer : le pédalier a déjà ces réglages.", Ui.Muted); return; }
            Run("Envoi au pédalier…", () =>
            {
                foreach (var c in cmds) { var e = Cmd(c, c.Contains(" SPS ") ? 3000 : 1500); if (e != null) return e; }
                return null;
            }, () =>
            {
                _device = draft;
                _unsaved = true;
                Msg("Réglages appliqués au pédalier (à enregistrer pour les garder SimHub fermé).", Ui.Muted);
            });
        }

        private void SaveToDevice()
        {
            if (_draft == null) return;
            _pushTimer.Stop();
            var draft = _draft.Clone();
            var cmds = draft.DiffCommands(_device);
            Run("Enregistrement dans le pédalier…", () =>
            {
                foreach (var c in cmds) { var e = Cmd(c, c.Contains(" SPS ") ? 3000 : 1500); if (e != null) return e; }
                return Cmd("SAVE", 3000);
            }, () =>
            {
                _device = draft;
                _unsaved = false;
                Msg("✓ Enregistré dans le pédalier : ces réglages restent actifs SimHub fermé et après débranchement.", Ui.Green);
            });
        }

        private void UndoChanges()
        {
            _pushTimer.Stop();
            PedalConfig cfg = null;
            string note = null;
            Run("Retour aux réglages enregistrés…", () =>
            {
                var r = Link.Request("LOAD", 2000);
                if (!r.Ok) note = "Rien d'enregistré dans le pédalier : il garde ses réglages actuels.";
                string e;
                cfg = ReadConfig(out e);
                return e;
            }, () =>
            {
                _device = cfg;
                _draft = cfg.Clone();
                _unsaved = note != null;
                FillUi();
                Msg(note ?? "Réglages enregistrés rechargés.", note != null ? Ui.Amber : Ui.Green);
            });
        }

        private void FactoryReset()
        {
            if (MessageBox.Show("Remettre les réglages d'usine dans le pédalier ?\n\nLa calibration sera perdue. Rien n'est écrit dans la mémoire permanente tant que tu ne cliques pas « Enregistrer dans le pédalier ».",
                    "Pédalier DIY", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _pushTimer.Stop();
            PedalConfig cfg = null;
            Run("Valeurs d'usine…", () =>
            {
                var e = Cmd("FACTORY");
                if (e != null) return e;
                cfg = ReadConfig(out e);
                return e;
            }, () =>
            {
                _device = cfg;
                _draft = cfg.Clone();
                _unsaved = true;
                FillUi();
                Msg("Valeurs d'usine chargées : clique « Enregistrer dans le pédalier » pour les garder.", Ui.Amber);
            });
        }

        private void ToggleConnect()
        {
            if (Link.WantConnected)
            {
                Link.Disconnect();
                _plugin.Settings.AutoConnect = false;
                Msg("Déconnecté : le port COM est libéré.", Ui.Muted);
            }
            else
            {
                Link.WantConnected = true;
                _plugin.Settings.AutoConnect = true;
                Msg("Recherche du pédalier…", Ui.Muted);
            }
            _plugin.SaveSettings();
        }

        // ─── Calibration ───

        /// <summary>
        /// Envoie des commandes CAL (chacune moyenne 250 ms dans le pédalier), puis relit la config.
        /// Les réglages en cours d'édition sont conservés ; seules calibration et linéarisation sont reprises.
        /// </summary>
        private void CalCommands(string what, params string[] cmds)
        {
            if (_draft == null) { Msg("Pédalier non connecté.", Ui.Red); return; }
            PedalConfig cfg = null;
            Run("Capture : " + what + "… ne bouge plus", () =>
            {
                foreach (var c in cmds)
                {
                    var r = Link.Request(c, 3000);
                    if (!r.Ok) return c + " : " + r.Error;
                }
                string e;
                cfg = ReadConfig(out e);
                return e;
            }, () =>
            {
                _device = cfg;
                _draft = _draft.WithHardwareOf(cfg);
                _unsaved = true;
                FillUi();
                Msg("✓ " + what + " capturé. Pense à « Enregistrer dans le pédalier ».", Ui.Green);
                if (_draft.DiffCommands(_device).Count > 0) OnEdited();
            });
        }

        private void StartPeak()
        {
            if (_draft == null || Link.State != LinkState.Connected) { Msg("Pédalier non connecté.", Ui.Red); return; }
            _peakValue = _draft.Brk.Min;
            _peakUntil = DateTime.Now.AddSeconds(3);
            _peakActive = true;
            UpdateStreamRate();    // le pic se lit sur le flux : rapide pendant la mesure
        }

        private void FinishPeak()
        {
            _peakActive = false;
            UpdateStreamRate();
            long span = Math.Abs((long)_peakValue - _draft.Brk.Min);
            if (span < 20000)
            {
                _peakInfo.Text = "Pic trop faible (" + span.ToString("N0") + " points) : rien n'a été changé.";
                return;
            }
            _peakInfo.Text = "Pic mesuré : " + _peakValue.ToString("N0") + " (course " + span.ToString("N0") + " points) → 100 % frein.";
            _draft.Brk.Max = _peakValue;
            FillUi();
            if (_liveApply.IsChecked == true) PushDraft(false); else OnEdited();
        }

        private void NewLinTable()
        {
            if (_linCount.SelectedIndex < 0) return;
            int n = _linCount.SelectedIndex + 2;
            CalCommands("nouvelle table de " + n + " points", "LIN " + n);
        }

        private void ApplyPreset(int a, int preset)
        {
            if (_draft == null) return;
            _draft.Axis(a).Curve = Curves.Preset(preset);
            _curves[a].Curve = _draft.Axis(a).Curve;
            UpdateCurveName(a);
            OnEdited();
        }

        // ─── Mises à jour ───

        private void RefreshUpdateUi()
        {
            var latest = Updater.Latest;
            bool connected = Link.State == LinkState.Connected;
            bool fwNewer = connected && Updater.FirmwareUpdateAvailable(Link.Firmware);

            _verPlugin.Text = "Plugin : " + Updater.Format(Updater.CurrentVersion)
                + (latest != null ? "   ·   dernière version publiée : " + Updater.Format(latest.PluginVersion) : "");
            _verFw.Text = "Firmware du pédalier : " + (connected ? Link.Firmware : "non connecté")
                + (latest != null && latest.FirmwareVersion != null ? "   ·   dernier publié : " + Updater.Format(latest.FirmwareVersion) : "");
            _updStatus.Text = Updater.Checking ? "Vérification…"
                            : Updater.LastError != null ? "Vérification impossible : " + Updater.LastError
                            : Updater.PendingRestartVersion != null ? "Plugin " + Updater.Format(Updater.PendingRestartVersion) + " installé : redémarre SimHub."
                            : latest == null ? "" : Updater.PluginUpdateAvailable ? "Une nouvelle version du plugin est disponible (bandeau en haut)."
                            : fwNewer ? "Un nouveau firmware est disponible pour le pédalier." : "Tout est à jour.";
            _flashBtn.Content = fwNewer || !connected ? "Mettre à jour le pédalier" : "Réinstaller le firmware";
            // Plugin d'abord : un nouveau firmware peut avoir besoin du nouveau plugin
            _flashBtn.IsEnabled = connected && !_busy && latest != null && latest.FirmwareAsset != null && !Updater.PluginUpdateAvailable;
            _checkBtn.IsEnabled = !Updater.Checking;

            if (Updater.PendingRestartVersion != null)
                ShowBanner("✓ Plugin " + Updater.Format(Updater.PendingRestartVersion) + " installé : redémarre SimHub pour l'utiliser.", null);
            else if (Updater.Dismissed)
                _banner.Visibility = Visibility.Collapsed;
            else if (Updater.PluginUpdateAvailable)
                ShowBanner("Nouvelle version du plugin : " + Updater.Format(latest.PluginVersion) + " (tu as " + Updater.Format(Updater.CurrentVersion) + ").", "Installer");
            else if (fwNewer)
                ShowBanner("Nouveau firmware pour le pédalier : " + Updater.Format(latest.FirmwareVersion) + " (il a " + Link.Firmware + ").", "Mettre à jour le pédalier");
            else
                _banner.Visibility = Visibility.Collapsed;
            _banner.ToolTip = latest != null && latest.Notes.Length > 0 ? latest.Notes : null;
        }

        private void ShowBanner(string text, string action)
        {
            _bannerText.Text = text;
            _bannerAction.Content = action ?? "";
            _bannerAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            _bannerLater.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            _banner.Visibility = Visibility.Visible;
        }

        private void BannerAction()
        {
            if (Updater.PluginUpdateAvailable)
            {
                _bannerAction.IsEnabled = false;
                Msg("Téléchargement du plugin…", Ui.Muted);
                Updater.InstallPluginInBackground(err => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _bannerAction.IsEnabled = true;
                    if (err != null) Msg("Mise à jour du plugin impossible : " + err, Ui.Red);
                    else Msg("✓ Plugin installé : redémarre SimHub pour l'utiliser.", Ui.Green);
                    RefreshUpdateUi();
                })));
            }
            else FlashFirmware();
        }

        private void FlashFirmware()
        {
            var latest = Updater.Latest;
            if (latest == null || latest.FirmwareAsset == null || Link.State != LinkState.Connected) return;
            if (_busy) { Msg("Patiente : une opération est en cours…", Ui.Amber); return; }
            if (MessageBox.Show("Installer le firmware " + Updater.Format(latest.FirmwareVersion) + " dans le pédalier ?\n\n"
                    + "Ça prend une vingtaine de secondes. Ne débranche pas le pédalier pendant l'écriture. Tes réglages enregistrés sont conservés.",
                    "Pédalier DIY", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _pushTimer.Stop();
            _busy = true;
            UpdateButtons();
            RefreshUpdateUi();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                try
                {
                    // Sauvegarde des réglages actuels : renvoyés si le nouveau firmware repart sur ses valeurs d'usine
                    string e;
                    var backup = ReadConfig(out e);
                    if (backup != null)
                    {
                        Directory.CreateDirectory(_plugin.DataDir);
                        File.WriteAllText(System.IO.Path.Combine(_plugin.DataDir, "sauvegarde_avant_maj_firmware.json"), JsonConvert.SerializeObject(backup, Formatting.Indented));
                    }
                    err = Updater.FlashFirmware(Link, backup, p => Dispatcher.BeginInvoke(new Action(() => Msg(p, Ui.Amber))));
                }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _busy = false;
                    if (err != null) Msg("Mise à jour du pédalier : " + err, Ui.Red);
                    UpdateButtons();
                    RefreshUpdateUi();
                    if (Link.State == LinkState.Connected)
                        ReloadFromDevice(err == null ? "✓ Firmware " + Link.Firmware + " installé dans le pédalier." : "Pédalier reconnecté.");
                }));
            });
        }

        // ─── Diagnostic ───

        private static readonly string[][] DiagLabels =
        {
            new[] { "FW", "Version du firmware" }, new[] { "CFG", "Format de configuration" },
            new[] { "SRC", "Réglages actifs" }, new[] { "UPTIME", "Allumé depuis" },
            new[] { "ACC_OK", "Accélérateur (AS5600)" }, new[] { "AIMANT", "Aimant" },
            new[] { "AGC", "Gain automatique (AGC)" }, new[] { "MAG", "Magnitude du champ" },
            new[] { "BURN", "Écritures définitives AS5600" }, new[] { "ACC_HZ", "Lectures accélérateur / s" },
            new[] { "ERR_ACC", "Erreurs accélérateur" }, new[] { "BRK_OK", "Frein (NAU7802)" },
            new[] { "BRK_HZ", "Lectures frein / s" }, new[] { "ERR_BRK", "Pertes du frein" },
            new[] { "TARES", "Re-tares automatiques" }, new[] { "LIN_OK", "Table de linéarisation" },
        };

        private void PollDiag()
        {
            if (_tabs.SelectedItem != _diagTab || _diagInFlight || Link.State != LinkState.Connected) return;
            _diagInFlight = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = Link.Request("DIAG", 1500);
                Dispatcher.BeginInvoke(new Action(() => { _diagInFlight = false; ShowDiag(r.Final); }));
            });
        }

        private void ShowDiag(string line)
        {
            if (line == null || !line.StartsWith("DIAG ")) return;
            var kv = new Dictionary<string, string>();
            foreach (var tok in line.Substring(5).Split(' '))
            {
                int i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = tok.Substring(i + 1);
            }
            _diagGrid.Children.Clear();
            _diagGrid.RowDefinitions.Clear();
            Action<string, string, Brush> add = (label, value, color) =>
            {
                int row = _diagGrid.RowDefinitions.Count;
                _diagGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = Ui.T(label, 12, Ui.Muted);
                var v = Ui.T(value, 12, color ?? Ui.Text);
                l.Margin = v.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
                _diagGrid.Children.Add(l);
                _diagGrid.Children.Add(v);
            };
            add("Port série", Link.PortName + " · " + Link.StreamHz + " mesures/s en direct", null);
            foreach (var d in DiagLabels)
            {
                string v;
                if (!kv.TryGetValue(d[0], out v)) continue;
                Brush color = null;
                switch (d[0])
                {
                    case "SRC": v = v == "EEPROM" ? "enregistrés dans le pédalier" : "valeurs d'usine (rien d'enregistré)"; break;
                    case "ACC_OK": case "BRK_OK": color = v == "1" ? Ui.Green : Ui.Red; v = v == "1" ? "OK" : "absent / en défaut"; break;
                    case "LIN_OK": color = v == "1" ? null : Ui.Amber; v = v == "1" ? "valide" : "invalide (correction ignorée)"; break;
                    case "AIMANT": color = v == "OK" ? Ui.Green : Ui.Red;
                        v = v == "OK" ? "OK" : v == "FAIBLE" ? "trop faible (aimant trop loin)" : v == "FORT" ? "trop fort (aimant trop près)" : "absent"; break;
                    case "ERR_ACC": case "ERR_BRK": color = v == "0" ? null : Ui.Amber; break;
                    case "UPTIME": int s; if (int.TryParse(v, out s)) v = (s / 3600) + " h " + (s / 60 % 60).ToString("00") + " min " + (s % 60).ToString("00") + " s"; break;
                }
                add(d[1], v, color);
            }
        }

        // ─── Profils ───

        private string ProfileDir
        {
            get
            {
                var dir = System.IO.Path.Combine(_plugin.DataDir, "Profils");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private void RefreshProfiles()
        {
            _profiles.Items.Clear();
            try
            {
                foreach (var f in Directory.GetFiles(ProfileDir, "*.json").OrderBy(f => f))
                    _profiles.Items.Add(System.IO.Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception e) { Msg("Profils illisibles : " + e.Message, Ui.Red); }
        }

        private static string SafeName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        private void SaveProfile()
        {
            if (_draft == null) { Msg("Connecte le pédalier pour avoir des réglages à enregistrer.", Ui.Red); return; }
            var name = SafeName(_profileName.Text);
            if (name.Length == 0) { Msg("Donne un nom au profil.", Ui.Red); return; }
            File.WriteAllText(System.IO.Path.Combine(ProfileDir, name + ".json"), JsonConvert.SerializeObject(_draft, Formatting.Indented));
            RefreshProfiles();
            _profiles.SelectedItem = name;
            Msg("Profil « " + name + " » enregistré sur le PC.", Ui.Green);
        }

        private void ApplyProfileFile(string path, string label)
        {
            if (_draft == null) { Msg("Connecte le pédalier pour charger un profil.", Ui.Red); return; }
            PedalConfig p;
            try { p = JsonConvert.DeserializeObject<PedalConfig>(File.ReadAllText(path)); }
            catch (Exception e) { Msg("Profil illisible : " + e.Message, Ui.Red); return; }
            if (p == null || p.Acc == null || p.Brk == null || p.Acc.Curve == null || p.Brk.Curve == null
                || p.Acc.Curve.Length != 11 || p.Brk.Curve.Length != 11) { Msg("Ce fichier n'est pas un profil de pédalier.", Ui.Red); return; }
            _draft = p.WithHardwareOf(_draft);
            FillUi();
            OnEdited();
            Msg("Profil « " + label + " » chargé.", Ui.Green);
        }

        private void LoadProfile()
        {
            if (_profiles.SelectedItem == null) { Msg("Choisis un profil dans la liste.", Ui.Red); return; }
            var name = (string)_profiles.SelectedItem;
            ApplyProfileFile(System.IO.Path.Combine(ProfileDir, name + ".json"), name);
        }

        private void DeleteProfile()
        {
            if (_profiles.SelectedItem == null) return;
            var name = (string)_profiles.SelectedItem;
            if (MessageBox.Show("Supprimer le profil « " + name + " » ?", "Pédalier DIY", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            File.Delete(System.IO.Path.Combine(ProfileDir, name + ".json"));
            RefreshProfiles();
            Msg("Profil supprimé.", Ui.Muted);
        }

        private void ExportProfile()
        {
            if (_draft == null) { Msg("Connecte le pédalier pour avoir des réglages à exporter.", Ui.Red); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Profil pédalier (*.json)|*.json", FileName = SafeName(_profileName.Text.Length > 0 ? _profileName.Text : "Pedalier") };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, JsonConvert.SerializeObject(_draft, Formatting.Indented));
            Msg("Exporté : " + dlg.FileName, Ui.Green);
        }

        private void ImportProfile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Profil pédalier (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            var name = SafeName(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
            var dest = System.IO.Path.Combine(ProfileDir, name + ".json");
            if (!string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(dlg.FileName, dest, true);
            RefreshProfiles();
            _profiles.SelectedItem = name;
            ApplyProfileFile(dest, name);
        }

        // ─── Outils ───

        private static int AngleDelta(int a, int b)
        {
            int d = a - b;
            if (d > 2048) d -= 4096;
            if (d < -2048) d += 4096;
            return d;
        }

        /// <summary>Position brute entre le 0 % et le 100 % calibrés (pour la barre "Brut").</summary>
        private double RawFraction(int axis, int raw)
        {
            var c = _draft.Axis(axis);
            double span = axis == 0 ? AngleDelta(c.Max, c.Min) : (double)c.Max - c.Min;
            if (span == 0) return 0;
            double d = axis == 0 ? AngleDelta(raw, c.Min) : (double)raw - c.Min;
            return d / span;
        }
    }
}
