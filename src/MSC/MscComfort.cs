using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;

namespace MWCoop
{
    // Only the house wasp is a shared cheat. Camera effects belong to the
    // player who pressed the cure button, and remain enabled afterwards.
    public static class MscComfort
    {
        static bool hostNoWasp, suppressed, wasActive;
        static GameObject home;
        static float nextFind;
        public static bool NoHouseWasp { get { return Session.Active && !Session.IsHost ? hostNoWasp : Config.GetInt("Coop", "NoHouseWasp", 0) != 0; } }
        public static void OnHost(bool on) { hostNoWasp = on; }
        public static void SetHouseWasp(bool on) { Config.Save("Coop", "NoHouseWasp", on ? "1" : "0"); Update(); }
        public static void Reset() { home = null; suppressed = false; nextFind = 0; }
        public static void Update()
        {
            if (!PlayerSync.InGame) return;
            if (home == null && Time.realtimeSinceStartup >= nextFind)
            { nextFind = Time.realtimeSinceStartup + 2; home = Game.FindAny("YARD/WaspHome"); }
            if (home == null) return;
            bool off = Cheats.Allowed && NoHouseWasp;
            if (off)
            {
                if (!suppressed) { wasActive = home.activeSelf; suppressed = true; }
                if (home.activeSelf) home.SetActive(false);
            }
            else if (suppressed)
            {
                Transform wasp = home.transform.Find("LoneWasp"); if (wasp != null) wasp.gameObject.SetActive(false);
                home.SetActive(wasActive); suppressed = false;
            }
        }
        public static string Cure(bool sting)
        {
            if (!PlayerSync.InGame) return Lang.R("Load the game first.");
            GameObject camera = Game.PlayerPart("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
            PlayMakerFSM f = camera == null ? null : Game.FsmOn(camera, "Blindness");
            if (f == null)
            {
                camera = Game.FindAny("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
                f = camera == null ? null : Game.FsmOn(camera, "Blindness");
            }
            if (f == null) return Lang.R("Camera effects are not ready.");
            if (sting) Game.SetGlobalFloat("PlayerAllergy", 0);
            string active = f.ActiveStateName;
            bool allergy = active == "Allergy" || active == "Ease allergy" || active == "Blindness 2";
            if (sting || !allergy)
            {
                foreach (string name in sting ? new[] { "Intensity", "Origin", "MaxAllergy" } : new[] { "Intensity", "Origin" })
                { FsmFloat v = f.FsmVariables.FindFsmFloat(name); if (v != null) v.Value = 0; }
                Game.SetState(f, active == "Blindness 2" ? "Reset 2" : "Reset");
            }
            return null;
        }
    }
}
