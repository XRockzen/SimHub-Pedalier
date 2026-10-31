using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace User.PedalierDIY
{
    /// <summary>Dernière release GitHub : version du plugin, version du firmware, fichiers joints.</summary>
    public sealed class ReleaseInfo
    {
        public Version PluginVersion;
        public Version FirmwareVersion;
        public string FirmwareAsset;          // "Pedalier-1.2.0.hex"
        public string Tag, Notes, HtmlUrl;
        public Dictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mises à jour depuis les releases GitHub (XRockzen/SimHub-Pedalier). Chaque release porte :
    ///   User.PedalierDIY.dll, Pedalier-x.y.z.hex, avrdude.exe, avrdude.conf, SHA256SUMS.txt
    /// - Plugin : la DLL est vérifiée (SHA-256, nom, version) puis échangée ; effet au redémarrage de SimHub.
    /// - Firmware : le pédalier redémarre en mode programmation (ouverture du port à 1200 bauds),
    ///   puis avrdude écrit le .hex, comme le fait l'IDE Arduino. Le programme de démarrage de la
    ///   Pro Micro n'est jamais effacé : un échec se rattrape en relançant la mise à jour.
    ///   La mémoire des réglages (EEPROM) n'est pas touchée.
    /// </summary>
    public static class Updater
    {
        public const string Owner = "XRockzen";
        public const string Repo = "SimHub-Pedalier";
        public const string DllName = "User.PedalierDIY.dll";
        public const string SumsName = "SHA256SUMS.txt";

        public static string ApiBase = "https://api.github.com";

        public static ReleaseInfo Latest { get; private set; }
        public static string LastError { get; private set; }
        public static Version PendingRestartVersion { get; private set; }
        public static bool Dismissed { get; set; }
        public static bool Checking { get; private set; }

        /// <summary>Levé (sur un fil de fond) quand l'état des mises à jour change.</summary>
        public static event Action Changed;

        public static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        public static string Format(Version v)
        {
            return v == null ? "?" : string.Format("{0}.{1}.{2}", v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static bool PluginUpdateAvailable
        {
            get
            {
                var l = Latest;
                return l != null && PendingRestartVersion == null && l.Assets.ContainsKey(DllName)
                    && Normalize(l.PluginVersion) > Normalize(CurrentVersion);
            }
        }

        /// <summary>Le firmware publié est plus récent que celui du pédalier branché.</summary>
        public static bool FirmwareUpdateAvailable(string connectedFw)
        {
            var l = Latest;
            var cur = ParseVersion(connectedFw);
            return l != null && l.FirmwareVersion != null && cur != null && Normalize(l.FirmwareVersion) > Normalize(cur);
        }

        // ─── Vérification ───

        public static void CheckInBackground()
        {
            if (Checking) return;
            Checking = true;
            Raise();
            Task.Run(() =>
            {
                try
                {
                    Latest = FetchLatest();
                    LastError = null;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    SimHub.Logging.Current.Info("[PedalierDIY] Vérification des mises à jour impossible : " + e.Message);
                }
                Checking = false;
                Raise();
            });
        }

        private static ReleaseInfo FetchLatest()
        {
            var json = JObject.Parse(Encoding.UTF8.GetString(Download(string.Format("{0}/repos/{1}/{2}/releases/latest", ApiBase, Owner, Repo))).TrimStart('\uFEFF'));
            var info = new ReleaseInfo
            {
                Tag = (string)json["tag_name"] ?? "",
                Notes = ((string)json["body"] ?? "").Trim(),
                HtmlUrl = (string)json["html_url"],
            };
            info.PluginVersion = ParseVersion(info.Tag);
            var assets = json["assets"] as JArray;
            if (assets != null)
                foreach (var a in assets)
                {
                    var name = (string)a["name"];
                    info.Assets[name] = (string)a["browser_download_url"];
                    var m = Regex.Match(name, @"^Pedalier-(\d+\.\d+\.\d+)\.hex$", RegexOptions.IgnoreCase);
                    if (m.Success) { info.FirmwareAsset = name; info.FirmwareVersion = ParseVersion(m.Groups[1].Value); }
                }
            if (info.PluginVersion == null || !info.Assets.ContainsKey(SumsName)) return null;   // release incomplète
            return info;
        }

        // ─── Plugin ───

        public static void InstallPluginInBackground(Action<string> done)
        {
            var info = Latest;
            Task.Run(() =>
            {
                string error = info == null ? "Aucune mise à jour disponible" : InstallPlugin(info);
                if (error == null) PendingRestartVersion = info.PluginVersion;
                Raise();
                if (done != null) done(error);
            });
        }

        private static string InstallPlugin(ReleaseInfo info)
        {
            try
            {
                var dir = TempDir("PedalierDIY_update");
                var sums = ReadSums(info);
                var tmp = VerifiedDownload(info, sums, DllName, dir);
                var name = AssemblyName.GetAssemblyName(tmp);
                if (name.Name != "User.PedalierDIY") return "Le fichier téléchargé n'est pas ce plugin";
                if (Normalize(name.Version) != Normalize(info.PluginVersion))
                    return "Version du fichier (" + Format(name.Version) + ") différente de la release (" + Format(info.PluginVersion) + ")";
                return Swap(tmp, Assembly.GetExecutingAssembly().Location, Sha256(tmp));
            }
            catch (Exception e) { return e.Message; }
        }

        // Une DLL chargée ne peut pas être écrasée mais peut être renommée : SimHub garde
        // l'ancien code jusqu'au redémarrage, le démarrage suivant charge le nouveau fichier.
        private static string Swap(string newDll, string target, string expectedSha)
        {
            var old = NextOldName(target);
            try
            {
                File.Move(target, old);
                try { File.Copy(newDll, target, true); }
                catch { File.Move(old, target); throw; }
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return SwapElevated(newDll, target, old, expectedSha);
            }
        }

        // Dossier protégé (C:\Program Files…) : même échange via un cmd administrateur (fenêtre UAC)
        private static string SwapElevated(string newDll, string target, string old, string expectedSha)
        {
            var script = Path.Combine(Path.GetDirectoryName(newDll), "install_update.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                string.Format("move /Y \"{0}\" \"{1}\" >nul\r\n", target, old) +
                string.Format("copy /Y \"{0}\" \"{1}\" >nul || move /Y \"{2}\" \"{1}\" >nul\r\n", newDll, target, old),
                Encoding.Default);
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { return "Autorisation administrateur refusée"; }
            return File.Exists(target) && string.Equals(Sha256(target), expectedSha, StringComparison.OrdinalIgnoreCase)
                ? null : "Le remplacement du fichier a échoué";
        }

        private static string NextOldName(string target)
        {
            var old = target + ".old";
            if (!File.Exists(old)) return old;
            try { File.Delete(old); return old; }
            catch { return target + "." + DateTime.Now.Ticks + ".old"; }   // encore chargée : mise à jour précédente sans redémarrage
        }

        /// <summary>Supprime les DLL laissées par une mise à jour précédente (plus chargées après redémarrage).</summary>
        public static void CleanupPrevious()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (var f in Directory.GetFiles(dir, DllName + "*.old"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        // ─── Firmware ───

        /// <summary>
        /// Met à jour le firmware du pédalier connecté sur "link" (bloquant, fil de fond).
        /// backup : réglages lus juste avant ; renvoyés et enregistrés si le nouveau firmware
        /// repart sur ses valeurs d'usine (format de configuration changé).
        /// Renvoie null si tout s'est bien passé, sinon le message d'erreur.
        /// </summary>
        public static string FlashFirmware(PedalLink link, PedalConfig backup, Action<string> progress)
        {
            var info = Latest;
            if (info == null || info.FirmwareAsset == null) return "Aucun firmware publié";
            var port = link.PortName;
            if (link.State != LinkState.Connected || port == null) return "Pédalier non connecté";

            string hex, avrdude, conf;
            try
            {
                progress("Téléchargement du firmware " + Format(info.FirmwareVersion) + "…");
                var dir = TempDir("PedalierDIY_firmware");
                var sums = ReadSums(info);
                hex = VerifiedDownload(info, sums, info.FirmwareAsset, dir);
                avrdude = VerifiedDownload(info, sums, "avrdude.exe", dir);
                conf = VerifiedDownload(info, sums, "avrdude.conf", dir);
            }
            catch (Exception e) { return "Téléchargement : " + e.Message; }

            progress("Passage du pédalier en mode programmation…");
            link.Disconnect();
            Thread.Sleep(400);
            var before = SafePortNames();
            try
            {
                // Port ouvert à 1200 bauds puis DTR coupé : le cœur Arduino redémarre sur le programme de démarrage
                using (var sp = new SerialPort(port, 1200))
                {
                    sp.DtrEnable = true;
                    sp.Open();
                    Thread.Sleep(50);
                    sp.DtrEnable = false;
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) { SimHub.Logging.Current.Warn("[PedalierDIY] Ouverture à 1200 bauds : " + e.Message); }

            var boot = WaitBootloaderPort(port, before, 10000);
            if (boot == null)
            {
                link.WantConnected = true;
                return "Le pédalier n'est pas passé en mode programmation. Débranche-le, rebranche-le et réessaie.";
            }

            progress("Écriture du firmware (" + boot + ")… ne débranche pas le pédalier");
            string output;
            int code = RunAvrdude(avrdude, string.Format("-C \"{0}\" -p atmega32u4 -c avr109 -P {1} -b 57600 -D -U flash:w:{2}:i",
                conf, boot, Path.GetFileName(hex)), Path.GetDirectoryName(hex), out output);
            SimHub.Logging.Current.Info("[PedalierDIY] avrdude (code " + code + ") :\n" + output);
            link.WantConnected = true;
            if (code != 0)
            {
                var last = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault() ?? "";
                return "L'écriture a échoué (" + last + "). Relance la mise à jour : le pédalier ne risque rien.";
            }

            progress("Redémarrage du pédalier…");
            var until = DateTime.UtcNow.AddSeconds(15);
            while (link.State != LinkState.Connected && DateTime.UtcNow < until) Thread.Sleep(200);
            if (link.State != LinkState.Connected) return "Firmware écrit, mais le pédalier ne répond pas encore : débranche-le et rebranche-le.";

            // Nouveau format de configuration : le pédalier est reparti sur ses valeurs d'usine
            var diag = link.Request("DIAG", 1500).Final ?? "";
            if (backup != null && diag.Contains("SRC=USINE"))
            {
                progress("Restauration de tes réglages…");
                foreach (var c in backup.DiffCommands(null)) link.Request(c, c.Contains(" SPS ") ? 3000 : 1500);
                link.Request("SAVE", 3000);
            }
            return null;
        }

        private static string[] SafePortNames()
        {
            try { return SerialPort.GetPortNames(); } catch { return new string[0]; }
        }

        /// <summary>Port du programme de démarrage : un nouveau port, ou le même qui disparaît puis revient.</summary>
        private static string WaitBootloaderPort(string port, string[] before, int timeoutMs)
        {
            bool gone = false;
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var now = SafePortNames();
                var added = now.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
                if (added.Count > 0) { Thread.Sleep(300); return added[0]; }
                if (!now.Contains(port, StringComparer.OrdinalIgnoreCase)) gone = true;
                else if (gone) { Thread.Sleep(300); return port; }
                Thread.Sleep(100);
            }
            return null;
        }

        private static int RunAvrdude(string exe, string args, string workDir, out string output)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } output = sb.ToString(); return -1; }
                p.WaitForExit();
                output = sb.ToString();
                return p.ExitCode;
            }
        }

        // ─── Outils ───

        private static string TempDir(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Dictionary<string, string> ReadSums(ReleaseInfo info)
        {
            var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = Encoding.UTF8.GetString(Download(CheckedUrl(info, SumsName)));
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line, @"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$");
                if (m.Success) sums[m.Groups[2].Value] = m.Groups[1].Value.ToLowerInvariant();
            }
            return sums;
        }

        private static string VerifiedDownload(ReleaseInfo info, Dictionary<string, string> sums, string name, string dir)
        {
            string expected;
            if (!sums.TryGetValue(name, out expected)) throw new Exception(name + " absent de " + SumsName);
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, Download(CheckedUrl(info, name)));
            if (Sha256(path) != expected) throw new Exception("empreinte SHA-256 de " + name + " différente : fichier corrompu ou modifié");
            return path;
        }

        private static string CheckedUrl(ReleaseInfo info, string name)
        {
            string url;
            if (!info.Assets.TryGetValue(name, out url)) throw new Exception(name + " absent de la release");
            var prefix = string.Format("https://github.com/{0}/{1}/releases/download/", Owner, Repo);
            if (ApiBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new Exception("adresse de téléchargement inattendue, refusée");
            return url;
        }

        private static byte[] Download(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = WebRequest.Create(url);
            req.Timeout = 30000;
            var http = req as HttpWebRequest;
            if (http != null)
            {
                http.UserAgent = "User.PedalierDIY-updater";
                http.Accept = url.Contains("/releases/download/") ? "application/octet-stream" : "application/vnd.github+json";
            }
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        public static Version ParseVersion(string s)
        {
            var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }
}
