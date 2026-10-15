using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;
namespace MWCoop
{
    public static class MscPlayerProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static int hour;
        static bool At(int i, float t, float time) { return t >= time && steps.Add(i); }
        static PlayMakerFSM Crouch { get { return Game.FsmOn(Game.PlayerT.gameObject, "Crouch"); } }
        static void Record(string channel, string name, bool pass, string detail) { MscRegressionProbe.Record(channel, name, pass, detail); }
        public static void Update(float t, string channel)
        {
            float local = t - (Session.IsHost ? 0 : 20);
            if (At(1, local, 1)) { Game.SetState(Crouch, "Crouched 2"); Respawn.TestDie("Thirst"); }
            if (At(2, local, 3)) Respawn.TestChoose(1);
            if (At(3, local, 6))
                Record(channel, "revive-standing", !Respawn.Choosing && Crouch.enabled && Crouch.ActiveStateName == "Wait key" && !Game.GlobalBool("PlayerStop"), Crouch.ActiveStateName + ";Position=" + FireTools.GetF(Crouch, "Position", 0));
            if (At(4, local, 7)) Crouch.SendEvent("FINISHED");
            if (At(5, local, 9)) { Record(channel, "revive-crouch", Crouch.ActiveStateName == "Crouched 1", "State=" + Crouch.ActiveStateName); Crouch.SendEvent("FINISHED"); }
            if (At(6, local, 10)) Crouch.SendEvent("FINISHED");
            if (At(7, local, 11)) { VehicleSync.TestEnter("JONNEZ", false); }
            if (At(8, local, 13)) VehicleSync.TestEnter("JONNEZ", true);
            if (At(9, local, 16))
            {
                Record(channel, "revive-drive", VehicleSync.LocalDrivingRoot != null && VehicleSync.LocalDrivingRoot.name.StartsWith("JONNEZ"), "Vehicle=" + VehicleSync.LocalDrivingKey);
                Respawn.TestDie("Thirst");
            }
            if (At(20, local, 18)) Respawn.TestChoose(1);
            if (At(21, local, 19)) Record(channel, "revive-from-driver", VehicleSync.LocalDrivingRoot == null && Crouch.ActiveStateName == "Wait key" && !Game.GlobalBool("PlayerStop"), "Vehicle=" + VehicleSync.LocalDrivingKey + ";State=" + Crouch.ActiveStateName);
            if (At(10, t, 88))
            {
                var sun = Game.FsmOn(Game.FindAny("MAP/SUN/Pivot/SUN"), "Color"); hour = sun.FsmVariables.GetFsmInt("Time").Value;
            }
            if (At(11, t, 90) && Session.IsHost) Sleep();
            if (At(12, t, 106))
            {
                var sun = Game.FsmOn(Game.FindAny("MAP/SUN/Pivot/SUN"), "Color");
                Record(channel, "host-sleeps-alone", sun.FsmVariables.GetFsmInt("Time").Value == hour && !Game.GlobalBool("PlayerSleeps"), "Hour=" + sun.FsmVariables.GetFsmInt("Time").Value + ";State=" + sun.ActiveStateName + ";Ambient=" + RenderSettings.ambientLight);
            }
            if (At(13, t, 110) && !Session.IsHost) Sleep();
            if (At(14, t, 126))
            {
                var sun = Game.FsmOn(Game.FindAny("MAP/SUN/Pivot/SUN"), "Color");
                Record(channel, "guest-sleeps-alone", sun.FsmVariables.GetFsmInt("Time").Value == hour && !Game.GlobalBool("PlayerSleeps"), "Hour=" + sun.FsmVariables.GetFsmInt("Time").Value + ";State=" + sun.ActiveStateName);
            }
            if (At(15, t, 130)) Sleep();
            if (At(16, t, 149))
            {
                var sun = Game.FsmOn(Game.FindAny("MAP/SUN/Pivot/SUN"), "Color"); int actual = sun.FsmVariables.GetFsmInt("Time").Value;
                Record(channel, "sleep-together", actual != hour && !Game.GlobalBool("PlayerSleeps"), "Hour=" + actual + ";State=" + sun.ActiveStateName + ";Ambient=" + RenderSettings.ambientLight);
            }
        }
        static void Sleep()
        {
            var f = Game.FsmOn(Game.FindAny("YARD/Building/BEDROOM1/LOD_bedroom1/Sleep/SleepTrigger"), "Activate");
            FsmVariables.GlobalVariables.FindFsmFloat("PlayerFatigue").Value = 80;
            Game.SetState(f, "Sleep");
        }
    }
}
