using System;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BigOrb;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "bigorb";
    public const string Name = "Big Orb";
    public const string Version = "1.2.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<int> Port;
    internal static ConfigEntry<bool> AutoOpen;
    internal static ConfigEntry<float> FlyMaxSpeed;
    internal static ConfigEntry<float> FlyMaxAirSeconds;
    internal static ConfigEntry<bool> FlyAutoKick;
    internal static ConfigEntry<bool> ChimeEnabled;
    internal static ConfigEntry<float> ChimeVolume;
    internal static ConfigEntry<bool> GuardAutoBan;
    internal static ConfigEntry<bool> GuardBanAnonymous;
    internal static ConfigEntry<int> GuardVoiceLimit;
    internal static ConfigEntry<bool> GuardChatFilter;

    // random per launch, embedded in the page and required on api calls so other
    // sites in the browser can't poke the api
    internal static readonly string Token = "945d" + Convert.ToHexString(RandomNumberGenerator.GetBytes(14)).ToLowerInvariant();

    public override void Load()
    {
        Logger = Log;
        Port = Config.Bind("web", "port", 7845, "Dashboard port (localhost only)");
        AutoOpen = Config.Bind("web", "autoOpen", true, "Open the dashboard in your browser when you start hosting");
        FlyMaxSpeed = Config.Bind("anticheat", "maxSpeed", 16f, "Sustained horizontal m/s before a player is flagged");
        FlyMaxAirSeconds = Config.Bind("anticheat", "maxAirSeconds", 6f, "Seconds of steady climbing before a player is flagged");
        FlyAutoKick = Config.Bind("anticheat", "autoKick", false, "Automatically kick flagged fly/speed cheaters (off = report only)");

        ChimeEnabled = Config.Bind("chime", "enabled", true, "Play a chime on the host's machine when a player joins or leaves");
        ChimeVolume = Config.Bind("chime", "volume", 0.5f, "Chime volume, 0-1 (independent of the game's own volume)");

        GuardAutoBan = Config.Bind("guard", "autoBan", true, "Address-ban a connection that is proven to be a modded client (spoofed identity, voice flood). Off = alert and disconnect only");
        GuardBanAnonymous = Config.Bind("guard", "banAnonymousLogins", true, "Treat an EOS account with no linked Steam/PSN/Xbox account (anonymous device-id login) as a modded client");
        GuardVoiceLimit = Config.Bind("guard", "voicePacketsPerSecond", 120, "Voice packets per second a connection may send before the rest are dropped (a talking player sends ~50). 0 = off");
        GuardChatFilter = Config.Bind("guard", "dropFakeSystemChat", true, "Drop guest chat that imitates a system message (\"host has been removed\" etc.)");

        OrbState.Init();

        ClassInjector.RegisterTypeInIl2Cpp<OrbBehaviour>();
        var go = new GameObject("BigOrbHolder");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<OrbBehaviour>();

        var harmony = new Harmony(Guid);
        Patches.PatchAllSafe(harmony);
        Guard.Voice.Patch(harmony);
        Guard.Eos.Load();
        Scripting.Load(harmony);

        WebServer.Start(Port.Value);
        Logger.LogInfo($"Big Orb loaded, dashboard at http://localhost:{Port.Value}/");
    }
}
