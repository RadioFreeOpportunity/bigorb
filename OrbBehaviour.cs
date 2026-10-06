using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using Mirror.Authenticators;
using UnityEngine;

namespace BigOrb;

public class OrbBehaviour : MonoBehaviour
{
    public OrbBehaviour(IntPtr ptr) : base(ptr) { }

    // the 24 look colours from PlayerLookSet
    internal static readonly string[] LookHex = {
        "#4b72af","#bb3102","#f4cc48","#4895a8","#264186","#ff2c2b","#eaaa32","#295b35",
        "#4a1538","#2b2b2b","#d55701","#f9b8be","#cfe190","#8f8a84","#962550","#1c9063",
        "#60361d","#7a021b","#ede3d9","#00a996","#b29672","#ffa300","#ecfaff","#9f500e" };

    internal static volatile bool NametagsOn;

    private class Track
    {
        public Vector3 LastPos; public float Speed, AirSec, SpeedSec;
        public float LastFlag = -999f; public bool Seen;
        public string Name;
    }

    private readonly Dictionary<string, Track> _tracks = new();
    private readonly Dictionary<string, GameObject> _tags = new();
    private float _nextTick, _nextSnap;
    private bool _wasHosting;

    private void Update()
    {
        while (OrbState.MainQueue.TryDequeue(out var act))
        {
            try { act(); } catch (Exception e) { Plugin.Logger.LogError("cmd: " + e.Message); }
        }

        var hosting = NetworkServer.active;
        if (hosting && !_wasHosting)
        {
            string world = null;
            try { world = SaveManager.worldName; } catch { }
            OrbState.NewSession(world);
            try { Props.SessionReset(); } catch (Exception e) { Plugin.Logger.LogError("pose snapshot: " + e.Message); }
            if (Plugin.AutoOpen.Value) OpenDashboard();
        }
        _wasHosting = hosting;

        if (Time.unscaledTime >= _nextTick)
        {
            _nextTick = Time.unscaledTime + 0.25f;
            try { Tick(hosting); } catch (Exception e) { Plugin.Logger.LogError("tick: " + e.Message); }
            if (hosting)
            {
                try { Scripting.Tick(); } catch (Exception e) { Plugin.Logger.LogError("modules tick: " + e.Message); }
                try { Props.Tick(); } catch (Exception e) { Plugin.Logger.LogError("props tick: " + e.Message); }
            }
        }
        if (Time.unscaledTime >= _nextSnap) { _nextSnap = Time.unscaledTime + 0.5f; try { Snapshot(hosting); } catch (Exception e) { Plugin.Logger.LogError("snap: " + e.Message); } }
        try { UpdateNametags(); } catch { }
        try { Chime.Step(gameObject); } catch (Exception e) { Plugin.Logger.LogError("chime step: " + e.Message); }
    }

