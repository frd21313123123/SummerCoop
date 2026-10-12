using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerCoop.Core;
using SummerCoop.Game;
using UnityEngine;

// Test-only mod, never part of a release. The production SummerCoop DLL stays
// byte-identical. Only its other-mod check admits this single test observer.
public sealed class HostMovementHarnessMod : Mod
{
    public override string ID { get { return "SummerCoopHostMovementHarness"; } }
    public override string Name { get { return "SummerCoop host movement test"; } }
    public override string Author { get { return "SummerCoop tests"; } }
    public override string Version { get { return "1"; } }
    public override void ModSetup()
    {
        HostMovementHarness.ValidateScope();
        HarmonyInstance.Create("summercoop.host-movement-test").Patch(typeof(CoopRuntime).GetMethod("VerifyVanilla", BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(HostMovementHarness).GetMethod("AdmitOnlyHarness")), null, null);
        SetupFunction(Setup.OnMenuLoad, Begin);
    }
    private void Begin()
    {
        GameObject observer = new GameObject("SummerCoop.HostMovementHarness");
        UnityEngine.Object.DontDestroyOnLoad(observer); observer.AddComponent<HostMovementHarness>();
    }
}

public sealed class HostMovementHarness : MonoBehaviour
{
    private static HostMovementHarness active;
    private static string shared;
    private static long axisReads;
    private static bool moving;
    private HarmonyInstance harmony;
    private GameObject player;
    private FPSInputController input;
    private CharacterMotor motor;
    private float readyAt, began, deadline, finishedAt;
    private int stage;
    private bool done, clientReady;
    private Vector3 before, clientBefore;
    private long readsBefore;
    private string phase;

