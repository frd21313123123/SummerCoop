using System;
using UnityEngine;

namespace MWCoop
{
    public static class MscGarage
    {
        // MSC places the portable tools in ITEMS, outside MWC's Props.WorldRoots.
        // Their native names are unique per location. Sibling numbers and the
        // current parent change on pickup, so neither belongs in a network ID.
        public static string Identity(int kind, bool repairShop)
        {
            switch (kind)
            {
                case 0: return "msc:garage:car-jack";
                case 1: return "msc:garage:floor-jack:" + (repairShop ? "fleetari" : "home");
                case 2: return "msc:garage:shop-lift";
                case 3: return "msc:garage:motor-hoist";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }
        public static string Register(Transform root, int kind)
        {
            bool shop = false;
            for (Transform t = root; t != null; t = t.parent) if (t.name == "REPAIRSHOP") shop = true;
            string key = Identity(kind, shop);
            if (kind != 2 && root.GetComponent<Rigidbody>() != null) Props.Adopt(root.gameObject, key);
            return key;
        }
        public static float Rest(int kind, float loaded)
        {
            // Native MSC minima; a jack saved in the raised position is still
            // raised. Using its loaded Y as the minimum hid it from car sync.
            return kind == 0 ? .04f : kind == 1 ? 0f : loaded;
        }
    }
}
