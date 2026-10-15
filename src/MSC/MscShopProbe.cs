using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    public static class MscShopProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static float cash;
        static bool At(int n, float t, float at) { return t >= at && steps.Add(n); }
        static float Cash { get { return FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value; } }
        static PlayMakerFSM Fsm(string path, string name) { return Game.FsmOn(Game.FindAny(path), name); }
        static void Hour(int hour)
        {
            var f = Fsm("MAP/SUN/Pivot/SUN", "Color"); f.FsmVariables.GetFsmInt("Time").Value = hour; f.SendEvent("WAKEUP");
        }
        static void Active(string path)
        {
            GameObject go = Game.FindAny(path); for (Transform p = go.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
        }
        public static void Update(float t, string channel)
        {
            if (At(0, t, 180) && Session.IsHost) Hour(20);
            if (t >= 183 && t < 211) { Active("STORE/LOD/ActivateBar/OrderList"); Active("STORE/LOD/GFX_Pub/PubCashRegister/Register"); }
            if (At(1, t, 185)) cash = Cash;
            if (At(2, t, 187) && !Session.IsHost) Game.SetState(Fsm("STORE/LOD/ActivateBar/OrderList/1", "Buy"), "Check money");
            if (At(3, t, 200))
            {
                float expected = Session.IsHost ? 0 : 8;
                MscRegressionProbe.Record(channel, "pub-guest-payment", Mathf.Abs((cash - Cash) - expected) < .01f, "Before=" + cash + ";After=" + Cash);
                cash = Cash;
                Products(channel, "pub-guest-products", 1);
            }
            if (At(4, t, 202) && Session.IsHost) Game.SetState(Fsm("STORE/LOD/ActivateBar/OrderList/1", "Buy"), "Check money");
            if (At(5, t, 214))
            {
                float expected = Session.IsHost ? 8 : 0;
                MscRegressionProbe.Record(channel, "pub-host-payment", Mathf.Abs((cash - Cash) - expected) < .01f, "Before=" + cash + ";After=" + Cash);
                Products(channel, "pub-host-products", 2);
            }
            if (At(6, t, 215) && Session.IsHost) Hour(10);
            if (t >= 220 && t < 241) Active("STORE/LOD/ShopFunctions/BagCreator");
            if (At(7, t, 223))
            {
                cash = Cash;
                if (Session.IsHost) { var f = Fsm("STORE/StoreCashRegister/Register", "Data"); f.FsmVariables.GetFsmInt("QPizza").Value = 1; f.FsmVariables.GetFsmFloat("PriceTotal").Value = 24; }
            }
            if (At(8, t, 225) && !Session.IsHost)
            {
                var f = Fsm("STORE/StoreCashRegister/Register", "Data");
                f.FsmVariables.GetFsmInt("QSausages").Value = 1;
                f.FsmVariables.GetFsmFloat("Total").Value = 10.95f;
                Game.SetState(f, "Check money");
            }
            if (At(9, t, 239))
            {
                float expected = Session.IsHost ? 0 : 10.95f;
                int bags = 0;
                foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) if (body.name.StartsWith("shopping bag", StringComparison.Ordinal) || Props.ItemId(body.gameObject).StartsWith("shoppingbag", StringComparison.Ordinal)) bags++;
                MscRegressionProbe.Record(channel, "store-guest-payment", Mathf.Abs((cash - Cash) - expected) < .02f && bags > 0, "Before=" + cash + ";After=" + Cash + ";Bags=" + bags);
                if (Session.IsHost) MscRegressionProbe.Record(channel, "store-other-cart", Fsm("STORE/StoreCashRegister/Register", "Data").FsmVariables.GetFsmInt("QPizza").Value == 1, "Pizza=" + Fsm("STORE/StoreCashRegister/Register", "Data").FsmVariables.GetFsmInt("QPizza").Value);
            }
        }
        static void Products(string channel, string name, int expected)
        {
            var ids = new List<string>();
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                string id = Props.ItemId(body.gameObject);
                if (id.StartsWith("beer", StringComparison.Ordinal) && (id.Contains("-0-") || id.Contains("-1-"))) ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            MscRegressionProbe.Record(channel, name, ids.Count == expected, "Ids=" + string.Join(",", ids.ToArray()));
        }
    }
}
