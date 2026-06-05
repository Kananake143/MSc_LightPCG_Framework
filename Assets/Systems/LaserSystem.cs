using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach ONLY to Emitter GameObjects.
    /// Fires laser along transform.forward each frame.
    /// Handles Mirror (reflection) and Refractor (90° deflection).
    /// When laser hits Receiver -> destroys Door -> player can pass.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class LaserSystem : MonoBehaviour
    {
        [Header("Laser Settings")]
        public float maxLaserDistance = 50f;
        public int maxBounces = 10;
        public LayerMask obstacleLayer;

        [Header("Visual")]
        public float lineWidth = 0.04f;
        public Color laserColor = new Color(1f, 0.92f, 0.016f); // bright yellow

        private LineRenderer lr;
        private List<Vector3> pts = new List<Vector3>();
        private bool solved = false;

        void Start()
        {
            lr = GetComponent<LineRenderer>();
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.useWorldSpace = true;
            lr.material = new Material(Shader.Find("Unlit/Color")) { color = laserColor };
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void Update()
        {
            if (!solved) TraceLaser();
        }

        void TraceLaser()
        {
            pts.Clear();
            Vector3 pos = transform.position;
            Vector3 dir = transform.forward; // Emitter faces inward from GridVisualizer

            pts.Add(pos);

            for (int b = 0; b < maxBounces; b++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(pos, dir, out hit, maxLaserDistance, obstacleLayer))
                {
                    pts.Add(pos + dir * maxLaserDistance);
                    break;
                }

                pts.Add(hit.point);
                string tag = hit.collider.tag;

                if (tag == "Mirror")
                {
                    // Law of reflection: angle in = angle out
                    dir = Vector3.Reflect(dir, hit.normal);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Refractor")
                {
                    // 90° deflection perpendicular to hit surface normal on XZ plane
                    Vector3 right = Vector3.Cross(Vector3.up, hit.normal).normalized;
                    float dot = Vector3.Dot(dir, right);
                    dir = (dot >= 0 ? right : -right);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Receiver")
                {
                    // Laser reached the receiver — puzzle solved!
                    solved = true;
                    Debug.Log("[LaserSystem] ✓ Receiver hit — puzzle solved!");
                    OpenDoor();
                    break;
                }
                else
                {
                    // Wall or untagged → stop laser here
                    break;
                }
            }

            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
        }

        void OpenDoor()
        {
            // Find and destroy the door to let the player through
            GameObject door = GameObject.FindWithTag("Door");
            if (door != null)
            {
                Destroy(door);
                Debug.Log("[LaserSystem] Door destroyed — exit open!");
            }
            else
            {
                Debug.LogWarning("[LaserSystem] Door not found — check Tag 'Door' is defined.");
            }
        }

        // Allow resetting (e.g. for next level)
        public void ResetLaser()
        {
            solved = false;
        }
    }
}
