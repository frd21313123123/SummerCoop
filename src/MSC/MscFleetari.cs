using System;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Opening hours and the automatic garage door belong to the world host.
    // Brochure input and its camera stay local to the customer.
    public static class MscFleetari
    {
        const string Module = "msc-fleetari";
        static PlayMakerFSM hours, register, officeDoor;
        static GameObject mechanic, parts;
        static Transform garageDoor, open, closed;
        static FsmBool available;
        static int received = -1, sent = -1;
        static float nextBind, nextSend;
        public static bool Owns(PlayMakerFSM f)
        { return f != null && f.name == "REPAIRSHOP" && f.FsmName == "OpeningHours"; }
        public static void OnLevelLoaded()
        {
            hours = register = officeDoor = null; mechanic = parts = null;
            garageDoor = open = closed = null; available = null;
            received = sent = -1; nextBind = nextSend = 0;
        }
        static bool Bind()
        {
            GameObject shop = Game.FindAny("REPAIRSHOP"); if (shop == null) return false;
            hours = Game.FsmOn(shop, "OpeningHours");
            if (hours == null || !hours.Fsm.Initialized) { hours = null; return false; }
            available = hours.FsmVariables.FindFsmBool("OpenRepairShop");
            mechanic = Game.FindAny("REPAIRSHOP/LOD/Office/Fleetari"); parts = Game.FindAny("REPAIRSHOP/LOD/Store/Parts");
            register = Game.FsmOn(Game.FindAny("REPAIRSHOP/LOD/Store/ShopCashRegister/Register"), "Data");
            officeDoor = Game.FsmOn(Game.FindAny("REPAIRSHOP/LOD/Door/Handle"), "Use");
            open = shop.transform.Find("Building/Door1Open"); closed = shop.transform.Find("Building/Door1Closed");
            garageDoor = open != null ? open.Find("garage_door 1") : null;
            if (garageDoor == null && closed != null) garageDoor = closed.Find("garage_door 1");
            if (available == null || mechanic == null || parts == null || register == null || garageDoor == null || open == null || closed == null)
            { hours = null; return false; }
            if (!Replay.Claim(hours, Module)) { hours = null; return false; }
            NearDoors.Retarget(hours);
            if (!Session.IsHost) hours.enabled = false;
            Log.Info("MSC Fleetari: host opening hours and garage door; local service brochure");
            return true;
        }
        static int Capture()
        {
            return (available.Value ? 1 : 0) | (garageDoor.parent == open ? 2 : 0)
                | (mechanic.activeSelf ? 4 : 0) | (parts.activeSelf ? 8 : 0) | (register.enabled ? 16 : 0);
        }
        static void Apply(int flags)
        {
            available.Value = (flags & 1) != 0;
            if (mechanic.activeSelf != ((flags & 4) != 0)) mechanic.SetActive((flags & 4) != 0);
            if (parts.activeSelf != ((flags & 8) != 0)) parts.SetActive((flags & 8) != 0);
            register.enabled = (flags & 16) != 0;
            Transform parent = (flags & 2) != 0 ? open : closed;
            if (garageDoor.parent != parent)
            {
                garageDoor.parent = parent; garageDoor.localPosition = Vector3.zero; garageDoor.localRotation = Quaternion.identity;
                if (parent == closed && officeDoor != null)
                {
                    Replay.Depth++;
                    try { officeDoor.SendEvent("GLOBALEVENT"); }
                    finally { Replay.Depth--; }
                }
            }
        }
        public static void OnRemote(int who, NetReader reader)
        {
            if (Session.IsHost || who != 0) return;
            int flags = reader.U8(); if (reader.More || (flags & ~31) != 0) throw new System.IO.IOException("Invalid Fleetari opening state");
            received = flags;
            if (hours != null) Apply(flags);
        }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame || Time.timeSinceLevelLoad < 3) return;
            float now = Time.realtimeSinceStartup;
            if (hours == null) { if (now < nextBind) return; nextBind = now + 1; if (!Bind()) return; }
            if (!Session.IsHost)
            {
                hours.enabled = false;
                if (received >= 0) Apply(received);
                return;
            }
            int flags = Capture();
            if (flags == sent && now < nextSend) return;
            sent = flags; nextSend = now + 2;
            Session.SendAll(new NetWriter(Msg.Job).U8(Session.LocalId).Str("@fleetari").U8(flags), true);
        }
    }
}
