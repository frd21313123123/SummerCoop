using System;
using HutongGames.PlayMaker;
namespace MWCoop
{
    public static class MscShop
    {
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            string path = Recon.Path(f.transform);
            return path == "STORE/LOD/GFX_Pub/PubCashRegister/Register"
                || path == "STORE/StoreCashRegister/Register" || path == "STORE/LOD/ShopFunctions/BagCreator"
                || path.StartsWith("STORE/LOD/ActivateBar/OrderList/", StringComparison.Ordinal);
        }
    }
}
