using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mirror;
using Mirror.Authenticators;
using UnityEngine;

namespace BigOrb;

// Defence against modded clients. Everything a client sends about itself (identifier, name,
// platform id, epic id) can be typed in by a mod; the only thing it cannot change is the
// transport address (the EOS ProductUserId the connection was authenticated with). So:
//  - bans carry the address and are enforced at the auth handshake, before spawn
//  - kicks are enforced server-side (the client-side kick RPC can be ignored)
//  - every new address is looked up on Epic: a Steam-linked account claiming another Steam ID
//    is a proven spoof; an address with no linked account at all is an anonymous device-id
//    login, which no real Steam/PSN/Xbox client ever does
//  - the syncvars are checked against the transport once they land
//  - voice packets are rate-limited per connection at the relay
//  - guest chat that imitates a system message is dropped
internal static class Guard
{
    private const float KickGrace = 1f;
    private static readonly Dictionary<int, float> PendingDisconnect = new();
    private static readonly Dictionary<string, HashSet<string>> IdsByAddress = new();   // this session
    private static readonly Dictionary<uint, string> Judged = new();                     // netId → epic|plat already judged
    private static readonly Dictionary<string, int> Refused = new();

    internal static bool AutoBan => Plugin.GuardAutoBan.Value;

    // ---------------- kick / ban actions ----------------

    internal static void Kick(PlayerNetworking pn, string reason = null)
    {
        if (pn == null) return;
        NetworkConnectionToClient conn = null; try { conn = pn.connectionToClient; } catch { }
        if (conn == null) return;
        lock (PendingDisconnect) { if (PendingDisconnect.ContainsKey(conn.connectionId)) return; PendingDisconnect[conn.connectionId] = Time.unscaledTime + KickGrace; }
        // the game's own kick also clears the signs that player wrote; RPCKickUser alone does not
        try { PeckEffectTextInput.ClearTextWrittenByAuthor(pn.identifier); } catch { }
        try { pn.RPCKickUser(conn); } catch { }
    }

    internal static void StepKicks()
    {
        List<int> due = null;
        lock (PendingDisconnect) foreach (var kv in PendingDisconnect) if (Time.unscaledTime >= kv.Value) (due ??= new()).Add(kv.Key);
        if (due == null) return;
        foreach (var cid in due)
        {
            lock (PendingDisconnect) PendingDisconnect.Remove(cid);
            try { if (NetworkServer.connections.TryGetValue(cid, out var c) && c != null) c.Disconnect(); } catch { }
        }
    }

    internal static void BanAddress(string address, string name, string why, string id = null)
    {
        if (string.IsNullOrEmpty(address) || address == "localhost") return;
        if (!OrbState.IsBannedAddress(address)) OrbState.BanAdd(id, name, 0, address);
        OrbState.AddEvent("autoban", id, name, why);
        foreach (var kv in NetworkServer.connections)
        {
            var c = kv.Value; if (c == null || c.connectionId == 0) continue;
            string a = null; try { a = c.address; } catch { }
            if (a == address) { try { c.Disconnect(); } catch { } }
        }
    }

    private static void Act(string kind, string id, string name, string detail, string address, NetworkConnectionToClient conn)
    {
        OrbState.AddAlert(kind, id, name, detail);
        if (AutoBan) BanAddress(address, name ?? id ?? "?", kind + ": " + detail, id);
        else { try { conn?.Disconnect(); } catch { } }
    }

    // ---------------- auth handshake ----------------

    private static string Base(string id) => id == null ? null : Regex.Replace(id, @"-\d+$", "");
    private static bool IsSteam(string id) => id != null && id.Length == 17 && id.StartsWith("7656119") && id.All(char.IsDigit);
    private static bool IsNumeric(string id) => !string.IsNullOrEmpty(id) && id.Length >= 15 && id.All(char.IsDigit);
    private static bool IsEosAddr(string a) => a != null && a.Length == 32 && a.All(Uri.IsHexDigit);

