using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach ONLY to Emitter GameObjects.
    /// - Fires laser along transform.forward each frame
    /// - Notifies ReceiverDetector on the Receiver object when hit
    /// - Does NOT open the door directly — ReceiverDetector handles that
    ///   so the door only opens when the laser is CONTINUOUSLY hitting the Receiver
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
        public Color laserColor = new Color(1f, 0.92f, 0.016f);

        private LineRenderer lr;
        private List<Vector3> pts = new List<Vector3>();

        // Track whether this laser is currently hitting a receiver
        // (checked every frame — door only opens when this stays true)
        public bool IsHittingReceiver { get; private set; }

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
            TraceLaser();
        }

        void TraceLaser()
        {
            pts.Clear();
            IsHittingReceiver = false;  // reset every frame

            Vector3 pos = transform.position;
            Vector3 dir = transform.forward;
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
                    Vector3 right = Vector3.Cross(Vector3.up, hit.normal).normalized;
                    float dot = Vector3.Dot(dir, right);
                    dir = (dot >= 0 ? right : -right);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Receiver")
                {
                    // Mark as hitting — ReceiverDetector component on the Receiver
                    // will open the door when it confirms a sustained hit
                    IsHittingReceiver = true;
                    hit.collider.GetComponent<ReceiverDetector>()?.OnLaserHit();
                    break;
                }
                else
                {
                    break; // wall or untagged
                }
            }

            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
        }

        public void ResetLaser() { /* no longer needed but kept for API compat */ }
    }
}
