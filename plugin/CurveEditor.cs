using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace User.PedalierDIY
{
    /// <summary>
    /// Éditeur de courbe de réponse : 11 points (entrée 0, 10, ... 100 %), chacun réglable en hauteur
    /// à la souris. Un point mobile montre la position actuelle de la pédale sur la courbe.
    /// </summary>
    internal sealed class CurveEditor : FrameworkElement
    {
        private const double Pad = 28;
        private int[] _curve = Curves.Linear();
        private int _drag = -1;
        private readonly Brush _color;
        private readonly Pen _gridPen = new Pen(Ui.Line, 1);
        private readonly Pen _diagPen = new Pen(Ui.Faint, 1) { DashStyle = DashStyles.Dash };
        private readonly Pen _curvePen;
        private double _liveX = double.NaN, _liveY = double.NaN;

        /// <summary>Levé pendant le glissement et au lâcher (courbe modifiée par l'utilisateur).</summary>
        public event Action<int[]> CurveChanged;

        public CurveEditor(Brush color, double size = 250)
        {
            _color = color;
            _curvePen = new Pen(color, 2);
            Width = size;
            Height = size;
            Cursor = Cursors.Hand;
            _gridPen.Freeze();
            _diagPen.Freeze();
            _curvePen.Freeze();
        }

        public int[] Curve
        {
            get { return (int[])_curve.Clone(); }
            set { _curve = (int[])value.Clone(); InvalidateVisual(); }
        }

        /// <summary>Position actuelle : entrée et sortie de 0 à 1 (NaN = pas de point).</summary>
        public void SetLive(double x, double y)
        {
            if (Math.Abs(x - _liveX) < 0.001 && Math.Abs(y - _liveY) < 0.001) return;
            _liveX = x;
            _liveY = y;
            InvalidateVisual();
        }

        private Rect Plot { get { return new Rect(Pad, 8, Math.Max(10, ActualWidth - Pad - 8), Math.Max(10, ActualHeight - Pad - 8)); } }

        private Point ToScreen(double x, double y)
        {
            var r = Plot;
            return new Point(r.Left + x * r.Width, r.Bottom - y * r.Height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var r = Plot;
            dc.DrawRectangle(Ui.Panel, null, r);
            var tf = new Typeface("Segoe UI");
            for (int i = 0; i <= 10; i++)
            {
                double f = i / 10.0;
                dc.DrawLine(_gridPen, ToScreen(f, 0), ToScreen(f, 1));
                dc.DrawLine(_gridPen, ToScreen(0, f), ToScreen(1, f));
                if (i % 5 == 0)
                {
                    var lbl = new FormattedText((i * 10).ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10, Ui.Muted, 1.0);
                    dc.DrawText(lbl, new Point(ToScreen(f, 0).X - lbl.Width / 2, r.Bottom + 3));
                    dc.DrawText(lbl, new Point(r.Left - lbl.Width - 5, ToScreen(0, f).Y - lbl.Height / 2));
                }
            }
            dc.DrawLine(_diagPen, ToScreen(0, 0), ToScreen(1, 1));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(ToScreen(0, _curve[0] / 10000.0), false, false);
                for (int i = 1; i < 11; i++) ctx.LineTo(ToScreen(i / 10.0, _curve[i] / 10000.0), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, _curvePen, geo);
            for (int i = 0; i < 11; i++)
                dc.DrawEllipse(i == _drag ? Brushes.White : _color, null, ToScreen(i / 10.0, _curve[i] / 10000.0), 4.5, 4.5);

            if (!double.IsNaN(_liveX))
            {
                var p = ToScreen(Math.Max(0, Math.Min(1, _liveX)), Math.Max(0, Math.Min(1, _liveY)));
                dc.DrawEllipse(Ui.Amber, new Pen(Brushes.Black, 1), p, 5.5, 5.5);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            var r = Plot;
            int i = (int)Math.Round((pos.X - r.Left) / r.Width * 10);
            if (i < 0 || i > 10) return;
            _drag = i;
            CaptureMouse();
            MoveTo(pos);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag >= 0) MoveTo(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_drag < 0) return;
            _drag = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            Raise();
        }

        private void MoveTo(Point pos)
        {
            var r = Plot;
            double y = (r.Bottom - pos.Y) / r.Height;
            int v = (int)Math.Round(Math.Max(0, Math.Min(1, y)) * 200) * 50;   // pas de 0,5 %
            if (v == _curve[_drag]) return;
            _curve[_drag] = v;
            InvalidateVisual();
            Raise();
        }

        private void Raise()
        {
            var h = CurveChanged;
            if (h != null) h(Curve);
        }
    }
}
