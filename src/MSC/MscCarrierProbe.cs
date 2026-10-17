using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscCarrierProbe
    {
        public static readonly List<string> Results = new List<string>();
        static readonly HashSet<int> steps = new HashSet<int>();
        static PlayMakerFSM carrier, creator;
        static long clock;
        static int filled;
        static float nextFill;
        static string extraWood;
        static bool At(int step, float t, float at) { return t >= at && steps.Add(step); }
        static List<GameObject> Wood(bool split)
        {
            List<GameObject> result = new List<GameObject>();
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (body.name.StartsWith(split ? "firewood" : "log(Clone)", StringComparison.Ordinal) && Props.ItemId(body.gameObject).Length > 0) result.Add(body.gameObject);
            result.Sort((a, b) => string.CompareOrdinal(Props.ItemId(a), Props.ItemId(b))); return result;
        }
        static void Put(bool last)
        {
            List<GameObject> wood = Wood(true); if (wood.Count == 0) throw new InvalidOperationException("No carrier fixture firewood");
            GameObject part = wood[last ? wood.Count - 1 : 0];
            part.transform.parent = null;
            Rigidbody body = part.GetComponent<Rigidbody>(); body.isKinematic = false; body.useGravity = false; body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            if (!Session.IsHost) Home.MarkHeld(part);
            carrier.FsmVariables.FindFsmGameObject("FirewoodObject").Value = part;
            Game.SetState(carrier, "Parent");
        }
        static void Take()
        {
            PlayMakerFSM pick = Game.FsmOn(carrier.transform.parent.Find("PickupTrigger").gameObject, "Use");
            Game.SetState(pick, "Bool test");
        }
        static void Check(string label, int expected)
        {
            int count = carrier.FsmVariables.FindFsmInt("WoodsIndex").Value, visible = 0;
            foreach (Transform child in carrier.transform.parent.Find("Woods")) if (child.gameObject.activeSelf) visible++;
            string line = "Carrier=" + label + ";Count=" + count + ";Visible=" + visible + ";Pass=" + (count == expected && visible == expected);
            Results.Add(line); Log.Info(line);
        }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-carrier-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 30) return;
            if (carrier == null)
            {
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || f.hideFlags != HideFlags.None || f.FsmName != "Trigger") continue;
                    FsmString tag = f.FsmVariables.FindFsmString("UniqueTagWood");
                    if (tag != null && tag.Value == "woodcarrier1wood") carrier = f;
                }
                if (carrier == null) throw new InvalidOperationException("Carrier fixture missing");
                creator = Game.FsmOn(Game.FindAny("COTTAGE/Logging/Logwall"), "Use");
                MscProbeFiles.WriteAllText(Path.Combine(channel, Session.IsHost ? "host-ready.txt" : "guest-ready.txt"), "ready");
            }
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(5).Ticks.ToString());
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (t < 0) return;
            if (At(0, t, 1) && Session.IsHost)
                for (int i = 0; i < 12; i++)
                {
                    Game.SetState(creator, "Create log");
                    GameObject log = creator.FsmVariables.FindFsmGameObject("Log").Value;
                    foreach (Rigidbody body in log.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.useGravity = false; }
                }
            if (At(1, t, 5) && Session.IsHost) foreach (GameObject log in Wood(false)) foreach (FixedJoint joint in log.GetComponentsInChildren<FixedJoint>()) UnityEngine.Object.Destroy(joint);
            if (At(2, t, 10) && Session.IsHost) Put(false);
            if (At(3, t, 14)) Check("host-stationary-add", 1);
            if (At(4, t, 16) && !Session.IsHost) Take();
            if (At(5, t, 20)) Check("guest-take", 0);
            if (At(6, t, 22) && !Session.IsHost) Put(false);
            if (At(7, t, 26)) Check("guest-stationary-add", 1);
            if (At(8, t, 28) && Session.IsHost) Take();
            if (At(9, t, 32)) Check("host-take", 0);
            if (At(10, t, 34)) Put(!Session.IsHost);
            if (At(11, t, 40)) Check("concurrent-add", 2);
            if (At(12, t, 43) && Session.IsHost) Take();
            if (At(13, t, 44) && !Session.IsHost) Take();
            if (At(14, t, 49)) Check("both-take", 0);
            if (Session.IsHost && t >= 52 && filled < 9 && Time.realtimeSinceStartup >= nextFill) { filled++; nextFill = Time.realtimeSinceStartup + .3f; Put(false); }
            if (At(15, t, 60)) Check("capacity-nine", 9);
            if (At(16, t, 62) && Session.IsHost) { extraWood = Props.ItemId(Wood(true)[0]); Put(false); }
            if (At(17, t, 65))
            {
                Check("full-preserves-wood", 9);
                if (Session.IsHost && Props.ObjectOf(extraWood) == null) throw new InvalidOperationException("Full carrier consumed the excess log");
            }
            if (At(18, t, 67) && Session.IsHost) Take();
            if (At(19, t, 70) && !Session.IsHost) Put(false);
            if (At(20, t, 75)) Check("refill-after-full", 9);
        }
    }
}
