using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace User.PedalierDIY
{
    public enum LinkState { Disconnected, Searching, Connected }

    /// <summary>Dernière ligne de flux "S ..." reçue du pédalier.</summary>
    public sealed class LiveData
    {
        public int AccRaw, AccCal, AccOut, BrkRaw, BrkCal, BrkOut, Flags;
        public bool AccOk { get { return (Flags & 1) != 0; } }
        public bool BrkOk { get { return (Flags & 2) != 0; } }
        public bool MagnetWarning { get { return (Flags & 8) != 0; } }
        public static readonly LiveData Empty = new LiveData();
    }

    /// <summary>Réponse à une commande : lignes intermédiaires + ligne finale (OK…, ERR… ou DIAG…).</summary>
    public sealed class Reply
    {
        public string Final;
        public List<string> Lines = new List<string>();
        public bool Ok { get { return Final != null && Final.StartsWith("OK"); } }
        public string Error
        {
            get
            {
                if (Final == null) return "pas de réponse du pédalier";
                return Final.StartsWith("ERR") ? Final.Substring(3).Trim() : null;
            }
        }
    }

    /// <summary>
    /// Liaison série avec le pédalier (voir PROTOCOLE.md du firmware).
    /// - Recherche : seuls les ports USB du pédalier (VID 2341, PID 80F0) sont ouverts, pour ne
    ///   jamais déranger les autres Arduino gérés par SimHub ; un port choisi à la main est essayé tel quel.
    /// - Un fil lit les lignes en continu (flux "S" et réponses) ; les commandes passent une par une.
    /// - Si le pédalier est débranché, la liaison retente toute seule toutes les 2 s.
    /// </summary>
    public sealed class PedalLink : IDisposable
    {
        public const string DeviceId = "PEDALIER_DIY";
        private const string UsbId = "VID_2341&PID_80F0";

        private readonly object _cmdLock = new object();
        private readonly object _respLock = new object();
        private readonly ManualResetEvent _respEvent = new ManualResetEvent(false);
        private SerialPort _port;
        private Thread _reader, _supervisor;
        private volatile bool _stop, _closing;
        private volatile LinkState _state = LinkState.Disconnected;
        private Reply _pending;
        private int _streamCount, _streamHz;
        private DateTime _streamWindow = DateTime.UtcNow;

        public volatile bool WantConnected;
        public volatile string PreferredPort = "AUTO";
        public volatile LiveData Live = LiveData.Empty;

        /// <summary>Lignes de flux par seconde demandées au pédalier (30 au repos, 100 sur l'écran en direct).</summary>
        public const int IdleStreamHz = 30, FastStreamHz = 100;
        private volatile int _streamRate = IdleStreamHz;

        public LinkState State { get { return _state; } }
        public string PortName { get; private set; }
        public string Firmware { get; private set; }
        public string LastError { get; private set; }
        public int StreamHz { get { return _streamHz; } }

        /// <summary>Messages qui arrivent sans commande en attente (pour le journal).</summary>
        public event Action<string> Unsolicited;

        /// <summary>Levé (depuis un fil de fond) à chaque connexion réussie.</summary>
        public event Action Connected;

        public void Start()
        {
            _supervisor = new Thread(SupervisorLoop) { IsBackground = true, Name = "PedalierDIY link" };
            _supervisor.Start();
        }

        public void Dispose()
        {
            _stop = true;
            Close(null);
        }

        public void Disconnect()
        {
            WantConnected = false;
            Close(null);
        }

        /// <summary>Change la cadence du flux (sans bloquer l'appelant). Gardée pour les reconnexions.</summary>
        public void SetStreamRate(int hz)
        {
            if (hz == _streamRate) return;
            _streamRate = hz;
            if (_state != LinkState.Connected) return;
            ThreadPool.QueueUserWorkItem(_ => Request("STREAM ON " + hz, 1000));
        }

        private void SupervisorLoop()
        {
            while (!_stop)
            {
                if (WantConnected && _state == LinkState.Disconnected) TryConnect();
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
            }
        }

        /// <summary>Ports COM des pédaliers branchés, d'après l'identifiant USB.</summary>
        public static List<string> FindPedalPorts()
        {
            var list = new List<string>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var id = (o["DeviceID"] as string ?? "").ToUpperInvariant();
                        var name = o["Name"] as string ?? "";
                        if (!id.Contains(UsbId)) continue;
                        var m = Regex.Match(name, @"\((COM\d+)\)");
                        if (m.Success) list.Add(m.Groups[1].Value);
                    }
                }
            }
            catch (Exception e)
            {
                SimHub.Logging.Current.Warn("[PedalierDIY] Recherche WMI impossible : " + e.Message);
            }
            return list;
        }

        private void TryConnect()
        {
            _state = LinkState.Searching;
            var pref = PreferredPort;
            var candidates = string.IsNullOrEmpty(pref) || pref == "AUTO" ? FindPedalPorts() : new List<string> { pref };
            if (candidates.Count == 0) LastError = "pédalier introuvable (branché ? firmware à jour ?)";
            foreach (var name in candidates)
            {
                if (_stop || !WantConnected) break;
                if (Open(name)) return;
            }
            if (_state != LinkState.Connected) _state = LinkState.Disconnected;
        }

        private bool Open(string name)
        {
            try
            {
                var p = new SerialPort(name, 115200)
                {
                    DtrEnable = true,           // le firmware n'envoie le flux que port ouvert (DTR)
                    RtsEnable = true,
                    NewLine = "\n",
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 300,
                    WriteTimeout = 1000,
                };
                p.Open();
                p.DiscardInBuffer();
                _port = p;
                _closing = false;
                _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "PedalierDIY reader" };
                _reader.Start();

                var hello = Request("HELLO", 1500);
                if (hello.Final == null || !hello.Final.StartsWith("OK " + DeviceId))
                {
                    LastError = name + " : ce n'est pas un pédalier DIY";
                    Close(null);
                    return false;
                }
                var m = Regex.Match(hello.Final, @"FW=(\S+)");
                Firmware = m.Success ? m.Groups[1].Value : "?";
                PortName = name;
                LastError = null;
                _state = LinkState.Connected;
                Request("STREAM ON " + _streamRate, 1000);
                SimHub.Logging.Current.Info("[PedalierDIY] Connecté sur " + name + ", firmware " + Firmware);
                var h = Connected;
                if (h != null) h();
                return true;
            }
            catch (Exception e)
            {
                LastError = name + " : " + e.Message;
                Close(null);
                return false;
            }
        }

        private void Close(string error)
        {
            _closing = true;
            var p = _port;
            _port = null;
            if (p != null)
            {
                try { if (p.IsOpen) p.Write("STREAM OFF\n"); } catch { }
                try { p.Close(); } catch { }
                try { p.Dispose(); } catch { }
            }
            if (error != null) LastError = error;
            Live = LiveData.Empty;
            _streamHz = 0;
            _state = LinkState.Disconnected;
            lock (_respLock) { if (_pending != null) _respEvent.Set(); }
        }

        private void ReaderLoop()
        {
            var p = _port;
            while (!_stop && !_closing && p != null)
            {
                string line;
                try { line = p.ReadLine(); }
                catch (TimeoutException) { continue; }
                catch (Exception)
                {
                    if (!_closing) { SimHub.Logging.Current.Warn("[PedalierDIY] Liaison perdue"); Close("pédalier débranché"); }
                    return;
                }
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("S ")) { ParseStream(line); continue; }

                bool handled = false;
                lock (_respLock)
                {
                    if (_pending != null)
                    {
                        handled = true;
                        if (line.StartsWith("OK") || line.StartsWith("ERR") || line.StartsWith("DIAG "))
                        {
                            _pending.Final = line;
                            _respEvent.Set();
                        }
                        else _pending.Lines.Add(line);
                    }
                }
                if (!handled)
                {
                    var h = Unsolicited;
                    if (h != null) h(line);
                }
            }
        }

        private void ParseStream(string line)
        {
            var t = line.Split(' ');
            if (t.Length != 8) return;
            var d = new LiveData();
            if (!int.TryParse(t[1], out d.AccRaw) || !int.TryParse(t[2], out d.AccCal) || !int.TryParse(t[3], out d.AccOut)
                || !int.TryParse(t[4], out d.BrkRaw) || !int.TryParse(t[5], out d.BrkCal) || !int.TryParse(t[6], out d.BrkOut)
                || !int.TryParse(t[7], out d.Flags)) return;
            Live = d;
            _streamCount++;
            var now = DateTime.UtcNow;
            if ((now - _streamWindow).TotalSeconds >= 1)
            {
                _streamHz = _streamCount;
                _streamCount = 0;
                _streamWindow = now;
            }
        }

        /// <summary>
        /// Envoie une commande et attend sa réponse finale (bloquant : à appeler hors du fil de l'interface).
        /// Les commandes sont sérialisées : une seule à la fois sur la liaison.
        /// </summary>
        public Reply Request(string cmd, int timeoutMs = 2000)
        {
            lock (_cmdLock)
            {
                var r = new Reply();
                var p = _port;
                if (p == null) return r;
                lock (_respLock) { _pending = r; _respEvent.Reset(); }
                try { p.Write(cmd + "\n"); }
                catch (Exception e)
                {
                    lock (_respLock) _pending = null;
                    Close("écriture impossible : " + e.Message);
                    return r;
                }
                _respEvent.WaitOne(timeoutMs);
                lock (_respLock) _pending = null;
                return r;
            }
        }
    }
}