    [HarmonyPatch(typeof(HouseAuthenticator), nameof(HouseAuthenticator.OnInitialAuthRequestMessage))]
    internal static class AuthPatch
    {
        private static void Postfix(NetworkConnectionToClient __0, HouseAuthenticator.InitialialAuthRequestMessage __1)
        {
            try
            {
                Patches.MarkFired("auth");
                if (__0 == null) return;
                string addr = null, claimed = null, ver = null;
                try { addr = __0.address; } catch { }
                try { claimed = __1?.platformUserId; } catch { }
                try { ver = __1?.versionNumber; } catch { }
                OrbState.AuthSeen(__0.connectionId, addr, claimed, ver);
                var who = OrbState.RosterName(claimed) ?? claimed ?? "?";
                OrbState.AddEvent("auth", claimed, who, $"conn {__0.connectionId} addr {addr ?? "?"} claims {claimed ?? "?"} v{ver ?? "?"}");

                if (OrbState.IsBannedAddress(addr))
                {
                    OrbState.AddEvent("autokick", claimed, who, $"banned address {addr} tried to join as {claimed}");
                    __0.Disconnect(); return;
                }
                if (OrbState.IsBanned(claimed))
                {
                    OrbState.AddEvent("autokick", claimed, who, $"banned identifier tried to join from {addr ?? "?"}");
                    OrbState.BanAttachAddress(claimed, addr);
                    __0.Disconnect(); return;
                }
                if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(claimed) || addr == "localhost") return;

                // steam transport: address and identifier are both steam ids and must agree
                if (IsSteam(addr) && IsSteam(claimed) && addr != claimed)
                { Act("idspoof", claimed, who, $"claims SteamID {claimed} but the connection is {addr}", addr, __0); return; }

                // the same address claiming several identifiers this session
                var b = Base(claimed);
                lock (IdsByAddress)
                {
                    if (!IdsByAddress.TryGetValue(addr, out var set)) IdsByAddress[addr] = set = new HashSet<string>();
                    // an id equal to the address is the EOS id, not a second identifier
                    if (b != addr) set.Add(b);
                    if (set.Count >= 2)
                    {
                        var detail = $"address {addr} has claimed {set.Count} identifiers: {string.Join(", ", set)}";
                        if (set.Count >= 3) { Act("idrotate", claimed, who, detail, addr, __0); return; }
                        OrbState.AddAlert("idrotate", claimed, who, detail);
                    }
                }

                Eos.Lookup(addr, claimed);
            }
            catch (Exception e) { Plugin.Logger.LogError("auth gate: " + e.Message); }
        }
    }

    // ---------------- syncvars, once they land ----------------

    internal static void Tick()
    {
        StepKicks();
        Voice.Tick();
        foreach (var pc in OrbBehaviour.Players())
        {
            try
            {
                var pn = pc.playerNetworking; if (pn == null || pn.isLocalPlayer) continue;
                string id = null, addr = null, epic = null; ulong plat = 0;
                try { id = Patches.Pid(pn); } catch { } try { addr = pn.connectionToClient?.address; } catch { }
                try { epic = pn.epicUserId; } catch { } try { plat = pn.userPlatformId; } catch { }
                if (OrbState.IsUnsetIdentifier(id) || addr == null) continue;
                var sig = (epic ?? "") + "|" + plat;
                if (Judged.TryGetValue(pn.netId, out var prev) && prev == sig) continue;
                Judged[pn.netId] = sig;
                var who = Patches.Display(pn);
                if (IsEosAddr(addr) && !string.IsNullOrEmpty(epic) && !string.Equals(epic, addr, StringComparison.OrdinalIgnoreCase))
                    Act("epicspoof", id, who, $"connection is {addr} but the client reports epic id {epic}", addr, pn.connectionToClient);
                else if (plat != 0 && IsNumeric(Base(id)) && plat.ToString() != Base(id))
                {
                    string victim = null;
                    foreach (var o in OrbBehaviour.Players()) { var on = o.playerNetworking; if (on != null && on.Pointer != pn.Pointer && Base(Patches.Pid(on)) == plat.ToString()) { victim = Patches.Display(on); break; } }
                    Act("platspoof", id, who, $"claims identifier {id} but platform id is {plat}" + (victim != null ? $" ({victim}'s, who is connected)" : ""), addr, pn.connectionToClient);
                }
            }
            catch { }
        }
        if (Judged.Count > 64) { var live = new HashSet<uint>(); foreach (var pc in OrbBehaviour.Players()) { try { live.Add(pc.playerNetworking.netId); } catch { } } foreach (var k in Judged.Keys.Where(k => !live.Contains(k)).ToList()) Judged.Remove(k); }
    }

    // ---------------- EOS: the account behind an address ----------------

    internal static class Eos
    {
        private const string Dll = "EOSSDK-Win64-Shipping";
        [StructLayout(LayoutKind.Sequential)] private struct QueryOptions { public int ApiVersion; public IntPtr LocalUserId; public IntPtr AccountIdTypeDeprecated; public IntPtr ProductUserIds; public uint Count; }
        [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public int ResultCode; public IntPtr ClientData; public IntPtr LocalUserId; }
        [StructLayout(LayoutKind.Sequential)] private struct CountOptions { public int ApiVersion; public IntPtr TargetUserId; }
        [StructLayout(LayoutKind.Sequential)] private struct CopyOptions { public int ApiVersion; public IntPtr TargetUserId; public uint Index; }
        [StructLayout(LayoutKind.Sequential)] private struct AccountInfo { public int ApiVersion; public IntPtr ProductUserId; public IntPtr DisplayName; public IntPtr AccountId; public int AccountIdType; public long LastLoginTime; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OnQuery(IntPtr info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr EOS_ProductUserId_FromString(string s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void EOS_Connect_QueryProductUserIdMappings(IntPtr h, ref QueryOptions o, IntPtr clientData, OnQuery cb);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern uint EOS_Connect_GetProductUserExternalAccountCount(IntPtr h, ref CountOptions o);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int EOS_Connect_CopyProductUserExternalAccountByIndex(IntPtr h, ref CopyOptions o, out IntPtr info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void EOS_Connect_ExternalAccountInfo_Release(IntPtr info);
        private static OnQuery _cb;
        private static readonly string[] Types = { "epic", "steam", "psn", "xbl", "discord", "gog", "nintendo", "uplay", "openid", "apple", "google", "oculus", "itchio", "amazon", "viveport" };

        internal class Mapping { public string Type, AccountId, DisplayName, When; public List<string> All = new(); }
        internal static readonly Dictionary<string, Mapping> Map = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ClaimedBy = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long, string> Inflight = new();
        private static long _seq;
        private static string File => Path.Combine(OrbState.DataDir, "eosmap.tsv");

        internal static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(File)) return;
                foreach (var l in System.IO.File.ReadAllLines(File))
                {
                    var p = l.Split('\t'); if (p.Length < 5) continue;
                    var m = new Mapping { Type = p[1], AccountId = p[2], DisplayName = p[3], When = p[4] };
                    if (p.Length > 5) m.All.AddRange(p[5].Split('|'));
                    Map[p[0]] = m;
                }
            }
            catch { }
        }

        internal static void Lookup(string addr, string claimed)
        {
            if (!IsEosAddr(addr)) return;
            ClaimedBy[addr] = claimed;
            if (Map.TryGetValue(addr, out var known)) { Compare(addr, known, claimed); return; }
            if (Pending.Contains(addr)) return;
            try
            {
                var mgr = PlayEveryWare.EpicOnlineServices.EOSManager.Instance;
                var connect = mgr?.GetEOSConnectInterface(); var me = mgr?.GetProductUserId();
                if (connect == null || me == null) return;
                var target = EOS_ProductUserId_FromString(addr); if (target == IntPtr.Zero) return;
                var ids = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(ids, target);
                long seq = ++_seq; lock (Inflight) Inflight[seq] = addr;
                _cb ??= OnQueried;
                var o = new QueryOptions { ApiVersion = 2, LocalUserId = me.InnerHandle, ProductUserIds = ids, Count = 1 };
                Pending.Add(addr);
                EOS_Connect_QueryProductUserIdMappings(connect.InnerHandle, ref o, (IntPtr)seq, _cb);
            }
            catch (Exception e) { Plugin.Logger.LogError("eos lookup: " + e.Message); Pending.Remove(addr); }
        }

        private static void OnQueried(IntPtr infoPtr)
        {
            try
            {
                var info = Marshal.PtrToStructure<QueryInfo>(infoPtr);
                string addr; lock (Inflight) { Inflight.TryGetValue((long)info.ClientData, out addr); Inflight.Remove((long)info.ClientData); }
                if (addr == null) return;
                Pending.Remove(addr);
                if (info.ResultCode != 0) { OrbState.AddEvent("eos", null, "host", $"lookup {addr} → result {info.ResultCode}"); return; }
                Read(addr);
            }
            catch (Exception e) { Plugin.Logger.LogError("eos callback: " + e.Message); }
        }

        private static void Read(string addr)
        {
            var connect = PlayEveryWare.EpicOnlineServices.EOSManager.Instance?.GetEOSConnectInterface(); if (connect == null) return;
            var target = EOS_ProductUserId_FromString(addr);
            var co = new CountOptions { ApiVersion = 1, TargetUserId = target };
            uint n = EOS_Connect_GetProductUserExternalAccountCount(connect.InnerHandle, ref co);
            var m = new Mapping { When = OrbState.Now() };
            for (uint i = 0; i < n; i++)
            {
                var o = new CopyOptions { ApiVersion = 1, TargetUserId = target, Index = i };
                if (EOS_Connect_CopyProductUserExternalAccountByIndex(connect.InnerHandle, ref o, out var p) != 0 || p == IntPtr.Zero) continue;
                try
                {
                    var a = Marshal.PtrToStructure<AccountInfo>(p);
                    var type = a.AccountIdType >= 0 && a.AccountIdType < Types.Length ? Types[a.AccountIdType] : "type" + a.AccountIdType;
                    var acct = a.AccountId != IntPtr.Zero ? Marshal.PtrToStringUTF8(a.AccountId) : "";
                    var name = a.DisplayName != IntPtr.Zero ? Marshal.PtrToStringUTF8(a.DisplayName) : "";
                    m.All.Add($"{type}:{acct}:{name}");
                    if (m.Type == null || (m.Type == "epic" && type != "epic")) { m.Type = type; m.AccountId = acct; m.DisplayName = name; }
                }
                finally { EOS_Connect_ExternalAccountInfo_Release(p); }
            }
            ClaimedBy.TryGetValue(addr, out var claimed);
            if (n == 0) m.Type = "none";
            Map[addr] = m; Save(addr, m);
            OrbState.AddEvent("eos", claimed, OrbState.RosterName(claimed) ?? claimed ?? "?", n == 0 ? $"{addr} has no linked account" : $"{addr} → {m.Type} {m.AccountId} \"{m.DisplayName}\"");
            if (n == 0)
            {
                var who = OrbState.RosterName(claimed) ?? claimed ?? "?";
                var detail = $"{addr} has no external account on Epic: anonymous device-id login, not a real Steam/PSN/Xbox client (claimed {claimed ?? "?"})";
                OrbState.AddAlert("eosanon", claimed, who, detail);
                if (Plugin.GuardBanAnonymous.Value && AutoBan) BanAddress(addr, who, "eosanon: " + detail, claimed);
                return;
            }
            Compare(addr, m, claimed);
        }

        private static void Compare(string addr, Mapping m, string claimed)
        {
            if (string.IsNullOrEmpty(claimed) || m == null || m.Type == "none") return;
            var b = Base(claimed);
            bool anyId = false, match = false;
            foreach (var a in m.All) { var p = a.Split(':'); if (p.Length > 1 && p[1].Length > 0) { anyId = true; if (p[1] == b) match = true; } }
            if (!anyId || match) return;
            var who = OrbState.RosterName(claimed) ?? claimed;
            var detail = $"Epic says {addr} is {m.Type} account {m.AccountId} (\"{m.DisplayName}\") but the client claimed {claimed}";
            OrbState.AddAlert("eosspoof", claimed, who, detail);
            if (AutoBan) BanAddress(addr, who, "eosspoof: " + detail, claimed);
        }

        private static void Save(string addr, Mapping m)
        {
            try
            {
                var lines = new List<string>(); if (System.IO.File.Exists(File)) lines.AddRange(System.IO.File.ReadAllLines(File));
                lines.RemoveAll(l => l.StartsWith(addr + "\t", StringComparison.OrdinalIgnoreCase));
                lines.Add($"{addr}\t{m.Type}\t{m.AccountId}\t{(m.DisplayName ?? "").Replace('\t', ' ')}\t{m.When}\t{string.Join("|", m.All).Replace('\t', ' ')}");
                System.IO.File.WriteAllLines(File, lines);
            }
            catch { }
        }

        internal static string Json()
        {
            var sb = new System.Text.StringBuilder("{");
            foreach (var kv in Map)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(OrbState.J(kv.Key)).Append(":{\"type\":").Append(OrbState.J(kv.Value.Type)).Append(",\"accountId\":").Append(OrbState.J(kv.Value.AccountId))
                  .Append(",\"displayName\":").Append(OrbState.J(kv.Value.DisplayName)).Append(",\"when\":").Append(OrbState.J(kv.Value.When)).Append('}');
            }
            return sb.Append('}').ToString();
        }
    }

    // ---------------- voice relay rate limit ----------------

    internal static class Voice
    {
        private sealed class Bucket { public int Count, LastRate, Dropped; public float Window, OverSince = -1, LastAlert = -999; }
        private static readonly Dictionary<int, Bucket> B = new();
        private const float BanAfter = 5f;

        internal static void Patch(Harmony harmony)
        {
            MethodInfo target = null;
            try
            {
                foreach (var t in typeof(PlayerNetworking).Assembly.GetTypes())
                    if (t.Name == "MirrorIgnoranceServer") { target = t.GetMethod("OnMessageReceived", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); break; }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("voice relay type scan: " + e.Message); }
            if (target == null) { Plugin.Logger.LogWarning("voice relay hook not found, voice limiter off"); return; }
            try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(Voice).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.Public))); }
            catch (Exception e) { Plugin.Logger.LogWarning("voice relay patch: " + e.Message); }
        }

        public static bool Prefix(object __0)
        {
            int limit = Plugin.GuardVoiceLimit.Value; if (limit <= 0) return true;
            try
            {
                var conn = __0 as NetworkConnection; if (conn == null) return true;
                int cid = conn.connectionId; if (cid == 0) return true;
                float now = Time.unscaledTime;
                lock (B)
                {
                    if (!B.TryGetValue(cid, out var k)) B[cid] = k = new Bucket { Window = now };
                    if (now - k.Window >= 1f) { k.LastRate = k.Count; k.Count = 0; k.Window = now; }
                    k.Count++;
                    if (k.Count > limit) { k.Dropped++; return false; }
                }
            }
            catch { }
            return true;
        }

        internal static void Tick()
        {
            int limit = Plugin.GuardVoiceLimit.Value; if (limit <= 0) return;
            float now = Time.unscaledTime;
            List<(int, Bucket)> over = null;
            lock (B)
            {
                foreach (var kv in B)
                {
                    var k = kv.Value;
                    bool isOver = k.Count > limit || (now - k.Window < 1f && k.LastRate > limit);
                    if (isOver) { if (k.OverSince < 0) k.OverSince = now; (over ??= new()).Add((kv.Key, k)); }
                    else k.OverSince = -1;
                }
                foreach (var c in B.Keys.Where(c => !NetworkServer.connections.ContainsKey(c)).ToList()) B.Remove(c);
            }
            if (over == null) return;
            foreach (var (cid, k) in over)
            {
                if (!NetworkServer.connections.TryGetValue(cid, out var conn) || conn == null) continue;
                var a = OrbState.AuthFor(cid); var id = a?.ClaimedId; var who = OrbState.RosterName(id) ?? id ?? $"conn {cid}";
                if (now - k.LastAlert > 30f) { k.LastAlert = now; OrbState.AddAlert("voiceflood", id, who, $"{Math.Max(k.Count, k.LastRate)} voice packets/s (limit {limit}), {k.Dropped} dropped"); }
                if (now - k.OverSince >= BanAfter)
                {
                    string addr = null; try { addr = conn.address; } catch { }
                    if (AutoBan) BanAddress(addr, who, $"voiceflood: {BanAfter:0}s sustained over {limit} packets/s", id);
                    try { conn.Disconnect(); } catch { }
                    k.OverSince = -1;
                }
            }
        }
    }

    // ---------------- chat that imitates the game ----------------

    private static readonly Regex FakeSystem = new(
        @"^\s*(?:\[?(?:server|system|host|admin)\]?\s*[:\-]" +
        @"|(?:the\s+)?(?:host|server|admin|you|player|[\w\-]+)\s+(?:has|have)\s+been\s+(?:removed|kicked|banned|disconnected)" +
        @"|[\w\-]+\s+(?:has|have)\s+(?:joined|left)\s+the\s+(?:game|lobby|server)" +
        @"|(?:you\s+(?:have\s+been|were)\s+)?(?:kicked|banned)\s+(?:by|from)\s+(?:the\s+)?(?:host|server)" +
        @"|(?:connection|host)\s+(?:lost|timed\s+out)|lost\s+connection\s+to\s+(?:the\s+)?host)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [HarmonyPatch(typeof(PlayerNetworking), nameof(PlayerNetworking.InvokeUserCode_CmdSendTextChatMessage__String))]
    internal static class FakeChatPatch
    {
        private static bool Prefix(NetworkBehaviour __0, NetworkReader __1)
        {
            if (!Plugin.GuardChatFilter.Value) return true;
            try
            {
                var pn = __0.TryCast<PlayerNetworking>(); if (pn == null || pn.isLocalPlayer) return true;
                int pos = __1.Position;
                string msg = null; try { msg = NetworkReaderExtensions.ReadString(__1); } catch { }
                __1.Position = pos;
                if (string.IsNullOrEmpty(msg) || !FakeSystem.IsMatch(msg)) return true;
                Patches.MarkFired("chat/fake");
                OrbState.AddAlert("fakesys", Patches.Pid(pn), Patches.Display(pn), $"chat styled as a system message, dropped: \"{msg}\"");
                return false;
            }
            catch { return true; }
        }
    }
}
