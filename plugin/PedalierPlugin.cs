using System.IO;
using System.Reflection;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace User.PedalierDIY
{
    /// <summary>Réglages du plugin lui-même (le pédalier garde les siens dans son EEPROM).</summary>
    public class PedalierSettings
    {
        public string Port = "AUTO";       // "AUTO" = recherche par identifiant USB
        public bool AutoConnect = true;
        public bool LiveApply = true;      // envoi immédiat des modifications (RAM du pédalier)
    }

    [PluginDescription("Pédalier DIY (AS5600 + NAU7802) : calibration, linéarisation, courbes, zones mortes, filtrage et diagnostic, enregistrés dans le pédalier")]
    [PluginAuthor("XRockzen")]
    [PluginName("Pédalier DIY")]
    public class PedalierPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsName = "PedalierSettings";

        internal PedalLink Link;
        internal PedalierSettings Settings;
        internal string DataDir;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon { get { return Ui.Icon; } }

        public string LeftMenuTitle { get { return "Pédalier DIY"; } }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[PedalierDIY] Démarrage du plugin");
            Settings = this.ReadCommonSettings<PedalierSettings>(SettingsName, () => new PedalierSettings());
            var simhubDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            DataDir = Path.Combine(simhubDir, "PluginsData", "Common", "PedalierDIY");

            Link = new PedalLink { PreferredPort = Settings.Port, WantConnected = Settings.AutoConnect };
            Link.Start();

            Updater.CleanupPrevious();
            Updater.CheckInBackground();

            // Propriétés pour les dashboards et overlays (PedalierPlugin.*)
            this.AttachDelegate("Connecte", () => Link.State == LinkState.Connected);
            this.AttachDelegate("Accelerateur", () => Link.Live.AccOut * 100.0 / 4095);
            this.AttachDelegate("Frein", () => Link.Live.BrkOut * 100.0 / 4095);
            this.AttachDelegate("AccelerateurCalibre", () => Link.Live.AccCal / 100.0);
            this.AttachDelegate("FreinCalibre", () => Link.Live.BrkCal / 100.0);
            this.AttachDelegate("AccelerateurBrut", () => Link.Live.AccRaw);
            this.AttachDelegate("FreinBrut", () => Link.Live.BrkRaw);
            this.AttachDelegate("CapteursOK", () => Link.Live.AccOk && Link.Live.BrkOk);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Rien par image : les valeurs arrivent par la liaison série (fil de fond).
        }

        public void End(PluginManager pluginManager)
        {
            SaveSettings();
            if (Link != null) Link.Dispose();   // libère le port COM
        }

        internal void SaveSettings()
        {
            this.SaveCommonSettings(SettingsName, Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new PedalierControl(this);
        }
    }
}
