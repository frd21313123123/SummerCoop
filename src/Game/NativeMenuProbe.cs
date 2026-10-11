using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SummerCoop.Game
{
    // Read-only observer for real keyboard/menu checks in an isolated test copy.
    // It never presses a key, toggles a menu, or assigns a native FSM state.
    public sealed class NativeMenuProbe : MonoBehaviour
    {
        private GameObject menu;
        private PlayMakerFSM openMenu;
        private string path, directory, previous;
        private float nextSample, afterKey;
        private int escapePresses;
        private bool ready, dumped;

        private void Update()
        {
            if (CoopRuntime.Argument("--summercoop-test-native-menu") != "yes"
                || CoopRuntime.Argument("--summercoop-test-report") == "") return;
            CoopRuntime runtime = CoopRuntime.Instance;
            if (runtime == null || (!runtime.Active && !runtime.NativeBaselineReady) || runtime.Joining) return;
            if (!ready)
            {
                directory = runtime.SaveRoot;
                path = Path.Combine(directory, "native-menu-proof.txt");
                foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (candidate.name == "OptionsMenu" && candidate.transform.parent != null && candidate.transform.parent.name == "Systems") { menu = candidate; break; }
                foreach (PlayMakerFSM candidate in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (candidate.name == "Options" && candidate.FsmName == "Open Menu"
                        && candidate.transform.parent != null && candidate.transform.parent.name == "Systems") { openMenu = candidate; break; }
                File.WriteAllText(path, "ROLE=" + (runtime.NativeBaselineReady ? "native-baseline" : runtime.Session.IsHost ? "host" : "client") + Environment.NewLine);
                ready = true;
                Sample(runtime, "READY");
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                escapePresses++;
                Sample(runtime, "ESC_DOWN=" + escapePresses);
                afterKey = Time.realtimeSinceStartup + 0.35f;
            }
            if (Input.GetMouseButtonDown(0)) Sample(runtime, "MOUSE0_DOWN position=" + Input.mousePosition);
            if (Input.GetMouseButtonUp(0)) Sample(runtime, "MOUSE0_UP position=" + Input.mousePosition);
            // Allow native OnEnable/Start to initialize the Resume button before
            // observing its actions; do not initialize or execute it ourselves.
            if (!dumped && menu != null && menu.activeInHierarchy && afterKey == 0) { DumpMenu(); dumped = true; }
            if (afterKey != 0 && Time.realtimeSinceStartup >= afterKey)
            {
                Sample(runtime, "ESC_SETTLED=" + escapePresses);
                Application.CaptureScreenshot(Path.Combine(directory, "native-menu-esc-" + escapePresses + ".png"));
                afterKey = 0;
            }
            if (Time.realtimeSinceStartup >= nextSample)
            {
                nextSample = Time.realtimeSinceStartup + 0.25f;
                string current = State(runtime);
                if (current != previous) { Append("CHANGE " + current); previous = current; }
            }
        }

        private string State(CoopRuntime runtime)
        {
            return "MENU=" + (menu == null ? "MISSING" : menu.activeSelf + "/" + menu.activeInHierarchy)
                + " IN_MENU=" + Bool("PlayerInMenu") + " STOP=" + Bool("PlayerStop")
                + " SCALE=" + Time.timeScale.ToString("F2", CultureInfo.InvariantCulture)
                + " OPEN_FSM=" + (openMenu == null ? "MISSING" : openMenu.ActiveStateName)
                + " OPEN_SHARED=" + (openMenu != null && runtime.Registry != null && runtime.Registry.Binding(openMenu.Fsm) != null);
        }

        private static string Bool(string name)
        {
            FsmBool variable = FsmVariables.GlobalVariables.FindFsmBool(name);
            return variable == null ? "MISSING" : variable.Value.ToString();
        }

        private void Sample(CoopRuntime runtime, string reason)
        {
            Append(reason + " " + State(runtime));
            if (menu == null) return;
            GUIText[] labels = menu.GetComponentsInChildren<GUIText>(true);
            int visible = 0, nonempty = 0;
            List<string> examples = new List<string>();
            foreach (GUIText label in labels)
            {
                if (!label.enabled || !label.gameObject.activeInHierarchy) continue;
                visible++;
                if (string.IsNullOrEmpty(label.text)) continue;
                nonempty++;
                if (examples.Count < 6) examples.Add(label.name + ":" + label.text.Replace('\n', ' ').Replace('\r', ' '));
            }
            Append("TEXT labels=" + labels.Length + " visible=" + visible + " nonempty=" + nonempty + " examples=" + string.Join(" | ", examples.ToArray()));
            visible = 0; nonempty = 0; examples.Clear();
            TextMesh[] meshLabels = menu.GetComponentsInChildren<TextMesh>(true);
            foreach (TextMesh label in meshLabels)
            {
                Renderer renderer = label.GetComponent<Renderer>();
                if (!label.gameObject.activeInHierarchy || renderer == null || !renderer.enabled) continue;
                visible++;
                if (string.IsNullOrEmpty(label.text)) continue;
                nonempty++;
                if (examples.Count < 8) examples.Add(label.name + ":" + label.text.Replace('\n', ' ').Replace('\r', ' '));
            }
            Append("TEXT_MESH labels=" + meshLabels.Length + " visible=" + visible + " nonempty=" + nonempty + " examples=" + string.Join(" | ", examples.ToArray()));
            UnityEngine.UI.Text[] canvasLabels = menu.GetComponentsInChildren<UnityEngine.UI.Text>(true);
            visible = 0; nonempty = 0; examples.Clear();
            foreach (UnityEngine.UI.Text label in canvasLabels)
            {
                if (!label.enabled || !label.gameObject.activeInHierarchy) continue;
                visible++;
                if (string.IsNullOrEmpty(label.text)) continue;
                nonempty++;
                if (examples.Count < 8) examples.Add(label.name + ":" + label.text.Replace('\n', ' ').Replace('\r', ' '));
            }
            Append("CANVAS_TEXT labels=" + canvasLabels.Length + " visible=" + visible + " nonempty=" + nonempty + " examples=" + string.Join(" | ", examples.ToArray()));
            foreach (PlayMakerFSM component in menu.GetComponents<PlayMakerFSM>())
                Append("MENU_FSM=" + component.FsmName + " initialized=" + component.Fsm.Initialized + " state=" + component.ActiveStateName);
            foreach (PlayMakerFSM component in menu.GetComponentsInChildren<PlayMakerFSM>(true))
                if (PathOf(component.transform).IndexOf("/Menu/Btn_Resume", StringComparison.Ordinal) >= 0)
                    Append("RESUME_FSM=" + PathOf(component.transform) + " " + component.FsmName + " initialized=" + component.Fsm.Initialized + " state=" + component.ActiveStateName);
        }

        private void OnGUI()
        {
            if (!ready || Event.current == null) return;
            EventType type = Event.current.type;
            if (type == EventType.MouseDown || type == EventType.MouseUp)
                Append("GUI_EVENT=" + type + " button=" + Event.current.button + " position=" + Event.current.mousePosition);
        }

        private string PathOf(Transform transform)
        {
            string result = transform.name;
            while (transform.parent != null && transform != menu.transform) { transform = transform.parent; result = transform.name + "/" + result; }
            return result;
        }

        private void DumpMenu()
        {
            using (StreamWriter writer = new StreamWriter(Path.Combine(directory, "native-menu-fsms.txt")))
                foreach (PlayMakerFSM component in menu.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    string objectPath = PathOf(component.transform);
                    if (component.gameObject != menu && objectPath.IndexOf("/Menu/Btn_Resume", StringComparison.Ordinal) < 0) continue;
                    writer.WriteLine("FSM " + objectPath + " " + component.FsmName + " state=" + component.ActiveStateName);
                    if (!component.Fsm.Initialized) continue;
                    foreach (FsmState state in component.Fsm.States)
                    {
                        writer.WriteLine("STATE " + state.Name);
                        foreach (FsmTransition transition in state.Transitions) writer.WriteLine("TRANS " + transition.EventName + " -> " + transition.ToState);
                        foreach (FsmStateAction action in state.Actions)
                        {
                            writer.WriteLine("ACTION " + action.GetType().Name);
                            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                            {
                                object value = field.GetValue(action);
                                NamedVariable variable = value as NamedVariable;
                                string text = variable == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : variable.Name + "=" + variable;
                                FsmOwnerDefault owner = value as FsmOwnerDefault;
                                if (owner != null) text = owner.OwnerOption + " " + (owner.GameObject == null ? "NONE" : owner.GameObject.Name + "=" + owner.GameObject);
                                writer.WriteLine("FIELD " + field.Name + " " + text);
                            }
                        }
                    }
                }
        }

        private void Append(string value)
        {
            File.AppendAllText(path, "t=" + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture) + " " + value + Environment.NewLine);
        }
    }
}
