using System;
using System.Globalization;
using BepInEx.Logging;
using ProjectM;
using Steamworks;
using Stunlock.Network;
using Stunlock.Network.Steam;
using Stunlock.Network.Steam.Wrapper;
using Unity.Entities;
using UnityEngine;

namespace VRNetworkPanel
{
    /// <summary>
    /// A Dota 2 style network readout: one thin line of FPS, ping, jitter and packet loss in / out,
    /// optionally a second line of traffic. READ-ONLY - it writes nothing to the game.
    ///
    /// WHERE THE NUMBERS COME FROM (measured from the interop assemblies, game build of
    /// 2026-11-01):
    ///
    ///   Client_0 world -> ProjectM.ClientBootstrapSystem._NetClient : IClientNetworkLayer
    ///     -> Stunlock.Network.ClientNetworkLayer._TransportLayer : IClientTransportLayer
    ///       -> Stunlock.Network.Steam.ClientSteamTransportLayer._NetClient
    ///         -> SteamNetworkingClient.GetQuickConnectionStatus() : QuickConnectionStatus
    ///
    /// QuickConnectionStatus is Stunlock's copy of Steam's SteamNetConnectionRealTimeStatus_t.
    /// Ping is milliseconds, by Steam's definition. ConnectionQualityLocal is the fraction of the
    /// server's packets that reached us end to end, in order, so loss IN = 1 - it;
    /// ConnectionQualityRemote is the same fraction as the server observed for OUR packets, so
    /// loss OUT = 1 - it. Steam reports a negative value while it does not know yet, and that is
    /// shown as a dash rather than as 0% or 100%.
    ///
    /// The other two transports in the build (Lidgren, EOS) expose no delivery-quality figure,
    /// so on them loss reads as a dash and ping falls back to ClientBootstrapSystem.Latency,
    /// whose UNIT IS UNMEASURED. Do not promise loss on a non-Steam transport.
    ///
    /// Nothing from the world is cached across samples. Leave Game destroys ClientBootstrapSystem
    /// and the transport with it, and calling into a freed one is the silent native crash
    /// WorldGuard exists for - so every sample walks the chain again from the world, behind
    /// WorldGuard.Usable(). That is ten probes a second (ProbeSeconds), never per frame.
    /// </summary>
    internal sealed class NetPanel
    {
        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        internal NetPanel(Settings cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        // SteamPending: the Steam transport is there but its status is not readable on this probe
        // (still FindingRoute while the game layer already says Connected, a one-probe IsValid
        // blip, or ClosedByPeer on the way out). It is a GAP, not a change of transport: it keeps
        // the last reading, logs nothing and does not restart the jitter window. Treating it as
        // LatencyOnly logged "not Steam" on a Steam server once per connect and wiped jitter on
        // every blip. A gap longer than PendingStaleSeconds clears the reading to dashes.
        private enum Source { None, NoWorld, NotConnected, Steam, SteamPending, LatencyOnly }

        // ---------------------------------------------------------------- the reading

        private Source _source = Source.None;
        private Source _loggedSource = Source.None;
        private int _pingMs = -1;               // -1 = unknown
        private bool _pingGuessed;              // from Latency, whose unit is unmeasured
        private float _lossIn = -1f, _lossOut = -1f; // 0..1, negative = unknown
        private float _inKBs = -1f, _outKBs = -1f, _inPps = -1f, _outPps = -1f;
        private float _fps = -1f;
        private float _jitterMs = -1f;          // computed at publish time; -1 = not settled yet
        private float _pendingSince = -1f;
        private const float PendingStaleSeconds = 2f;

        // ---- jitter
        //
        // The connection is PROBED every ProbeSeconds, and the display only changes every
        // SampleMs. The probe rate is not a setting: it is what lets a ping update be seen close
        // to when it happens.
        //
        // JITTER = the mean size of each real ping CHANGE over the last JitterWindowSeconds, in ms.
        // That is RFC 3550's "mean |delta|" idea, taken over Steam's own updates rather than over
        // probes. Steam's ping is a smoothed estimate that repeats between its updates, so a
        // per-probe delta would mostly be zero and dilute the mean by however many probes landed
        // between two updates. Only a probe that sees a different value records a delta.
        //
        // What it is NOT:
        //  - A standard deviation. That counted slow drift and one-off steps as jitter: a route
        //    change from 86 to 120 ms mid-window read 17 ms "jitter" for 5 s on a connection that
        //    was flat on both sides.
        //  - Per-packet jitter. The input is already smoothed, so this reads LOWER than packet
        //    timing would, and it cannot see packet bursts at all.
        //  - Immune to a step. A single step still adds one delta and lifts the mean until it ages
        //    out of the window, at most JitterWindowSeconds.
        //
        // The window is by AGE, not by probe count. Probes are quantised to frames (a 100 ms probe
        // at 60 FPS lands every ~117 ms), so "50 probes" meant 5.8 s at 60 FPS and 10 s at 10 FPS.
        private const float ProbeSeconds = 0.1f;
        private const float JitterWindowSeconds = 5f;
        private const float JitterSettleSeconds = 2f; // watched this long before a number shows
        private const int DeltaCap = 128;             // far above any plausible update rate
        private readonly float[] _deltaAt = new float[DeltaCap];
        private readonly int[] _deltaMs = new int[DeltaCap];
        private int _deltaHead, _deltaCount;
        private int _lastPing = -1;
        private float _jitterSince = -1f;

        // Set once when the Steam status call throws (a stripped body, a patch that moved it).
        // From then on the probe falls back to Latency only, said once in the log - not retried
        // every probe into an error throttle that would make it look like a crash.
        private bool _steamCallBroken;
        private string _pendingDetail = "(none)"; // last IsValid / state seen, for that warning
        private bool _bootLookupWarned;

        private void MarkSteamBroken(string what, string detail)
        {
            _steamCallBroken = true;
            _log.LogWarning($"Network panel: {what}, so packet loss will not be shown until you "
                          + $"reconnect - ping falls back to the game's own latency figure. {detail}");
        }

        private bool _active;
        private float _nextSample;              // probe timer, ProbeSeconds
        private float _nextPublish;             // display timer, SampleMs
        private float _fpsWindowStart = -1f;
        private int _fpsFrames;

        /// <summary>FPS every frame (it is a frame counter), the connection every ProbeSeconds,
        /// the visible numbers every SampleMs.</summary>
        public void Update()
        {
            // Keyed on a flag of its own, not on _source: switched off inside the first sample
            // window, _source is still None while the FPS window is half full, and switching
            // back on minutes later would divide those few frames by the whole gap.
            if (!_cfg.Enabled.Value)
            {
                if (_active) { Reset(); _active = false; }
                return;
            }
            _active = true;

            float now = Time.unscaledTime;
            // The frame that opens a window is its start, not one of its frames - counting it
            // read 62 at 60 FPS on the first window after enabling.
            if (_fpsWindowStart < 0f) { _fpsWindowStart = now; _fpsFrames = 0; }
            else _fpsFrames++;
            float window = now - _fpsWindowStart;
            if (window >= 0.5f)
            {
                _fps = _fpsFrames / window;
                _fpsWindowStart = now;
                _fpsFrames = 0;
                _dirtyFps = true;
            }

            if (now >= _nextSample)
            {
                // += rather than now + : the frame-quantisation error then does not accumulate.
                // More than one interval behind (a hitch, the first probe) restarts the cadence
                // instead of firing a burst of catch-up probes.
                _nextSample += ProbeSeconds;
                if (_nextSample <= now) _nextSample = now + ProbeSeconds;
                Sample(now);
            }

            // Only here does the visible text move. The probe above refreshes the fields ten
            // times a second; publishing at that rate is the flicker SampleMs prevents.
            if (now >= _nextPublish)
            {
                _nextPublish = now + Mathf.Max(_cfg.SampleMs.Value, 100f) / 1000f;
                _jitterMs = Jitter(now);
                _dirtyNet = true;
            }
        }

        private void ClearJitter()
        {
            _deltaHead = _deltaCount = 0;
            _lastPing = -1;
            _jitterSince = -1f;
        }

        private void ObservePing(int ping, float now)
        {
            if (_jitterSince < 0f) _jitterSince = now;
            if (_lastPing >= 0 && ping != _lastPing)
            {
                _deltaAt[_deltaHead] = now;
                _deltaMs[_deltaHead] = Math.Abs(ping - _lastPing);
                _deltaHead = (_deltaHead + 1) % DeltaCap;
                if (_deltaCount < DeltaCap) _deltaCount++;
            }
            _lastPing = ping;
        }

        /// <summary>Drop deltas older than the window. The ring is in time order, so the oldest
        /// is at head - count.</summary>
        private void TrimDeltas(float now)
        {
            while (_deltaCount > 0)
            {
                int oldest = (_deltaHead - _deltaCount + DeltaCap) % DeltaCap;
                if (now - _deltaAt[oldest] <= JitterWindowSeconds) break;
                _deltaCount--;
            }
        }

        /// <summary>Mean |change| over the window, in ms. -1 until the run has been watched for
        /// JitterSettleSeconds; 0 once it has and ping has not moved at all.</summary>
        private float Jitter(float now)
        {
            if (_jitterSince < 0f || now - _jitterSince < JitterSettleSeconds) return -1f;
            TrimDeltas(now);
            if (_deltaCount == 0) return 0f;
            long sum = 0;
            for (int i = 0; i < _deltaCount; i++)
                sum += _deltaMs[(_deltaHead - 1 - i + DeltaCap) % DeltaCap];
            return (float)sum / _deltaCount;
        }

        private void ClearReading()
        {
            _pingMs = -1;
            _pingGuessed = false;
            _lossIn = _lossOut = -1f;
            _inKBs = _outKBs = _inPps = _outPps = -1f;
        }

        // Steam and SteamPending are one run: a gap does not restart the jitter window.
        private static bool SameRun(Source a, Source b)
            => a == b || (IsSteam(a) && IsSteam(b));
        private static bool IsSteam(Source s) => s == Source.Steam || s == Source.SteamPending;

        private void Sample(float now)
        {
            var source = Read();

            if (!SameRun(source, _source)) ClearJitter();

            if (source == Source.SteamPending)
            {
                // A gap: keep the last reading, unless the gap has gone on too long to be one.
                // Too long to be a gap: the Steam status is not going to become readable on this
                // connection. Say so once and fall back to the game's latency figure from the
                // next probe on, rather than showing dashes all session under a stale log line.
                if (_pendingSince < 0f) _pendingSince = now;
                if (now - _pendingSince > PendingStaleSeconds)
                {
                    ClearReading();
                    ClearJitter();
                    MarkSteamBroken($"the Steam connection status stayed unreadable for "
                                  + $"{PendingStaleSeconds:0}s", "last read " + _pendingDetail);
                    _pendingSince = -1f;
                }
            }
            else
            {
                _pendingSince = -1f;
                if (source == Source.NoWorld || source == Source.NotConnected) ClearReading();
                if (_pingMs >= 0) ObservePing(_pingMs, now);
                else ClearJitter(); // no value is not a value; do not bridge across it
            }
            _source = source;

            // The latch costs one warning per CONNECTION, not per session: a transient throw
            // during a state change must not turn loss off after reconnecting to a healthy server.
            if (source == Source.NotConnected || source == Source.NoWorld) _steamCallBroken = false;
            // A gap is not news; logging it would be the flapping this state exists to stop.
            if (source != Source.SteamPending && source != _loggedSource) LogSource(source);
        }

        /// <summary>Walk the chain from the world, fresh. Fills the fields it can reach and
        /// says how far it got.</summary>
        private Source Read()
        {
            World client = FindClient();
            if (!WorldGuard.Usable(client)) return Source.NoWorld;

            ClientBootstrapSystem boot;
            try { boot = client.GetExistingSystemManaged<ClientBootstrapSystem>(); }
            catch (Exception e)
            {
                // Said once a session: a lookup that throws (stripping, a patch moving the type)
                // would otherwise read as "no world" forever with nothing in the log.
                if (!_bootLookupWarned)
                {
                    _bootLookupWarned = true;
                    _log.LogWarning("Could not look up ClientBootstrapSystem, so only FPS can be "
                                  + $"shown: {e.Message}");
                }
                return Source.NoWorld;
            }
            if (boot == null) return Source.NoWorld;

            IClientNetworkLayer layer = boot._NetClient;
            ClientNetworkLayer cnl = layer?.TryCast<ClientNetworkLayer>();
            if (cnl == null || cnl._ConnectState != ClientConnectState.Connected)
                return Source.NotConnected;

            if (!_steamCallBroken)
            {
                var steam = cnl._TransportLayer?.TryCast<ClientSteamTransportLayer>();
                var net = steam?._NetClient;
                if (net != null)
                {
                    // The call AND every field read sit inside the latch: if a property body was
                    // stripped, reading it would otherwise throw on every probe into the Update
                    // error throttle and freeze the panel - the failure the latch exists to stop.
                    bool live = false;
                    try
                    {
                        QuickConnectionStatus q = net.GetQuickConnectionStatus();
                        bool valid = q.IsValid;
                        var state = q.ConnectionState;
                        _pendingDetail = $"valid={valid} state={state}";
                        if (valid && state == ESteamNetworkingConnectionState
                                                  .k_ESteamNetworkingConnectionState_Connected)
                        {
                            int ping = q.Ping;
                            float lossIn = Loss(q.ConnectionQualityLocal);
                            float lossOut = Loss(q.ConnectionQualityRemote);
                            float inKBs = q.InBytesPerSec / 1024f, outKBs = q.OutBytesPerSec / 1024f;
                            float inPps = q.InPacketsPerSec, outPps = q.OutPacketsPerSec;
                            _pingGuessed = false;
                            _pingMs = ping >= 0 ? ping : -1;
                            _lossIn = lossIn;
                            _lossOut = lossOut;
                            _inKBs = inKBs;
                            _outKBs = outKBs;
                            _inPps = inPps;
                            _outPps = outPps;
                            live = true;
                        }
                    }
                    catch (Exception e)
                    {
                        MarkSteamBroken("the Steam connection status call failed", e.Message);
                    }
                    if (live) return Source.Steam;
                    // Steam transport, status not readable on this probe: a gap, see Source.
                    if (!_steamCallBroken) return Source.SteamPending;
                }
            }

            // Lidgren, EOS, or a Steam transport whose status call is broken this connection.
            ClearReading();
            try { _pingMs = LatencyToMs(boot.Latency); } catch { _pingMs = -1; }
            _pingGuessed = _pingMs >= 0;
            return Source.LatencyOnly;
        }

        /// <summary>Delivery quality 0..1 to loss 0..1. Steam uses a negative value for "not
        /// known yet"; that stays unknown rather than becoming 100% loss.</summary>
        private static float Loss(float quality)
        {
            if (float.IsNaN(quality) || quality < 0f) return -1f;
            return Mathf.Clamp01(1f - quality);
        }

        /// <summary>
        /// ClientBootstrapSystem.Latency in ms - UNIT UNMEASURED. Lidgren's own round-trip figure
        /// is seconds, so a value under 3 is read as seconds; 3 or more as milliseconds already.
        /// This IS wrong in one regime whichever unit is real (ms: loopback 0.4 reads as 400 ms;
        /// seconds: a 5 s stall reads as 5 ms), so a number from here is always drawn with a "?"
        /// and in the unknown colour - never green or red.
        /// </summary>
        private static int LatencyToMs(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) return -1;
            return Mathf.RoundToInt(v < 3f ? v * 1000f : v);
        }

