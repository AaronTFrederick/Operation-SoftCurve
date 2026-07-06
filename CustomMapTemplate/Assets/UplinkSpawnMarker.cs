// ============================================================
//  UplinkSpawnMarker — attach to an empty GameObject to mark
//  where the uplink station will be placed in-game.
//  Move this object in the scene view to reposition the uplink.
//  The exporter strips this component before bundling.
// ============================================================
using UnityEngine;

[ExecuteAlways]
public class UplinkSpawnMarker : MonoBehaviour
{
#if UNITY_EDITOR
    static readonly Color GoldSolid = new Color(1.0f, 0.75f, 0.0f, 0.95f);
    static readonly Color GoldFill  = new Color(1.0f, 0.75f, 0.0f, 0.20f);

    void OnDrawGizmos()
    {
        Vector3 pos = transform.position;

        // Ground ring — shows the base of the uplink
        Gizmos.color = GoldSolid;
        Gizmos.DrawWireSphere(pos + Vector3.up * 0.05f, 0.7f);

        // Vertical pole
        Gizmos.DrawLine(pos, pos + Vector3.up * 2.8f);

        // Diamond at the top
        Gizmos.DrawWireSphere(pos + Vector3.up * 2.8f, 0.25f);

        // Label
        var style = new GUIStyle();
        style.normal.textColor = GoldSolid;
        style.fontStyle = FontStyle.Bold;
        style.fontSize  = 11;
        UnityEditor.Handles.Label(pos + Vector3.up * 3.3f, "Uplink Spawn", style);
    }

    void OnDrawGizmosSelected()
    {
        // Filled sphere highlight when selected
        Gizmos.color = GoldFill;
        Gizmos.DrawSphere(transform.position + Vector3.up * 1.4f, 1.0f);
    }
#endif
}
