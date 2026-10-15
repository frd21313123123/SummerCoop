using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Runs only with the explicit local QA channel. Native pickup, fold and
    // pumping actions pass through the production Props/Garage network handlers.
    public static class MscGarageProbe
    {
        public static readonly List<string> Results = new List<string>();
        static long ticks;
        static int stage = -1, pumps;
        static bool placed, folded, recorded, released, measuring, picked;
        static float nextPump, firstHeight, maxRise;
        static Vector3 carryAt;
        static string problem = "";
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-garage-channel");
            if (channel.Length == 0) return;
            string clock = Path.Combine(channel, "start.txt");
            if (ticks == 0)
            {
                if (VehicleSync.Count == 0 || !Props.HandFound || Props.BodyOf(MscGarage.Identity(0, false)) == null || Props.BodyOf(MscGarage.Identity(1, false)) == null) return;
                string ready = Path.Combine(channel, Session.IsHost ? "host-ready.txt" : "guest-ready.txt");
                if (!File.Exists(ready)) File.WriteAllText(ready, "ready");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) File.WriteAllText(clock, DateTime.UtcNow.AddSeconds(3).Ticks.ToString(C));
                if (!File.Exists(clock) || !long.TryParse(File.ReadAllText(clock), out ticks)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
            if (elapsed < 0 || elapsed >= 120) return;
            int current = (int)(elapsed / 30);
            float phase = elapsed - current * 30;
            int kind = current / 2; // floor jack, then car jack, each guest -> host
            kind = kind == 0 ? 1 : 0;
            bool actor = (current % 2 == 0) != Session.IsHost;
            string key = MscGarage.Identity(kind, false);
            Transform root, point; PlayMakerFSM ctrl, fold;
            if (!Garage.TestDevice(kind, out root, out point, out ctrl, out fold)) return;
            Rigidbody car = VehicleSync.Body("SATSUMA");
            if (car == null || point == null) return;
            if (stage != current)
            {
                stage = current; pumps = 0; placed = folded = recorded = released = measuring = picked = false; problem = ""; nextPump = 0;
                firstHeight = car.position.y; maxRise = 0;
                carryAt = root.position + new Vector3(1.2f, .3f, 0);
                Log.Info("MSC garage probe " + stage + " " + key + (actor ? " ACTOR" : " OBSERVER") + " tracked=" + (Props.BodyOf(key) != null));
            }
            if (actor)
            {
                if (phase < 2.5f)
                {
                    Transform cam = PlayerSync.LocalCamera;
                    Rigidbody held = cam != null ? Props.TestHold(key, cam) : null;
                    if (held != null) { picked = true; held.transform.position = carryAt; held.transform.rotation = Quaternion.identity; }
                }
                if (phase >= 2.5f && !released) { released = true; Props.TestHold(key, null); }
                if (phase >= 4 && !placed)
                {
                    placed = true;
                    Vector3 at = car.position + car.rotation * new Vector3(-.5f, 0, -.5f);
                    float ground = car.position.y - .15f;
                    foreach (RaycastHit hit in Physics.RaycastAll(at + Vector3.up * 3, Vector3.down, 8))
                    {
                        Transform ht = hit.collider.transform;
                        if (ht == root || ht.IsChildOf(root) || ht == car.transform || ht.IsChildOf(car.transform) || Game.UnderPlayer(ht) || hit.collider.isTrigger) continue;
                        if (hit.normal.y > .8f && hit.point.y < car.position.y) ground = Mathf.Max(ground - 1, hit.point.y);
                    }
                    root.rotation = Quaternion.Euler(0, car.rotation.eulerAngles.y, 0);
                    root.position += new Vector3(at.x, ground + .03f, at.z) - point.position;
                    Rigidbody rb = root.GetComponent<Rigidbody>(); if (rb != null) { rb.velocity = rb.angularVelocity = Vector3.zero; }
                    Transform player = Game.PlayerT;
                    if (player != null) { CharacterController cc = player.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false; player.position = at + new Vector3(-2, 1, 0); if (cc != null) cc.enabled = true; }
                    Log.Info("MSC garage probe placed root=" + root.position.ToString("F3") + " lift=" + point.position.ToString("F3") + " car=" + car.position.ToString("F3"));
                    Game.SetState(ctrl, "Down");
                }
                if (phase >= 5 && !folded)
                {
                    folded = true;
                    if (fold != null && !fold.FsmVariables.GetFsmBool("Open").Value) Game.SetState(fold, "Bool test");
                }
                if (phase >= 6 && phase <= 17 && Time.realtimeSinceStartup >= nextPump)
                {
                    nextPump = Time.realtimeSinceStartup + .28f;
                    // Start after the terrain check: the isolated test fixture
                    // positions the jack, rather than exercising mouse picking.
                    if (Game.SetState(ctrl, kind == 0 ? "Check rigidbody" : "Up")) pumps++;
                }
                if (phase >= 23 && !recorded)
                {
                    recorded = true;
                    if (!picked) problem = "PICKUP NOT TRACKED";
                    float y = ctrl.FsmVariables.GetFsmFloat("Y").Value;
                    File.WriteAllText(Path.Combine(channel, "state-" + stage + ".txt"), string.Join(";", new[]{F(root.position.x), F(root.position.y), F(root.position.z), F(y), F(point.position.y), F(car.position.y), F(maxRise)}));
                    Record(key, true, 0, 0, 0, y, car, maxRise);
                }
                if (phase >= 28) Game.SetState(ctrl, "Down");
            }
            else if (phase >= 26 && !recorded)
            {
                string state = Path.Combine(channel, "state-" + stage + ".txt");
                if (File.Exists(state))
                {
                    recorded = true; string[] a = File.ReadAllText(state).Split(';');
                    Vector3 expected = new Vector3(P(a[0]), P(a[1]), P(a[2]));
                    float y = ctrl.FsmVariables.GetFsmFloat("Y").Value;
                    Record(key, false, Vector3.Distance(root.position, expected), Mathf.Abs(y-P(a[3])), Mathf.Abs(car.position.y-P(a[5])), y, car, maxRise);
                }
            }
            if (phase >= 5 && !measuring) { measuring = true; firstHeight = car.position.y; maxRise = 0; }
            if (measuring) maxRise = Mathf.Max(maxRise, car.position.y-firstHeight);
        }
        static void Record(string key, bool actor, float rootError, float levelError, float carError, float level, Rigidbody car, float rise)
        {
            string line="Garage="+stage+";Device="+key+";Role="+(actor?"actor":"observer")+";Tracked="+(Props.BodyOf(key)!=null)+";RootError="+F(rootError)+";LevelError="+F(levelError)+";CarError="+F(carError)+";Level="+F(level)+";CarY="+F(car.position.y)+";Rise="+F(rise)+";Pumps="+pumps+";Owner="+VehicleSync.Authority(car)+";Problem="+problem;
            Results.Add(line);Log.Info("MSC "+line);
        }
        static string F(float f) { return f.ToString("F4",C); }
        static float P(string f) { return float.Parse(f,C); }
    }
}