        private static World FindClient()
        {
            var all = World.s_AllWorlds;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w != null && w.IsCreated && w.Name == "Client_0") return w;
            }
            return null;
        }

        // One line per change of source, never per sample.
        private void LogSource(Source s)
        {
            _loggedSource = s;
            switch (s)
            {
                case Source.Steam:
                    _log.LogInfo("Reading the Steam connection - ping, loss in/out and traffic are live.");
                    break;
                case Source.LatencyOnly:
                    _log.LogInfo("Connected over a transport with no delivery-quality figure (not "
                               + "Steam, or Steam status unreadable) - showing ping only, loss reads "
                               + "'-'.");
                    break;
                case Source.NotConnected:
                    _log.LogInfo("No server connection - showing FPS only.");
                    break;
            }
        }

        public void Reset()
        {
            _source = Source.None;
            _loggedSource = Source.None;
            ClearReading();
            _fps = -1f;
            _jitterMs = -1f;
            ClearJitter();
            _pendingSince = -1f;
            _nextPublish = 0f;
            _steamCallBroken = false;
            _fpsWindowStart = -1f;
            _nextSample = 0f;
            _dirtyFps = _dirtyNet = true;
        }

        // ---------------------------------------------------------------- drawing
        //
        // Each line is ONE GUI.Label of rich text - "FPS: 175   PING: 90 ms ..." with the labels
        // grey and each value in its own colour via <color> tags. That is what lets the text flow
        // as "LABEL: value" with no gap: the alternative, a label per piece placed side by side,
        // needs each piece's width, and GUIStyle.CalcSize is not proven to survive stripping in
        // this game. Rich text is parsed by the native text renderer, and GUIStyle.richText is a
        // plain style setter like wordWrap and fontSize, which already work here.
        //
        // The background strip's width is ESTIMATED from per-character widths (EmWidth), sized
        // for the WIDEST values each line can show, so the strip does not twitch as digits change.

        private GUIStyle _style;
        private int _styleFontSize = -1;
        private Texture2D _bg;
        private bool _dirtyFps = true, _dirtyNet = true;

        // Rebuilt only at publish (SampleMs) or when the FPS window closes, never per frame.
        private string _fpsPart = "", _netPart = "", _line1 = "", _line2 = "";

        private static readonly Color LabelColor = new Color(0.62f, 0.64f, 0.68f);
        private static readonly Color GoodColor = new Color(0.88f, 0.92f, 0.88f);
        private static readonly Color WarnColor = new Color(1f, 0.8f, 0.3f);
        private static readonly Color BadColor = new Color(1f, 0.38f, 0.32f);
        private static readonly Color UnknownColor = new Color(0.5f, 0.5f, 0.55f);

        private const string Dash = "-";
        private const string Sep = "    ";

        // The widest each line can get, plain text. Used only to size the strip and to shrink
        // the font on a narrow screen - never drawn.
        private const string WidestLine1 =
            "FPS: 999" + Sep + "PING: 9999 ms?" + Sep + "JITTER: 999 ms?" + Sep
            + "LOSS IN: 100.0%" + Sep + "OUT: 100.0%";
        private const string WidestLine2 =
            "IN: 9999.9 KB/s 9999 pkt/s" + Sep + "OUT: 9999.9 KB/s 9999 pkt/s";
        private static readonly float Line1Em = EmWidth(WidestLine1);
        private static readonly float Line2Em = EmWidth(WidestLine2);

        private static readonly string LabelHex = Hex(LabelColor);

        private void RebuildFps()
        {
            _dirtyFps = false;
            // Capped at the width WidestLine1 allows for (an uncapped menu can run past 1000).
            string fps = _fps < 0f ? Dash
                       : Mathf.Min(Mathf.RoundToInt(_fps), 999).ToString(CultureInfo.InvariantCulture);
            Color c = _fps < 0f ? UnknownColor : _fps >= 50f ? GoodColor : _fps >= 30f ? WarnColor : BadColor;
            _fpsPart = Part("FPS", fps, c);
            _line1 = _fpsPart + Sep + _netPart;
        }

        private void RebuildNet()
        {
            _dirtyNet = false;
            var ci = CultureInfo.InvariantCulture;

            string ping = _pingMs < 0 ? Dash : _pingMs.ToString(ci) + (_pingGuessed ? " ms?" : " ms");
            Color cPing = _pingMs < 0 || _pingGuessed ? UnknownColor
                        : _pingMs < 80 ? GoodColor : _pingMs < 150 ? WarnColor : BadColor;

            // Built from the same pings as PING, so a guessed unit taints it the same way.
            string jit = _jitterMs < 0f ? Dash
                       : Mathf.Min(Mathf.RoundToInt(_jitterMs), 999).ToString(ci) + (_pingGuessed ? " ms?" : " ms");
            Color cJit = _jitterMs < 0f || _pingGuessed ? UnknownColor
                       : _jitterMs < 10f ? GoodColor : _jitterMs < 30f ? WarnColor : BadColor;

            _netPart = Part("PING", ping, cPing) + Sep
                     + Part("JITTER", jit, cJit) + Sep
                     + Part("LOSS IN", LossText(_lossIn), LossColor(_lossIn)) + Sep
                     + Part("OUT", LossText(_lossOut), LossColor(_lossOut));
            _line1 = _fpsPart + Sep + _netPart;

            // All label colour, so no tags beyond the one wrapping it.
            string traffic = _inKBs < 0f
                ? "IN: -" + Sep + "OUT: -"
                : $"IN: {_inKBs.ToString("0.0", ci)} KB/s {_inPps.ToString("0", ci)} pkt/s" + Sep
                + $"OUT: {_outKBs.ToString("0.0", ci)} KB/s {_outPps.ToString("0", ci)} pkt/s";
            _line2 = _plain ? traffic : "<color=#" + LabelHex + ">" + traffic + "</color>";
        }

        /// <summary>"LABEL: value" with the label grey and the value in its own colour. Every
        /// string here is ours (digits, "ms", "%", "-"), so nothing needs escaping.</summary>
        private string Part(string label, string value, Color valueColor)
            => _plain ? label + ": " + value
             : "<color=#" + LabelHex + ">" + label + ":</color> <color=#" + Hex(valueColor) + ">"
             + value + "</color>";

        private bool _plain; // GUIStyle.richText threw: draw without tags (see EnsureStyle)

        /// <summary>RRGGBB. Written out rather than ColorUtility.ToHtmlStringRGB, which no code in
        /// this game is known to call - see the stripping note above.</summary>
        private static string Hex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture)
                 + b.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Approximate width of plain text in font-size units, from Arial Bold advance widths
        /// (Unity's built-in IMGUI font), rounded up. An estimate on purpose - see the section
        /// note - and erring wide, so the strip never ends inside the text.
        /// </summary>
        private static float EmWidth(string s)
        {
            float w = 0f;
            foreach (char ch in s)
            {
                if (ch == ' ' || ch == '.' || ch == ':' || ch == '/') w += 0.28f;
                else if (ch >= '0' && ch <= '9') w += 0.56f;
                else if (ch == '%') w += 0.89f;
                else if (ch == '-') w += 0.34f;
                else if (ch == '?') w += 0.61f;
                else if (ch >= 'A' && ch <= 'Z') w += 0.73f;
                else w += 0.61f; // lower case and anything else
            }
            return w * 1.05f;
        }

        private static string LossText(float l)
            => l < 0f ? Dash : (l * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        private static Color LossColor(float l)
            => l < 0f ? UnknownColor : l < 0.01f ? GoodColor : l < 0.05f ? WarnColor : BadColor;

        /// <summary>
        /// Draw the readout, Repaint pass only. Uses only GUI.Label, GUI.DrawTexture, GUI.color,
        /// GUIStyle, Texture2D and Event.current.type - the IMGUI calls proven to survive
        /// stripping in this game - plus GUIStyle.richText, a style setter of the same kind.
        /// No GUIStyle.CalcSize.
        /// </summary>
        public void Draw()
        {
            if (!_cfg.Enabled.Value) return;
            var ev = Event.current;
            if (ev != null && ev.type != EventType.Repaint) return;

            bool traffic = _cfg.ShowTraffic.Value;
            float em = traffic ? Mathf.Max(Line1Em, Line2Em) : Line1Em;

            // Clamp the size so the strip never runs off a narrow screen.
            const float pad = 6f, margin = 8f;
            int size = Mathf.Clamp(_cfg.FontSize.Value, 8, 40);
            int fit = Mathf.FloorToInt((Screen.width - 2f * (pad + margin)) / em);
            EnsureStyle(Mathf.Max(8, Mathf.Min(size, fit)));
            if (_dirtyFps) RebuildFps();
            if (_dirtyNet) RebuildNet();

            float fs = _styleFontSize;
            float lineH = fs + 6f;
            float textW = Mathf.Ceil(fs * em);
            float width = textW + pad * 2f;
            float height = lineH * (traffic ? 2 : 1) + pad * 2f - 2f;

            Corner(width, height, margin, out float x, out float y);

            var prev = GUI.color;
            GUI.color = Color.white; // the colours are in the text; a tint would multiply them
            if (_cfg.Background.Value && _bg != null)
                GUI.DrawTexture(new Rect(x, y, width, height), _bg);

            float cy = y + pad - 1f;
            GUI.Label(new Rect(x + pad, cy, textW, lineH), _line1, _style);
            if (traffic) GUI.Label(new Rect(x + pad, cy + lineH, textW, lineH), _line2, _style);
            GUI.color = prev;
        }

        /// <summary>Place the box by Corner: "Bottom..." puts it at the bottom, "...Left" /
        /// "...Right" at that side, anything else top / centre.</summary>
        private void Corner(float w, float h, float m, out float x, out float y)
        {
            string c = _cfg.Corner.Value ?? "";
            bool bottom = c.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase);
            bool left = c.EndsWith("Left", StringComparison.OrdinalIgnoreCase);
            bool right = c.EndsWith("Right", StringComparison.OrdinalIgnoreCase);
            x = left ? m : right ? Screen.width - w - m : (Screen.width - w) * 0.5f;
            y = bottom ? Screen.height - h - m : m;
        }

        private void EnsureStyle(int size)
        {
            if (_style == null)
            {
                // Built in a local and kept only once complete: a setter that throws must not
                // leave a half-built style that the null check then never rebuilds.
                var st = new GUIStyle();
                st.alignment = TextAnchor.MiddleLeft;
                st.wordWrap = false;
                st.clipping = TextClipping.Overflow; // an estimate a little short must not cut text
                st.fontStyle = FontStyle.Bold;
                // White once; the <color> tags carry the real colours. GUIStyle.normal
                // allocates per read, so it is touched only here.
                st.normal.textColor = Color.white;
                try { st.richText = true; }
                catch (Exception e)
                {
                    // Fail open: one colour without tags beats literal "<color=...>" text.
                    _plain = true;
                    _dirtyFps = _dirtyNet = true;
                    _log.LogWarning($"Rich text is unavailable, so the readout is drawn in one colour: {e.Message}");
                }
                _style = st;
            }
            if (size != _styleFontSize)
            {
                _styleFontSize = size;
                _style.fontSize = size;
            }
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.04f, 0.04f, 0.06f, 0.62f));
                _bg.Apply();
                _bg.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        /// <summary>The background texture is HideAndDontSave, so a hot reload would leak it.</summary>
        public void Dispose()
        {
            Reset();
            if (_bg != null) UnityEngine.Object.Destroy(_bg);
            _bg = null;
        }
    }
}
