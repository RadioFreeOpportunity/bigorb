using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;

namespace BigOrb;

internal static class Patches
{
    // il2cpp inlines a lot, and a patch on an inlined method just never fires. so each
    // thing is hooked in a few places and deduped. hooks log once when they first fire.

    private static readonly HashSet<string> FiredHooks = new();
    private static string _lastChatKey; private static DateTime _lastChatTime;
    private static string _lastSignKey; private static DateTime _lastSignTime;

    internal static void MarkFired(string hook)
    {
        if (FiredHooks.Add(hook)) Plugin.Logger.LogInfo($"hook fired for the first time: {hook}");
    }

    // patch one class at a time so a bad signature only kills that hook
    internal static void PatchAllSafe(Harmony harmony)
    {
        foreach (var t in new[]
                 {
                     typeof(IdentifierPatch), typeof(Guard.AuthPatch), typeof(Guard.FakeChatPatch),
                     typeof(ChatCmdPatch), typeof(ChatRpcPatch), typeof(ChatReceivePatch), typeof(ChatDisplayPatch),
                     typeof(SignCmdPatch), typeof(SignSyncPatch), typeof(SignSavePatch), typeof(SignLockPatch),
                 })
        {
            try
            {
                harmony.CreateClassProcessor(t).Patch();
                Plugin.Logger.LogInfo($"patched {t.Name}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"FAILED to patch {t.Name}: {e.Message} (that hook is disabled, the rest still runs)");
            }
        }
    }

    internal static string Display(PlayerNetworking pn)
    {
        try
        {
            // moderationName = platform display name, username = internal slug
            var n = pn.moderationNameSanitized;
            if (string.IsNullOrEmpty(n)) n = pn.moderationName;
            if (string.IsNullOrEmpty(n)) n = pn.username;
            return string.IsNullOrEmpty(n) ? "?" : n;
        }
        catch { return "?"; }
    }

    internal static string NameForIdentifier(string id)
    {
        try
        {
            var all = PlayerCharacter.allPlayerCharacters;
            if (all == null || id == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc != null && pc.playerNetworking != null && Pid(pc.playerNetworking) == id)
                    return Display(pc.playerNetworking);
            }
        }
        catch { }
        return null;
    }

    // ---------------- chat (4 hook points, deduped) ----------------

    private static void LogChat(string hook, string id, string name, string msg)
    {
        MarkFired(hook);
        if (string.IsNullOrEmpty(msg)) return;
        var key = id + "" + msg;
        if (key == _lastChatKey && (DateTime.Now - _lastChatTime).TotalSeconds < 3) return;
        _lastChatKey = key; _lastChatTime = DateTime.Now;
        OrbState.AddChat(id, name, msg);
    }

