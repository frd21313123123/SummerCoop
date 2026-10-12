using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerCoop.Core;
using SummerCoop.Game;

internal static class NativeBindingsTests
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static bool Personal(object value)
    {
        if (value is FsmProperty) value = ((FsmProperty)value).TargetObject;
        if (value is NamedVariable) return ((NamedVariable)value).Name == "FPSCamera" || ((NamedVariable)value).Name == "Hand";
        if (value is FsmEventTarget) return Personal(((FsmEventTarget)value).gameObject);
        NamedVariable name = NativeActionReader.Field(value, "GameObject") as NamedVariable;
        return name != null && (name.Name == "FPSCamera" || name.Name == "Hand");
    }
    public static int Main()
    {
        try
        {
            SetBoolValue stop = new SetBoolValue { boolVariable = new FsmBool { Name = "PlayerStop" }, boolValue = true };
            NativeActionInfo info = NativeActionReader.Describe(stop, Personal);
            Check(info.WritesPersonal && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "real SetBoolValue cannot stop host for a guest");
            GetVelocity velocity = new GetVelocity { vector = new FsmVector3 { Name = "PlayerVelocity" } };
            info = NativeActionReader.Describe(velocity, Personal);
            Check(info.WritesPersonal && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "real velocity getter cannot overwrite host movement globals");
            EnableFSM player = new EnableFSM { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = new FsmGameObject { Name = "FPSCamera" } }, fsmName = "PlayerFunctions", enable = false };
            info = NativeActionReader.Describe(player, Personal);
            Check(info.PersonalTarget && NativeActionPolicy.Allowed(info, NativeActionScope.PersonalReplay) && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "real EnableFSM animates the acting player only");
            SetProperty property = new SetProperty { targetProperty = new FsmProperty { TargetObject = new FsmObject { Name = "FPSCamera" } } };
            info = NativeActionReader.Describe(property, Personal);
            Check(info.PersonalTarget && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "real FsmProperty camera target cannot alter the host view");
            SendEvent animation = new SendEvent { eventTarget = new FsmEventTarget { gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = new FsmGameObject { Name = "Hand" } } } };
            info = NativeActionReader.Describe(animation, Personal);
            Check(info.PersonalTarget && NativeActionPolicy.Allowed(info, NativeActionScope.PersonalReplay) && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "real SendEvent targets the guest hand animation only");
            IntAdd stock = new IntAdd { intVariable = new FsmInt { Name = "DestroyedBottles" }, add = 1 };
            info = NativeActionReader.Describe(stock, Personal);
            Check(!info.WritesPersonal && NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor) && !NativeActionPolicy.Allowed(info, NativeActionScope.PersonalReplay), "real IntAdd consumes beer on host once");
            GetAxis scroll = new GetAxis { axisName = "Mouse ScrollWheel", store = new FsmFloat { Name = "Scroll" } };
            var outputs = NativeActionReader.InputOutputs(new FsmStateAction[] { scroll });
            Check(outputs.Contains("Scroll") && !outputs.Contains("Stage"), "real GetAxis output stays local while bolt Stage stays authoritative");
            GetButtonDown driving = new GetButtonDown { buttonName = "DrivingMode", storeResult = new FsmBool { Name = "Pressed" } };
            outputs = NativeActionReader.InputOutputs(new FsmStateAction[] { driving });
            Check(outputs.Contains("Pressed") && NativeActionPolicy.Pollable(new[] { NativeActionReader.Describe(driving, Personal) }), "real DrivingMode action remains client polling");
            GetName partName = new GetName { storeName = new FsmString { Name = "HandChild" } };
            GetChild heldPart = new GetChild { storeResult = new FsmGameObject { Name = "Part" } };
            outputs = NativeActionReader.InputOutputs(new FsmStateAction[] { partName, heldPart });
            Check(outputs.Contains("HandChild") && outputs.Contains("Part"), "actual attachment hand references and derived names remain client-local");
            SetIntValue key = new SetIntValue { intVariable = new FsmInt { Name = "PlayerKeyGifu" }, intValue = 2 };
            info = NativeActionReader.Describe(key, Personal);
            Check(info.WritesPersonal && !NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "actual truck key pickup cannot overwrite another player's key inventory");
            AddTorque torque = new AddTorque { vector = new FsmVector3 { Name = "PlayerDir" } };
            info = NativeActionReader.Describe(torque, Personal);
            Check(!info.WritesPersonal && NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor), "personal direction used as a force input is not mistaken for a player write");
            FsmEvent useEvent = (FsmEvent)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FsmEvent));
            typeof(FsmEvent).GetField("name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(useEvent, "USE");
            FsmTransition useTransition = new FsmTransition { ToState = "Check drink" };
            typeof(FsmTransition).GetField("fsmEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(useTransition, useEvent);
            Check(NativeUseInput.Destination(new FsmStateAction[] { new GetButtonDown { buttonName = "Use", sendEvent = useEvent } }, new[] { useTransition }) == "Check drink", "actual configured Use button routes through native drinking checks");
            Check(typeof(Fsm).GetMethod("SwitchState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null, "actual PlayMaker transition hook signature available");
            var controls = typeof(CarController).GetMethod("GetInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Check(controls != null && controls.IsVirtual && controls.GetParameters().Length == 7 && typeof(CarController).IsAssignableFrom(typeof(AxisCarController)), "actual vehicle input accepts base and axis controllers with virtual dispatch");
            bool axisFound = false; var methods = NativeVehicleBindings.InputMethods();
            foreach (var method in methods)
            {
                if (method.DeclaringType == typeof(AxisCarController)) axisFound = true;
                if (method.IsAbstract || method.GetMethodBody() == null || method.GetParameters().Length != 7) throw new Exception("Vehicle hook selected an abstract or incompatible input method");
                string[] expectedNames = { "throttleInput", "brakeInput", "steerInput", "handbrakeInput", "clutchInput", "startEngineInput", "targetGear" };
                var parameters = method.GetParameters();
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].Name != expectedNames[i] || !parameters[i].ParameterType.IsByRef) throw new Exception("Harmony prefix cannot bind vehicle input argument " + i);
            }
            Check(controls.IsAbstract && axisFound && methods.Length > 0, "production vehicle hook selector excludes abstract slots and selects concrete game overrides");
            SetBoolValue active = new SetBoolValue { Enabled = true }, disabled = new SetBoolValue { Enabled = false };
            NativeHooks.ClientProjection cached = new NativeHooks.ClientProjection { Effects = new FsmStateAction[] { active, disabled }, Enabled = new bool[2] };
            var outer = cached.Snapshot(); var inner = cached.Snapshot(); outer.Suspend(); inner.Suspend(); inner.Restore();
            if (active.Enabled) throw new Exception("Nested action mask escaped its outer scope");
            outer.Restore();
            Check(active.Enabled && !disabled.Enabled && !object.ReferenceEquals(inner.Enabled, outer.Enabled), "actual compiled nested action masks restore controls and preserve disabled actions");
            var completedMask = cached.Snapshot(); completedMask.Suspend(); completedMask.FinishSuppressed(); completedMask.Restore();
            Check(active.Finished && active.Enabled && !disabled.Enabled, "suppressed shared action finishes so native personal replay can leave the state");
            Console.WriteLine("ALL " + passed + " NATIVE BINDING CHECKS PASSED (managed action definitions; no Unity physics)"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