    public static void ValidateScope()
    {
        shared = CoopRuntime.Argument("--host-movement-shared");
        if (CoopRuntime.Argument("--host-movement-test") != "yes" || shared == "") throw new InvalidOperationException("Test harness requires explicit diagnostic arguments");
        shared = Path.GetFullPath(shared);
        string local = Path.GetFullPath(Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(HostMovementHarness).Assembly.Location), ".."), ".."));
        if (Path.GetFileName(local) != ".local" || !shared.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Movement test is restricted to workspace .local game copies");
        if (!Directory.Exists(shared)) throw new InvalidOperationException("Test exchange directory must already exist");
    }
    public static bool AdmitOnlyHarness()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Mods"), "*.dll"))
        {
            string name = Path.GetFileName(file);
            if (name != "SummerCoop.dll" && name != "HostMovementHarness.dll" && name != "MSCLoader_Console.dll" && name != "MSCLoader_Settings.dll") throw new InvalidOperationException("Unexpected mod in movement fixture: " + name);
        }
        return false;
    }
    private void Awake() { active = this; deadline = Time.realtimeSinceStartup + 240; }
    private void Update()
    {
        CoopRuntime r = CoopRuntime.Instance;
        if (done) { if (Time.realtimeSinceStartup - finishedAt > 2) Application.Quit(); return; }
        try
        {
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Movement scenario timed out");
            if (r == null || !r.Active || r.Registry == null) return;
            if (!r.Session.IsHost) { Client(r); return; }
            if (readyAt == 0) { readyAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - readyAt < 5) return;
            if (player == null)
            {
                player = GameObject.Find("PLAYER"); input = player.GetComponent<FPSInputController>(); motor = player.GetComponent<CharacterMotor>();
                if (input == null || motor == null) throw new Exception("Native player movement components missing");
                harmony = HarmonyInstance.Create("summercoop.host-movement-axis");
                harmony.Patch(typeof(FPSInputController).GetMethod("Update"), null, null, new HarmonyMethod(typeof(HostMovementHarness).GetMethod("Axes")));
                Record(r, "PRODUCTION_DLL=" + Codec.Hash(File.ReadAllBytes(typeof(CoopRuntime).Assembly.Location)));
            }
            if (stage == 0) { BeginMove(r, "SERVER_BEFORE_GUEST"); stage = 1; }
            else if (stage == 1 && Elapsed() >= 1.5f) { EndMove(r); File.WriteAllText(Path.Combine(shared, "baseline-ready"), "PASS"); stage = 2; }
            else if (stage == 2 && r.Session.World.Players.Count >= 2) { BeginMove(r, "GUEST_LOADING"); stage = 3; }
            else if (stage == 3 && Elapsed() >= 1.5f) { EndMove(r); stage = 4; }
            else if (stage == 4 && File.Exists(Path.Combine(shared, "guest-playing"))) { BeginMove(r, "GUEST_FULLY_LOADED"); stage = 5; }
            else if (stage == 5 && Elapsed() >= 2)
            {
                EndMove(r); File.WriteAllText(Path.Combine(shared, "host-moved"), "PASS"); stage = 6; began = Time.realtimeSinceStartup;
            }
            else if (stage == 6 && File.Exists(Path.Combine(shared, "client-observed-movement")))
            { Record(r, "HOST_MOVEMENT=PASS: server before guest, guest loading, fully loaded guest, and client-observed host pose"); Finish(r, true); }
            else if (stage == 6 && Elapsed() > 12) throw new Exception("Client did not observe host movement after loading");
        }
        catch (Exception e) { if (r != null) Record(r, "HOST_MOVEMENT=FAIL: " + e); Finish(r, false); }
    }
    private void Client(CoopRuntime r)
    {
        if (!clientReady)
        {
            clientReady = true; clientBefore = WorldRegistry.To(r.Session.World.Players["host"].Position);
            File.WriteAllText(Path.Combine(shared, "guest-playing"), "Phase=Playing; full snapshot applied; objects=" + r.Registry.Objects.Count);
            Record(r, "CLIENT_FULLY_LOADED=Playing; full snapshot applied");
        }
        Vector3 position = WorldRegistry.To(r.Session.World.Players["host"].Position);
        Vector3 delta = position - clientBefore; delta.y = 0;
        if (File.Exists(Path.Combine(shared, "host-moved")) && delta.magnitude > 0.5f)
        {
            Record(r, "CLIENT_OBSERVED_HOST_METRES=" + delta.magnitude.ToString("F3", CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(shared, "client-observed-movement"), "PASS"); Finish(r, true);
        }
    }
    private void BeginMove(CoopRuntime r, string label)
    {
        moving = false; CheckControls(r);
        // Select a free direction; never assign a position or invoke Move.
        Vector3 origin = player.transform.position + Vector3.up * 0.8f;
        float longest = -1; Vector3 direction = player.transform.forward;
        for (int angle = 0; angle < 360; angle += 45)
        {
            Vector3 candidate = Quaternion.Euler(0, angle, 0) * Vector3.forward; RaycastHit hit;
            float distance = Physics.SphereCast(origin, 0.3f, candidate, out hit, 4) ? hit.distance : 4;
            if (distance > longest) { longest = distance; direction = candidate; }
        }
        if (longest < 1) throw new Exception("No free walking direction in this saved location");
        player.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        before = player.transform.position; readsBefore = axisReads; began = Time.realtimeSinceStartup; phase = label; moving = true;
        Record(r, label + "_BEGIN position=" + before.ToString("F3") + " clear=" + longest.ToString("F2", CultureInfo.InvariantCulture));
    }
    private float Elapsed() { return Time.realtimeSinceStartup - began; }
    private void EndMove(CoopRuntime r)
    {
        moving = false; CheckControls(r); Vector3 delta = player.transform.position - before; delta.y = 0;
        long reads = axisReads - readsBefore;
        Record(r, phase + "_RESULT metres=" + delta.magnitude.ToString("F3", CultureInfo.InvariantCulture) + " native_axis_reads=" + reads + " peers=" + r.Session.World.Players.Count);
        if (delta.magnitude < 0.5f || reads < 4) throw new Exception("Host movement blocked in " + phase);
    }
    private void CheckControls(CoopRuntime r)
    {
        Record(r, "CONTROL scale=" + Time.timeScale + " joining=" + r.Joining + " hookMode=" + NativeHooks.UpdateMode + " saving=" + NativeHooks.Saving + " input=" + input.enabled + " motor=" + motor.enabled + " canControl=" + motor.canControl);
        foreach (string name in new[] { "PlayerStop", "PlayerInMenu", "PlayerSeated", "PlayerSleeps" })
        {
            FsmBool variable = FsmVariables.GlobalVariables.FindFsmBool(name);
            Record(r, name + "=" + (variable == null ? "MISSING" : variable.Value.ToString()));
            if (variable != null && variable.Value) throw new Exception("Host control flag blocks walking: " + name);
        }
        if (Time.timeScale <= 0 || r.Joining || NativeHooks.UpdateMode != 0 || NativeHooks.Saving || !input.enabled || !motor.enabled || !motor.canControl) throw new Exception("Native host controller blocked");
    }
    public static IEnumerable<CodeInstruction> Axes(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            MethodInfo method = instruction.operand as MethodInfo;
            if (method != null && method.Name == "GetAxis" && method.ReturnType == typeof(float) && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string)) instruction.operand = typeof(HostMovementHarness).GetMethod("ReadAxis");
            yield return instruction;
        }
    }
    public static float ReadAxis(string axis)
    {
        axisReads++;
        return active != null && moving && axis == "PlayerVertical" ? 1f : 0f;
    }
    private static void Record(CoopRuntime r, string message)
    {
        Directory.CreateDirectory(r.SaveRoot);
        File.AppendAllText(Path.Combine(r.SaveRoot, "host-movement-proof.txt"), Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
    }
    private void Finish(CoopRuntime r, bool passed)
    {
        moving = false; done = true; finishedAt = Time.realtimeSinceStartup;
        File.WriteAllText(Path.Combine(shared, r != null && r.Session != null && r.Session.IsHost ? "host-result" : "client-result"), passed ? "PASS" : "FAIL");
    }
    private void OnDestroy()
    {
        if (harmony != null) harmony.Unpatch(typeof(FPSInputController).GetMethod("Update"), typeof(HostMovementHarness).GetMethod("Axes"));
    }
}
