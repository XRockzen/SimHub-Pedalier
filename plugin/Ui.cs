using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace User.PedalierDIY
{
    /// <summary>Petits outils partagés par l'interface construite en code (même aspect partout).</summary>
    internal static class Ui
    {
        public static readonly Brush Text   = Frozen(Color.FromRgb(0xE8, 0xE9, 0xEC));
        public static readonly Brush Muted  = Frozen(Color.FromArgb(166, 255, 255, 255));
        public static readonly Brush Faint  = Frozen(Color.FromArgb(90, 255, 255, 255));
        public static readonly Brush Panel  = Frozen(Color.FromArgb(14, 255, 255, 255));
        public static readonly Brush Track  = Frozen(Color.FromArgb(26, 255, 255, 255));
        public static readonly Brush Line   = Frozen(Color.FromArgb(22, 255, 255, 255));
        public static readonly Brush Green  = Frozen(Color.FromRgb(0x66, 0xBB, 0x6A));
        public static readonly Brush Red    = Frozen(Color.FromRgb(0xEF, 0x6F, 0x6F));
        public static readonly Brush Amber  = Frozen(Color.FromRgb(0xFF, 0xC8, 0x57));
        public static readonly Brush AccColor = Frozen(Color.FromRgb(0x4C, 0xC2, 0x6E));   // accélérateur : vert
        public static readonly Brush BrkColor = Frozen(Color.FromRgb(0xE8, 0x5A, 0x4F));   // frein : rouge

        public static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Brush AxisBrush(int axis) { return axis == PedalConfig.AccIndex ? AccColor : BrkColor; }

        public static string AxisLabel(int axis) { return axis == PedalConfig.AccIndex ? "Accélérateur" : "Frein"; }

        public static TextBlock T(string text, double size = 13, Brush fg = null, bool bold = false)
        {
            return new TextBlock
            {
                Text = text ?? "", FontSize = size, Foreground = fg ?? Text,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
        }

        public static Border Card(UIElement child)
        {
            return new Border
            {
                Background = Panel, CornerRadius = new CornerRadius(7),
                Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = child,
            };
        }

        public static StackPanel Stack(params UIElement[] children)
        {
            var s = new StackPanel();
            foreach (var c in children) if (c != null) s.Children.Add(c);
            return s;
        }

        public static StackPanel Row(params UIElement[] children)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in children) if (c != null) s.Children.Add(c);
            return s;
        }

        public static Button Btn(string text, Action click, double minWidth = 0)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4), MinWidth = minWidth };
            b.Click += (s, e) => click();
            return b;
        }

        /// <summary>Grille à deux colonnes égales (accélérateur à gauche, frein à droite).</summary>
        public static Grid TwoColumns(UIElement left, UIElement right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        public static string Pct(double fraction) { return (fraction * 100).ToString("0.0") + " %"; }

        // ─── Icône du menu de gauche : deux pédales vues de côté ───────────────
        private static ImageSource _icon;

        public static ImageSource Icon
        {
            get
            {
                if (_icon != null) return _icon;
                var white = Frozen(Color.FromRgb(0xF2, 0xF3, 0xF5));
                var g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
                var pen = new Pen(white, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                // socle
                g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(3, 21), new Point(21, 21))));
                // pédale de frein (plus large) et accélérateur (plus fin), inclinées
                g.Children.Add(new GeometryDrawing(null, new Pen(BrkColor, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new LineGeometry(new Point(6, 20), new Point(10, 5))));
                g.Children.Add(new GeometryDrawing(null, new Pen(AccColor, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new LineGeometry(new Point(14, 20), new Point(18, 7))));
                g.Freeze();
                var img = new DrawingImage(g);
                img.Freeze();
                _icon = img;
                return _icon;
            }
        }
    }

    /// <summary>
    /// Barre horizontale de 0 à 100 % avec sa valeur en texte. Le remplissage est une mise à
    /// l'échelle (RenderTransform) : aucune remise en page à chaque mesure, d'où un direct fluide.
    /// </summary>
    internal sealed class LevelBar : Grid
    {
        private readonly Border _fill;
        private readonly ScaleTransform _scale = new ScaleTransform(0, 1);
        private readonly TextBlock _value;
        private double _last = -1;

        public LevelBar(string label, Brush color, double width = 260)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width + 8) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Margin = new Thickness(0, 3, 0, 3);
            var l = Ui.T(label, 12, Ui.Muted);
            Children.Add(l);
            var track = new Border { Background = Ui.Track, CornerRadius = new CornerRadius(3), Height = 14, Width = width, HorizontalAlignment = HorizontalAlignment.Left };
            _fill = new Border { Background = color, CornerRadius = new CornerRadius(3), Width = width, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _scale };
            track.Child = _fill;
            SetColumn(track, 1);
            Children.Add(track);
            _value = Ui.T("–", 12);
            _value.FontFamily = new FontFamily("Consolas");
            SetColumn(_value, 2);
            Children.Add(_value);
        }

        public void Set(double fraction, string text)
        {
            if (double.IsNaN(fraction)) fraction = 0;
            fraction = Math.Max(0, Math.Min(1, fraction));
            if (fraction != _last) { _scale.ScaleX = fraction; _last = fraction; }
            if (_value.Text != text) _value.Text = text;
        }
    }
}
