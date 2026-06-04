using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach ONLY to each Emitter GameObject.
    /// The Emitter's transform.forward determines firing direction.
    /// GridVisualizer rotates the Emitter to face inward automatically.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class LaserSystem : MonoBehaviour
    {
        [Header("Laser Settings")]
        public float maxLaserDistance = 50f;
        public int maxBounces = 10;
        public LayerMask obstacleLayer;

        [Header("Visual")]
        public float lineWidth = 0.05f;
        public Color laserColor = Color.yellow;

        private LineRenderer lr;
        private List<Vector3> pts = new List<Vector3>();
        private bool solved = false;

        void Start()
        {
            lr = GetComponent<LineRenderer>();
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.material = new Material(Shader.Find("Unlit/Color")) { color = laserColor };
            lr.useWorldSpace = true;
        }

        void Update()
        {
            if (!solved) Trace();
        }

        void Trace()
        {
            pts.Clear();
            Vector3 pos = transform.position;
            Vector3 dir = transform.forward;   // ← Emitter must face inward!
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
                    dir = Vector3.Reflect(dir, hit.normal);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Refractor")
                {
                    // 90° deflection — perpendicular to hit normal projected on XZ plane
                    Vector3 right = Vector3.Cross(Vector3.up, hit.normal).normalized;
                    float dot = Vector3.Dot(dir, right);
                    dir = (dot >= 0 ? right : -right);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Receiver")
                {
                    solved = true;
                    Debug.Log("[LaserSystem] ✓ Receiver hit — puzzle solved!");
                    OpenDoor();
                    break;
                }
                else
                {
                    break; // wall or untagged → stop
                }
            }

            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
        }

        void OpenDoor()
        {
            GameObject door = GameObject.FindWithTag("Door");
            if (door != null)
            {
                Destroy(door);
                Debug.Log("[LaserSystem] Door destroyed — exit open!");
            }
        }
    }
}