    [HarmonyPatch(typeof(PlayerNetworking), nameof(PlayerNetworking.UserCode_CmdSendTextChatMessage__String))]
    internal static class ChatCmdPatch
    {
        private static void Postfix(PlayerNetworking __instance, string __0)
        {
            try { LogChat("chat/cmd", Pid(__instance), Display(__instance), __0); } catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerNetworking), nameof(PlayerNetworking.RpcTextChatMessage))]
    internal static class ChatRpcPatch
    {
        private static void Postfix(PlayerNetworking __instance, string __0)
        {
            try { LogChat("chat/rpc", Pid(__instance), Display(__instance), __0); } catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerTexter), nameof(PlayerTexter.ReceieveMessage))]
    internal static class ChatReceivePatch
    {
        private static void Postfix(PlayerTexter __instance, string __0)
        {
            try
            {
                var pn = __instance.playerCharacter?.playerNetworking;
                LogChat("chat/receive", Pid(pn), pn != null ? Display(pn) : "?", __0);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerTexter), nameof(PlayerTexter.DisplayMessage))]
    internal static class ChatDisplayPatch
    {
        private static void Postfix(PlayerTexter __instance, string __0)
        {
            try
            {
                var pn = __instance.playerCharacter?.playerNetworking;
                LogChat("chat/display", Pid(pn), pn != null ? Display(pn) : "?", __0);
            }
            catch { }
        }
    }

    // ---------------- signs (3 hook points, deduped) ----------------

    private static void LogSign(string hook, string key, string text, string byId, uint netId = 0)
    {
        MarkFired(hook);
        var dk = key + "" + text;
        if (dk == _lastSignKey && (DateTime.Now - _lastSignTime).TotalSeconds < 3) return;
        _lastSignKey = dk; _lastSignTime = DateTime.Now;
        var byName = NameForIdentifier(byId) ?? (byId != null ? byId : "unknown");
        OrbState.AddSign(key, text, byId, byName, netId);
    }

    // fires for whoever edits a sign (incl. us). since 1.6.0 the second arg is the sender
    // connection (null on the client), not an author string
    [HarmonyPatch(typeof(PeckEffectTextInput), nameof(PeckEffectTextInput.CmdSendNewText))]
    internal static class SignCmdPatch
    {
        private static void Postfix(PeckEffectTextInput __instance, string __0, NetworkConnectionToClient __1)
        {
            try { LogSign("sign/cmd", SignKey(__instance), __0, AuthorOf(__1), SignNetId(__instance)); } catch { }
        }
    }

    // fires on every client when synced text changes. deferred a frame because
    // authorIdentifier syncs after networkedText.
    [HarmonyPatch(typeof(PeckEffectTextInput), nameof(PeckEffectTextInput.OnChangeNetworkedText))]
    internal static class SignSyncPatch
    {
        private static void Postfix(PeckEffectTextInput __instance, string __1)
        {
            try
            {
                MarkFired("sign/sync");
                var inst = __instance;
                var text = __1;
                OrbState.MainQueue.Enqueue(() =>
                {
                    string author = null, key = "sign";
                    try { author = PidOf(inst.authorIdentifier); key = SignKey(inst); } catch { }
                    LogSign("sign/sync", key, text, author, SignNetId(inst));
                });
            }
            catch { }
        }
    }

    // host commands arrive on the local connection
    internal static bool IsHostConnection(NetworkConnectionToClient conn)
    {
        try { return conn == null || conn.TryCast<LocalConnectionToClient>() != null || conn.connectionId == 0; }
        catch { return false; }
    }

    // stable player id: the platform id (steam / console). 1.6.0 moved it from identifier
    // (now the EOS id) to userPlatformId; bans and logs stay keyed on it. falls back to identifier.
    internal static string Pid(PlayerNetworking pn)
    {
        if (pn == null) return null;
        string s = null;
        try { s = pn.userPlatformIdString; } catch { }
        if (string.IsNullOrEmpty(s) || s == "0") { s = null; try { var u = pn.userPlatformId; if (u != 0) s = u.ToString(); } catch { } }
        if (string.IsNullOrEmpty(s)) { try { s = pn.identifier; } catch { } }
        return s;
    }

    // raw EOS id (a sign's authorIdentifier) -> platform id, when that player is here
    internal static string PidOf(string rawId)
    {
        if (string.IsNullOrEmpty(rawId)) return rawId;
        try { var pc = OrbBehaviour.ById(rawId); if (pc != null) return Pid(pc.playerNetworking) ?? rawId; } catch { }
        return rawId;
    }

    private static PlayerNetworking PlayerOf(NetworkConnectionToClient conn)
    {
        try { var id = conn?.identity; return id != null ? id.GetComponent<PlayerNetworking>() : null; }
        catch { return null; }
    }

    // platform id of whoever sent a command; null before their player spawns
    internal static string AuthorOf(NetworkConnectionToClient conn) => Pid(PlayerOf(conn));

    // raw EOS id of the sender: the game records it as sign author, and its kick cleanup matches on it
    internal static string RawAuthorOf(NetworkConnectionToClient conn)
    {
        var pn = PlayerOf(conn);
        try { return pn != null ? pn.identifier : null; } catch { return null; }
    }

    private static string SignKey(PeckEffectTextInput input)
    {
        try { return input.gameObject.name; } catch { return "sign"; }
    }

    private static uint SignNetId(PeckEffectTextInput input)
    {
        try { return input.netId; } catch { return 0; }
    }

    // fallback, host side, no author
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SetStringValue))]
    internal static class SignSavePatch
    {
        private static void Postfix(string __0, string __1)
        {
            try { LogSign("sign/save", __0, __1, null); } catch { }
        }
    }

    // ---------------- sign locks ----------------
    // the static InvokeUserCode_* handlers are registered as delegates so they can't be
    // inlined. for a locked sign, eat the incoming edit and replay with the locked text.

    [HarmonyPatch(typeof(PeckEffectTextInput), nameof(PeckEffectTextInput.InvokeUserCode_CmdSendNewText__String__NetworkConnectionToClient))]
    internal static class SignLockPatch
    {
        private static bool Prefix(NetworkBehaviour __0, NetworkReader __1, NetworkConnectionToClient __2)
        {
            PeckEffectTextInput sign;
            uint net;
            string locked;
            try
            {
                sign = __0.TryCast<PeckEffectTextInput>();
                if (sign == null) return true;
                net = SignNetId(sign);
                locked = net != 0 ? OrbState.LockedText(net) : null;
                if (locked == null) return true;
            }
            catch { return true; }

            // reader is consumed from here on, original must not run
            try
            {
                var text = NetworkReaderExtensions.ReadString(__1);
                // only the text is on the wire since 1.6.0; the author comes from the sender connection
                var author = AuthorOf(__2);
                var raw = RawAuthorOf(__2) ?? author;
                if (IsHostConnection(__2))
                {
                    // host edit moves the lock
                    OrbState.LockSign(net, SignKey(sign), text);
                    sign.ServerSetText(text, raw);
                }
                else
                {
                    MarkFired("lock/sign-blocked");
                    var who = NameForIdentifier(author) ?? author ?? "unknown";
                    OrbState.AddEvent("signblocked", author, who,
                        $"tried to edit locked sign {net} ({SignKey(sign)}) with \"{text}\"");
                    sign.ServerSetText(locked, raw);
                }
            }
            catch (Exception e) { Plugin.Logger.LogError("sign lock: " + e.Message); }
            return false;
        }
    }

    // ---------------- ban gate ----------------

    [HarmonyPatch(typeof(PlayerNetworking), nameof(PlayerNetworking.OnSetIdentifier))]
    internal static class IdentifierPatch
    {
        private static void Postfix(PlayerNetworking __instance, string __1)
        {
            try
            {
                MarkFired("identifier");
                if (!NetworkServer.active || string.IsNullOrEmpty(__1)) return;
                if (OrbState.IsBanned(__1) || OrbState.IsBanned(Pid(__instance)))
                {
                    OrbState.AddEvent("autokick", Pid(__instance), Display(__instance), "banned identifier tried to join");
                    Guard.Kick(__instance);
                }
            }
            catch (Exception e) { Plugin.Logger.LogError("identifier gate: " + e.Message); }
        }
    }
}
