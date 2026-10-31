using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace User.PedalierDIY
{
    /// <summary>Réglages d'un axe, mêmes unités que le firmware (voir PROTOCOLE.md).</summary>
    public class AxisConfig
    {
        public int Min;                       // valeur brute à 0 %
        public int Max;                       // valeur brute à 100 %
        public int Dzl;                       // zone morte début, 0,01 %
        public int Dzh;                       // zone morte fin, 0,01 %
        public int Tau;                       // lissage, ms
        public int Hys;                       // hystérésis, pas de sortie sur 4095
        public int[] Curve = Curves.Linear(); // 11 sorties (0,01 %) pour 0, 10, ... 100 %

        public AxisConfig Clone()
        {
            var a = (AxisConfig)MemberwiseClone();
            a.Curve = (int[])Curve.Clone();
            return a;
        }
    }

    /// <summary>
    /// Copie côté PC de la configuration du pédalier. Se remplit avec les lignes "P ..." de GET
    /// et produit les commandes SET qui amènent le pédalier à cet état.
    /// </summary>
    public class PedalConfig
    {
        public const int AccIndex = 0, BrkIndex = 1;

        public AxisConfig Acc = new AxisConfig();
        public AxisConfig Brk = new AxisConfig();
        public int BrkMed = 3;
        public int BrkSps = 320;
        public int TareBand = 300;
        public int TareMs = 2000;
        public int[] Lin = new[] { 0, 10000 };

        public AxisConfig Axis(int i) { return i == AccIndex ? Acc : Brk; }

        public static string AxisCode(int i) { return i == AccIndex ? "ACC" : "BRK"; }

        public PedalConfig Clone()
        {
            var c = (PedalConfig)MemberwiseClone();
            c.Acc = Acc.Clone();
            c.Brk = Brk.Clone();
            c.Lin = (int[])Lin.Clone();
            return c;
        }

        public bool LinValid()
        {
            if (Lin == null || Lin.Length < 2 || Lin.Length > 11) return false;
            for (int i = 1; i < Lin.Length; i++) if (Lin[i] <= Lin[i - 1]) return false;
            return true;
        }

        /// <summary>Lit une ligne "P ..." de la réponse à GET. Renvoie false si elle n'est pas reconnue.</summary>
        public bool ApplyLine(string line)
        {
            var t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 3 || t[0] != "P") return false;
            int v;
            if (t[1] == "LIN")
            {
                int n;
                if (!int.TryParse(t[2], out n) || n < 2 || t.Length != 3 + n) return false;
                var lin = new int[n];
                for (int i = 0; i < n; i++) if (!int.TryParse(t[3 + i], out lin[i])) return false;
                Lin = lin;
                return true;
            }
            AxisConfig a = t[1] == "ACC" ? Acc : t[1] == "BRK" ? Brk : null;
            if (a == null || t.Length < 4) return false;
            if (t[2] == "CURVE")
            {
                if (t.Length != 3 + 11) return false;
                var c = new int[11];
                for (int i = 0; i < 11; i++) if (!int.TryParse(t[3 + i], out c[i])) return false;
                a.Curve = c;
                return true;
            }
            if (!int.TryParse(t[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return false;
            switch (t[2])
            {
                case "MIN": a.Min = v; break;
                case "MAX": a.Max = v; break;
                case "DZL": a.Dzl = v; break;
                case "DZH": a.Dzh = v; break;
                case "TAU": a.Tau = v; break;
                case "HYS": a.Hys = v; break;
                case "MED": BrkMed = v; break;
                case "SPS": BrkSps = v; break;
                case "TBAND": TareBand = v; break;
                case "TMS": TareMs = v; break;
                default: return false;
            }
            return true;
        }

        /// <summary>
        /// Commandes SET pour passer de l'état "device" (dernier état connu du pédalier) à cet état.
        /// Seuls les réglages différents sont envoyés : le zéro du frein, que la re-tare
        /// automatique déplace dans le pédalier, n'est donc pas écrasé sans raison.
        /// </summary>
        public List<string> DiffCommands(PedalConfig device)
        {
            var cmds = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var a = Axis(i);
                var d = device == null ? null : device.Axis(i);
                string p = "SET " + AxisCode(i) + " ";
                if (d == null || a.Min != d.Min) cmds.Add(p + "MIN " + a.Min);
                if (d == null || a.Max != d.Max) cmds.Add(p + "MAX " + a.Max);
                if (d == null || a.Dzl != d.Dzl) cmds.Add(p + "DZL " + a.Dzl);
                if (d == null || a.Dzh != d.Dzh) cmds.Add(p + "DZH " + a.Dzh);
                if (d == null || a.Tau != d.Tau) cmds.Add(p + "TAU " + a.Tau);
                if (d == null || a.Hys != d.Hys) cmds.Add(p + "HYS " + a.Hys);
                if (d == null || !a.Curve.SequenceEqual(d.Curve)) cmds.Add(p + "CURVE " + string.Join(" ", a.Curve));
            }
            if (device == null || BrkMed != device.BrkMed) cmds.Add("SET BRK MED " + BrkMed);
            if (device == null || TareBand != device.TareBand) cmds.Add("SET BRK TBAND " + TareBand);
            if (device == null || TareMs != device.TareMs) cmds.Add("SET BRK TMS " + TareMs);
            if (LinValid() && (device == null || !Lin.SequenceEqual(device.Lin)))
                cmds.Add("SET LIN " + Lin.Length + " " + string.Join(" ", Lin));
            // En dernier : bloque le pédalier ~0,4 s (recalibration du NAU7802)
            if (device == null || BrkSps != device.BrkSps) cmds.Add("SET BRK SPS " + BrkSps);
            return cmds;
        }

        /// <summary>Profil "ressenti" : garde la calibration et la linéarisation de "hardware".</summary>
        public PedalConfig WithHardwareOf(PedalConfig hardware)
        {
            var c = Clone();
            c.Acc.Min = hardware.Acc.Min; c.Acc.Max = hardware.Acc.Max;
            c.Brk.Min = hardware.Brk.Min; c.Brk.Max = hardware.Brk.Max;
            c.Lin = (int[])hardware.Lin.Clone();
            return c;
        }
    }

    /// <summary>Courbes préréglées, mêmes formules que la commande CURVE du firmware.</summary>
    public static class Curves
    {
        public static readonly string[] Names = { "Linéaire", "Progressive", "Dégressive", "En S", "Logarithmique" };

        public static int[] Linear() { return Preset(0); }

        public static int[] Preset(int p)
        {
            var c = new int[11];
            for (int i = 0; i < 11; i++)
            {
                double x = i / 10.0, y;
                switch (p)
                {
                    case 1: y = x * x; break;                       // douce au début
                    case 2: y = 1 - (1 - x) * (1 - x); break;       // vive au début
                    case 3: y = x * x * (3 - 2 * x); break;         // en S
                    case 4: y = Math.Log10(1 + 9 * x); break;       // frein "en force"
                    default: y = x; break;
                }
                c[i] = (int)Math.Round(y * 10000);
            }
            return c;
        }

        /// <summary>Indice du préréglage identique à cette courbe, ou -1 (courbe libre).</summary>
        public static int Match(int[] curve)
        {
            for (int p = 0; p < Names.Length; p++)
                if (Preset(p).Zip(curve, (a, b) => Math.Abs(a - b) <= 1).All(x => x)) return p;
            return -1;
        }
    }
}
