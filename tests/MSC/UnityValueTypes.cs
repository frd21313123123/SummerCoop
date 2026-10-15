// CI codec fixture only: no engine behavior or game code is simulated here.
// Compiled only by test-mwcoop-network.ps1, never into the mod or a release.
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; } }
    public struct Quaternion { public float x, y, z, w; public Quaternion(float a, float b, float c, float d) { x = a; y = b; z = c; w = d; } }
}
