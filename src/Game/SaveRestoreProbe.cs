using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SummerCoop.Game
{
    // Explicit opt-in regression checks in a separate game/profile copy.
    public sealed class SaveRestoreProbe : MonoBehaviour
    {
        private float began, prepared;
        private bool done;
        private readonly List<string> lines = new List<string>();
        private void Update()
        {
            CoopRuntime runtime = CoopRuntime.Instance;
            if (done || runtime == null || !runtime.Active || !runtime.Session.IsHost || runtime.Joining) return;
            try
            {
                if (began == 0) began = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - began < 3) return;
                SteamTransport steam = runtime.Session.Transport as SteamTransport;
                if (steam != null && !steam.LobbyReady)
                {
                    if (Time.realtimeSinceStartup - began > 30) throw new Exception("Steam lobby did not become ready");
                    return;
                }
                if (prepared == 0)
                {
                    lines.Add("RESTORE=PASS world=" + runtime.Session.World.Id + " money=" + runtime.Session.World.Money);
                    if (steam != null) lines.Add("STEAM_LOBBY=PASS");
                    GameObject player = GameObject.Find("PLAYER"), vehicle = null;
                    foreach (ObjectBinding binding in runtime.Registry.Objects.Values)
                        if (binding.Kind == SummerCoop.Core.EntityKind.Vehicle && binding.Object != null) { vehicle = binding.Object; break; }
                    if (player == null || vehicle == null) throw new Exception("Player/vehicle unavailable for seating regression");
                    Transform transform = player.transform, parent = transform.parent;
                    Vector3 position = transform.localPosition, scale = transform.localScale; Quaternion rotation = transform.localRotation;
                    int sibling = transform.GetSiblingIndex();
                    try
                    {
                        transform.SetParent(vehicle.transform, true);
                        if (!WorldRegistry.PersonalObject(player) || !WorldRegistry.PersonalObject(player.transform.GetChild(0).gameObject)) throw new Exception("Seated player lost its local identity");
                        runtime.Registry.Discover(false);
                        if (runtime.Registry.IdOf(player) != "" || runtime.Registry.IdOf(player.transform.GetChild(0).gameObject) != "") throw new Exception("Seated player was registered as a shared spawn");
                    }
                    finally
                    {
                        transform.SetParent(parent, false); transform.localPosition = position; transform.localRotation = rotation; transform.localScale = scale; transform.SetSiblingIndex(sibling);
                    }
                    lines.Add("REPARENTED_PLAYER_EXCLUDED=PASS");
                    float timeScale = Time.timeScale;
                    byte[] save;
                    try { Time.timeScale = 0; save = runtime.Session.PrepareNativeSave(); }
                    finally { Time.timeScale = timeScale; }
                    if (save == null || save.Length == 0) throw new Exception("Native save preparation returned no bytes");
                    lines.Add("PREPARE_NATIVE_SAVE=PASS bytes=" + save.Length);
                    prepared = Time.realtimeSinceStartup;
                }
                if (Time.realtimeSinceStartup - prepared < 3) return;
                if (Application.loadedLevelName != "GAME" || !runtime.Active) throw new Exception("Preparing a native save closed the session");
                lines.Add("SESSION_REMAINS_PLAYING=PASS"); Complete(runtime);
            }
            catch (Exception error) { lines.Add("FAIL=" + error); Complete(runtime); }
        }
        private void Complete(CoopRuntime runtime)
        {
            done = true;
            File.WriteAllLines(Path.Combine(runtime.SaveRoot, "save-restore-proof.txt"), lines.ToArray());
        }
    }
}
