// ============================================================
//  ResupplySpawnMarker — attach to an empty GameObject to mark
//  where a resupply (ammo) station will be placed in-game.
//  Move this object in the scene view to reposition the station.
//  Add more ResupplySpawn_N objects for additional stations.
//  The exporter strips this component before bundling.
// ============================================================
using UnityEngine;

[ExecuteAlways]
public class ResupplySpawnMarker : MonoBehaviour
{
#if UNITY_EDITOR
    static readonly Color GreenSolid = new Color(0.15f, 0.90f, 0.30f, 0.95f);
    static readonly Color GreenFill  = new Color(0.15f, 0.90f, 0.30f, 0.20f);

    void OnDrawGizmos()
    {
        Vector3 pos = transform.position;

        // Box outline — represents the ammo crate footprint
        Gizmos.color = GreenSolid;
        Gizmos.DrawWireCube(pos + Vector3.up * 0.5f, new Vector3(1.0f, 1.0f, 1.0f));

        // Cross on top (ammo/supply symbol)
        Vector3 top = pos + Vector3.up * 1.05f;
        Gizmos.DrawLine(top + Vector3.left  * 0.35f, top + Vector3.right * 0.35f);
        Gizmos.DrawLine(top + Vector3.back  * 0.35f, top + Vector3.forward * 0.35f);

        // Short vertical pole so it's visible at distance
        Gizmos.DrawLine(pos + Vector3.up * 1.0f, pos + Vector3.up * 2.0f);
        Gizmos.DrawWireSphere(pos + Vector3.up * 2.0f, 0.15f);

        // Label
        var style = new GUIStyle();
        style.normal.textColor = GreenSolid;
        style.fontStyle = FontStyle.Bold;
        style.fontSize  = 11;
        UnityEditor.Handles.Label(pos + Vector3.up * 2.4f, gameObject.name, style);
    }

    void OnDrawGizmosSelected()
    {
        // Filled box highlight when selected
        Gizmos.color = GreenFill;
        Gizmos.DrawCube(transform.position + Vector3.up * 0.5f, new Vector3(1.0f, 1.0f, 1.0f));
    }
#endif
}
