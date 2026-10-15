using UnityEngine;

namespace MWCoop
{
    public static class MscRespawn
    {
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
