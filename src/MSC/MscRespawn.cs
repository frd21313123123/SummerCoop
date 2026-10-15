using UnityEngine;

namespace MWCoop
{
    public static class MscRespawn
    {
        public static void RestoreControls(GameObject player)
        {
            if (player == null) return;
            MscSleep.Revive();
            foreach (string name in new[] { "PlayerStop", "PlayerSeated", "PlayerSleeps", "PlayerComputer", "PlayerInMenu", "PlayerSeatbeltsOn",
                "PlayerCarControl", "PlayerVanControl", "PlayerMuscleControl", "PlayerTruckControl", "PlayerTractorControl", "PlayerMopedControl", "PlayerBoatControl" })
                Game.SetGlobalBool(name, false);
            var vehicle = HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmString("PlayerCurrentVehicle");
            if (vehicle != null) vehicle.Value = "";
            player.transform.localScale = Vector3.one;
            var setup = Game.FindAny("Systems/Setup Game");
            var wait = setup != null ? Game.FsmOn(setup, "WaitPlayer") : null;
            var dead = wait != null ? wait.FsmVariables.FindFsmBool("PlayerDead") : null;
            if (dead != null) dead.Value = false;
            var crouch = Game.FsmOn(player, "Crouch");
            if (crouch != null)
            {
                var inCar = crouch.FsmVariables.FindFsmBool("PlayerInCar");
                var inWater = crouch.FsmVariables.FindFsmBool("PlayerInWater");
                if (inCar != null) inCar.Value = false;
                if (inWater != null) inWater.Value = false;
                crouch.enabled = true; Game.SetState(crouch, "Wait key");
            }
            foreach (string name in new[] { "Stopping", "Running", "WalkSpeed", "Update Cursor" })
            {
                var fsm = Game.FsmOn(player, name); if (fsm == null) continue;
                fsm.enabled = true;
                string state = name == "Stopping" ? "Move" : name == "Running" ? "No run" : name == "WalkSpeed" ? "State 2" : "Exit car";
                if (fsm.Fsm.GetState(state) != null) Game.SetState(fsm, state);
            }
            Component motor = player.GetComponent("CharacterMotor");
            if (motor != null)
            {
                var control = motor.GetType().GetField("canControl"); if (control != null) control.SetValue(motor, true);
            }
            MscPlatforms.Reset();
        }
        // Outside the west wall of MSC's actual church. Its root transform is at
        // the village origin; use the building collider, not MWC apartment coords.
        public static Vector3 ChurchTop()
        {
            GameObject church = Game.FindAny("PERAJARVI/CHURCH/Church");
            if (church != null)
                foreach (Collider collider in church.GetComponentsInChildren<Collider>(true))
                    if (collider.name == "collider" && collider.bounds.size.sqrMagnitude > 1f)
                    {
                        Bounds bounds = collider.bounds;
                        return new Vector3(bounds.min.x - 3f, bounds.min.y + 4f, bounds.center.z);
                    }
            // Measured from the stock MSC church collider in GAME.
            return new Vector3(-1425.6f, 7.7f, 1149.1f);
        }
    }
}
