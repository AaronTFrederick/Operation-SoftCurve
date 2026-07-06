// ============================================================
//  SpawnMarker — attach to an empty GameObject to mark it as
//  a player spawn point. The exporter removes this script from
//  the bundle automatically; the game finds spawn points by name.
// ============================================================
using UnityEngine;

[ExecuteAlways]
public class SpawnMarker : MonoBehaviour
{
    [Tooltip("1 = Team 1 (blue)   2 = Team 2 (red)")]
    public int team = 1;

#if UNITY_EDITOR
    // ── Editor gizmos ────────────────────────────────────────────────────────
    // These only run inside the Unity editor — they're stripped at export time.

    void OnDrawGizmos()
    {
        Color teamColor = (team == 1)
            ? new Color(0.15f, 0.45f, 1.0f, 0.95f)   // blue  — Team 1
            : new Color(1.0f,  0.20f, 0.20f, 0.95f);  // red   — Team 2

        Vector3 pos = transform.position;

        // Vertical pole from ground to above head
        Gizmos.color = teamColor;
        Gizmos.DrawLine(pos, pos + Vector3.up * 2.2f);

        // Sphere at head height
        Gizmos.DrawSphere(pos + Vector3.up * 1.8f, 0.30f);

        // Foot ring so the base position is obvious
        Gizmos.DrawWireSphere(pos + Vector3.up * 0.05f, 0.55f);

        // Forward-direction arrow (shows which way players will face when they spawn)
        Gizmos.color = Color.white;
        Vector3 arrowStart = pos + Vector3.up * 0.9f;
        Gizmos.DrawRay(arrowStart, transform.forward * 1.0f);

        // Label — always visible in the scene view
        var labelStyle = new GUIStyle();
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontStyle = FontStyle.Bold;
        labelStyle.fontSize  = 11;
        UnityEditor.Handles.Label(
            pos + Vector3.up * 2.6f,
            (team == 1 ? "Team 1 Spawn" : "Team 2 Spawn"),
            labelStyle);
    }

    void OnDrawGizmosSelected()
    {
        // Extra ring drawn when this object is selected in the hierarchy.
        Color ring = (team == 1)
            ? new Color(0.15f, 0.45f, 1.0f, 0.25f)
            : new Color(1.0f,  0.20f, 0.20f, 0.25f);
        Gizmos.color = ring;
        Gizmos.DrawSphere(transform.position + Vector3.up * 0.9f, 1.1f);
    }
#endif
}
