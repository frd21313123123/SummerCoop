using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCoop
{
    // From SummerCoop.NativeSave: execute the save actions without moving every
    // FSM into SAVEGAME. MSC's setup/save transition chains can otherwise hang.
    public static class MscNativeSave
    {
        public static void Capture(bool exiting = false)
        {
            if (!PlayerSync.InGame) throw new InvalidOperationException("MSC save requires GAME");
            MscSaveGuard.Begin();
            int count = 0;
            try
            {
                foreach (UnityEngine.Object value in Game.AllFsms())
                {
                    PlayMakerFSM f = value as PlayMakerFSM;
                    if (f == null || !f.Fsm.Initialized || MscToolCases.Generated(f.transform)
                        || !exiting && (Game.UnderPlayer(f.transform) || f.FsmName == "WaitPlayer")) continue;
                    HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                    foreach (FsmTransition transition in f.Fsm.GlobalTransitions)
                    {
                        if (transition.EventName != "SAVEGAME") continue;
                        FsmState state = f.Fsm.GetState(transition.ToState);
                        while (state != null && visited.Add(state.Name))
                        {
                            foreach (FsmStateAction action in state.Actions)
                            {
                                if (action == null) continue;
                                string name = action.GetType().Name;
                                if (name.StartsWith("Save", StringComparison.Ordinal) || name == "ArrayListEasySave" || name.StartsWith("Get", StringComparison.Ordinal) || name == "BuildString" || name == "ConvertIntToString") { action.OnEnter(); count++; }
                            }
                            FsmState next = null;
                            foreach (FsmTransition t in state.Transitions) if (t.EventName == "FINISHED" && t.ToState.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0) next = f.Fsm.GetState(t.ToState);
                            state = next;
                        }
                    }
                }
                foreach (ES2Auto mesh in UnityEngine.Object.FindObjectsOfType<ES2Auto>())
                    if (!MscToolCases.Generated(mesh.transform) && !string.IsNullOrEmpty(mesh.saveFile) && !string.IsNullOrEmpty(mesh.uniqueTag)) mesh.Save();
                SleepingBags.OnGameSave();
                Wear.OnGameSave();
                MscSaveGuard.End();
                MscShop.MarkSaved();
                MscOrders.MarkSaved();
                Log.Info("MSC saved without switching native FSM states: " + count + " actions");
            }
            catch { MscSaveGuard.Rollback(); throw; }
        }
    }
}