    private static void OpenDashboard()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = $"http://localhost:{Plugin.Port.Value}/", UseShellExecute = true });
        }
        catch (Exception e) { Plugin.Logger.LogError("open browser: " + e.Message); }
    }

    // ---------------- enumeration helpers ----------------

    internal static IEnumerable<PlayerCharacter> Players()
    {
        var all = PlayerCharacter.allPlayerCharacters;
        if (all == null) yield break;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc != null && pc.playerNetworking != null) yield return pc;
        }
    }

    internal static PlayerCharacter ById(string id)
    {
        foreach (var pc in Players())
            if (Patches.Pid(pc.playerNetworking) == id) return pc;
        // a raw EOS id (sign authors) still resolves
        foreach (var pc in Players())
            if (pc.playerNetworking.identifier == id) return pc;
        return null;
    }

    private static PeckEffectTextInput FindSign(uint netId)
    {
        var signs = UnityEngine.Object.FindObjectsOfType<PeckEffectTextInput>(true);
        for (int i = 0; i < signs.Length; i++)
            if (signs[i] != null && signs[i].netId == netId) return signs[i];
        return null;
    }

    internal static PlayerCharacter Local()
    {
        foreach (var pc in Players())
            if (pc.playerNetworking.isLocalPlayer) return pc;
        return null;
    }

    internal static string TransformPath(Transform t)
    {
        var parts = new List<string>();
        int guard = 0;
        while (t != null && guard++ < 12) { parts.Add(t.gameObject.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // the lobby-size world variant (2/3/4 player worlds differ)
    internal static int WorldVariant()
    {
        try { var v = (int)PlayerCountSwapper.playerCount; if (v > 0) return v; } catch { }
        try { var v = PlayerNetworking.playerCount; if (v > 0) return v; } catch { }
        return 0;
    }

    // the join code shown on the in-game magic code screen
    internal static string LobbyCode()
    {
        try { return EOSPrivateLobbyManager.Instance?.CurrentLobbyCode ?? ""; } catch { return ""; }
    }

    // the game's password check lives in HouseAuthenticator.password, the session
    // menu's "change password" just writes that field, so we do the same
    internal static HouseAuthenticator Auth()
    {
        try { return NetworkManager.singleton?.authenticator?.TryCast<HouseAuthenticator>(); } catch { return null; }
    }

    // ---------------- periodic work ----------------

    private void Tick(bool hosting)
    {
        if (hosting) { try { Guard.Tick(); } catch (Exception e) { Plugin.Logger.LogError("guard: " + e.Message); } }
        foreach (var t in _tracks.Values) t.Seen = false;

        foreach (var pc in Players())
        {
            var pn = pc.playerNetworking;
            var id = Patches.Pid(pn);
            if (string.IsNullOrEmpty(id)) continue;
            var name = Patches.Display(pn);
            var pos = pc.transform.position;

            if (!_tracks.TryGetValue(id, out var t))
            {
                _tracks[id] = t = new Track { LastPos = pos, Name = name };
                OrbState.AddEvent("join", id, name, OrbState.Platform(id));
                // don't chime for ourselves
                if (hosting && !pn.isLocalPlayer) Chime.Play(true);
            }
            t.Seen = true; t.Name = name;

            try
            {
                OrbState.RosterSeen(id, name, SafeStr(() => pn.username), pn.userPlatformId,
                    Hex(pn.lookIdHead), Hex(pn.lookIdTorso), Hex(pn.lookIdLegs), true);
            }
            catch { }

            var dt = 0.25f;
            var delta = pos - t.LastPos;
            var horiz = new Vector3(delta.x, 0, delta.z).magnitude / dt;
            t.Speed = horiz;
            t.LastPos = pos;

            if (hosting && !pn.isLocalPlayer)
            {
                // catches people who were already in when the ban was added
                string addr = null; try { addr = pn.connectionToClient?.address; } catch { }
                if (OrbState.IsBanned(id) || OrbState.IsBannedAddress(addr))
                {
                    OrbState.AddEvent("autokick", id, name, "banned while in session");
                    OrbState.BanAttachAddress(id, addr);
                    Guard.Kick(pn);
                    continue;
                }

                // no raycasts here, they blow the raycast budget in a full lobby.
                // just look at how long someone keeps going up / going fast.
                var vy = delta.y / dt;
                t.AirSec = vy > 1.5f ? t.AirSec + dt : 0;
                t.SpeedSec = horiz > Plugin.FlyMaxSpeed.Value ? t.SpeedSec + dt : 0;

                bool fly = t.AirSec > Plugin.FlyMaxAirSeconds.Value;
                bool speed = t.SpeedSec > 1.5f;
                if ((fly || speed) && Time.unscaledTime - t.LastFlag > 60f)
                {
                    t.LastFlag = Time.unscaledTime;
                    var what = fly ? $"climbing steadily for {t.AirSec:0.0}s" : $"sustained {horiz:0.0} m/s";
                    OrbState.AddAlert(fly ? "fly" : "speed", id, name, what + $" at {pos.x:0},{pos.y:0},{pos.z:0}");
                    if (Plugin.FlyAutoKick.Value)
                    {
                        OrbState.AddEvent("autokick", id, name, "anticheat: " + what);
                        Guard.Kick(pn);
                    }
                }
            }
        }

        var gone = new List<string>();
        foreach (var kv in _tracks)
            if (!kv.Value.Seen)
            {
                OrbState.AddEvent("leave", kv.Key, kv.Value.Name);
                // hosting is false by the time a session ends so this stays quiet on shutdown
                if (hosting) Chime.Play(false);
                OrbState.RosterOffline(kv.Key);
                if (_tags.TryGetValue(kv.Key, out var go) && go != null) Destroy(go);
                _tags.Remove(kv.Key);
                gone.Add(kv.Key);
            }
        foreach (var id in gone) _tracks.Remove(id);
    }

    // ---------------- snapshot for the web UI ----------------

    private void Snapshot(bool hosting)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"hosting\":").Append(hosting ? "true" : "false")
          .Append(",\"session\":").Append(OrbState.J(OrbState.SessionName))
          .Append(",\"nametags\":").Append(NametagsOn ? "true" : "false")
          .Append(",\"chime\":").Append(Chime.Enabled ? "true" : "false")
          .Append(",\"code\":").Append(OrbState.J(hosting ? LobbyCode() : ""))
          .Append(",\"password\":").Append(OrbState.J(hosting ? SafeStr(() => Auth()?.password) : ""))
          .Append(",\"players\":[");
        bool first = true;
        foreach (var pc in Players())
        {
            var pn = pc.playerNetworking;
            var id = Patches.Pid(pn) ?? "";
            var pos = pc.transform.position;
            _tracks.TryGetValue(id, out var t);
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"id\":").Append(OrbState.J(id))
              .Append(",\"name\":").Append(OrbState.J(Patches.Display(pn)))
              .Append(",\"username\":").Append(OrbState.J(SafeStr(() => pn.username)))
              .Append(",\"modName\":").Append(OrbState.J(SafeStr(() => pn.moderationNameSanitized)))
              .Append(",\"platformId\":\"").Append(pn.userPlatformId).Append('"')
              .Append(",\"platform\":").Append(OrbState.J(OrbState.Platform(id)))
              .Append(",\"local\":").Append(pn.isLocalPlayer ? "true" : "false")
              .Append(",\"isHost\":").Append(pn.isHost ? "true" : "false")
              .Append(",\"muted\":").Append(pn.isMuted ? "true" : "false")
              .Append(",\"colors\":[\"").Append(Hex(pn.lookIdHead)).Append("\",\"").Append(Hex(pn.lookIdTorso)).Append("\",\"").Append(Hex(pn.lookIdLegs)).Append("\"]")
              .Append(",\"pos\":[").Append((int)pos.x).Append(',').Append((int)pos.y).Append(',').Append((int)pos.z).Append(']')
              .Append(",\"speed\":").Append((t?.Speed ?? 0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"air\":").Append((t?.AirSec ?? 0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"banned\":").Append(OrbState.IsBanned(id) ? "true" : "false")
              .Append(",\"addr\":").Append(OrbState.J(SafeStr(() => pn.isLocalPlayer ? "" : pn.connectionToClient?.address)))
              .Append('}');
        }
        sb.Append("]}");
        OrbState.SnapshotJson = sb.ToString();
    }

    private static string Hex(int lookId) => LookHex[((lookId % 24) + 24) % 24];

    private static string SafeStr(Func<string> get) { try { return get() ?? ""; } catch { return ""; } }

    // ---------------- commands (run on main thread via queue) ----------------

    internal static void Cmd(string action, string id, string key, int val, string text = null)
    {
        OrbState.MainQueue.Enqueue(() => Run(action, id, key, val, text));
    }

    private static void Run(string action, string id, string key, int val, string text)
    {
        var target = id != null ? ById(id) : null;
        var tn = target != null ? target.playerNetworking : null;

        switch (action)
        {
            case "kick" when tn != null:
                OrbState.AddEvent("kick", id, Patches.Display(tn));
                Guard.Kick(tn);
                break;

            case "ban" when tn != null:
            {
                // don't kick unless the ban actually saved, otherwise they just rejoin.
                // the transport address goes on the record so a new identifier does not dodge it
                string addr = null; try { addr = tn.connectionToClient?.address; } catch { }
                if (OrbState.BanAdd(id, Patches.Display(tn), tn.userPlatformId, addr))
                    Guard.Kick(tn);
                break;
            }
            case "ban": // offline ban
                OrbState.BanAdd(id, OrbState.RosterName(id) ?? "(offline ban)", 0, OrbState.LastAddressFor(id));
                break;
            case "banaddr" when !string.IsNullOrEmpty(key):
                Guard.BanAddress(key, string.IsNullOrEmpty(text) ? "(address ban)" : text, "manual address ban");
                break;
            case "banimport":
            {
                var r = OrbState.BansImport(text);
                OrbState.AddEvent("banimport", null, "host", $"import: {r.added} added, {r.skipped} skipped, {r.bad} bad");
                break;
            }
            case "eoslookup":
            {
                var a = key; if (string.IsNullOrEmpty(a) && tn != null) { try { a = tn.connectionToClient?.address; } catch { } }
                if (!string.IsNullOrEmpty(a)) Guard.Eos.Lookup(a, tn != null ? id : null);
                break;
            }
            case "unban":
                OrbState.BanRemove(id);
                break;

            case "signset" when key != null && uint.TryParse(key, out var signNet):
            {
                var found = FindSign(signNet);
                if (found != null)
                {
                    var me = Local();
                    var hostId = me != null ? me.playerNetworking.identifier : "";   // raw EOS id, what the game records as author
                    found.ServerSetText(text ?? "", hostId);
                    // editing a locked sign moves the lock
                    if (OrbState.LockedText(signNet) != null) OrbState.LockSign(signNet, found.gameObject.name, text ?? "");
                    OrbState.AddEvent("signset", null, "host",
                        string.IsNullOrEmpty(text) ? $"erased sign {signNet}" : $"set sign {signNet} to \"{text}\"");
                }
                else OrbState.AddEvent("signset", null, "host", $"FAILED: sign netId {signNet} not found");
                break;
            }

            case "signlock" when key != null && uint.TryParse(key, out var lockNet):
            {
                var found = FindSign(lockNet);
                if (found == null) { OrbState.AddEvent("signlock", null, "host", $"FAILED: sign netId {lockNet} not found"); break; }
                string current = null;
                try { current = found.networkedText; } catch { }
                var lockText = text ?? current ?? "";
                var me = Local();
                var hostId = me != null ? me.playerNetworking.identifier : "";   // raw EOS id, what the game records as author
                // push the locked text if the sign doesn't already show it
                if (lockText != current) found.ServerSetText(lockText, hostId);
                string lkey = "sign"; try { lkey = found.gameObject.name; } catch { }
                OrbState.LockSign(lockNet, lkey, lockText);
                OrbState.AddEvent("signlock", null, "host", $"locked sign {lockNet} ({lkey}) as \"{lockText}\"");
                break;
            }

            case "signunlock" when key != null && uint.TryParse(key, out var unlockNet):
                if (OrbState.UnlockSign(unlockNet)) OrbState.AddEvent("signunlock", null, "host", $"unlocked sign {unlockNet}");
                break;

            case "setpassword":
            {
                var auth = Auth();
                if (auth == null) { OrbState.AddEvent("password", null, "host", "FAILED: no authenticator (not hosting?)"); break; }
                auth.password = text ?? "";
                OrbState.AddEvent("password", null, "host", string.IsNullOrEmpty(text) ? "password removed" : "password changed");
                break;
            }

            case "chime":
                Chime.SetEnabled(val != 0);
                OrbState.AddEvent("chime", null, "host", val != 0 ? "join/leave chime on" : "join/leave chime off");
                break;

            case "nametags":
                NametagsOn = val != 0;
                break;

            case "propreset" when !string.IsNullOrEmpty(key):
                Props.Reset(key, val);
                break;
            case "posebaseline":
                Props.RecapturePoseBaseline();
                break;

            default:
                if (!Scripting.TryRun(action, id, key, val, text))
                    OrbState.AddEvent("cmd", null, "host", $"unknown action \"{action}\"");
                break;
        }
    }

    // ---------------- nametags (local-only) ----------------

    private void UpdateNametags()
    {
        if (!NametagsOn)
        {
            if (_tags.Count > 0)
            {
                foreach (var go in _tags.Values) if (go != null) Destroy(go);
                _tags.Clear();
            }
            return;
        }
        var cam = Camera.main;
        foreach (var pc in Players())
        {
            var pn = pc.playerNetworking;
            if (pn.isLocalPlayer) continue;
            var id = Patches.Pid(pn);
            if (string.IsNullOrEmpty(id)) continue;
            if (!_tags.TryGetValue(id, out var go) || go == null)
            {
                go = new GameObject("OrbTag_" + id);
                go.hideFlags = HideFlags.HideAndDontSave;
                var tm = go.AddComponent<TextMesh>();
                tm.fontSize = 48;
                tm.characterSize = 0.022f;
                tm.anchor = TextAnchor.LowerCenter;
                tm.alignment = TextAlignment.Center;
                _tags[id] = go;
            }
            var text = go.GetComponent<TextMesh>();
            text.text = Patches.Display(pn);
            if (ColorUtility.TryParseHtmlString(Hex(pn.lookIdHead), out var col)) text.color = col;
            go.transform.position = pc.transform.position + Vector3.up * 1.75f;
            if (cam != null)
                go.transform.rotation = Quaternion.LookRotation(go.transform.position - cam.transform.position);
        }
    }
}
