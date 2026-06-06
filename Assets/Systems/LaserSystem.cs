using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach ONLY to Emitter GameObjects.
    /// 
    /// Refractor fix:
    ///   Instead of using hit.normal (unreliable on thin edges),
    ///   we read the Refractor's transform.forward directly.
    ///   The refractor deflects laser 90° perpendicular to its forward axis.
    ///   This is consistent with the grid math in AISolverAgent.
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

        void Update() => TraceLaser();

        void TraceLaser()
        {
            pts.Clear();
            IsHittingReceiver = false;

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
                    // Standard physics reflection — mirror is flat so normal is reliable
                    dir = Vector3.Reflect(dir, hit.normal);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Refractor")
                {
                    // ── Refractor: use object's transform.forward, NOT hit.normal ──
                    // hit.normal on a thin prism's edge is unreliable.
                    // Instead we derive the deflection plane from the object's own axes.
                    dir = ComputeRefractorDeflection(dir, hit.collider.transform);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Receiver")
                {
                    IsHittingReceiver = true;
                    hit.collider.GetComponent<ReceiverDetector>()?.OnLaserHit();
                    break;
                }
                else
                {
                    break;
                }
            }

            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
        }

        /// <summary>
        /// Computes the refracted direction based on the prism's orientation.
        /// 
        /// The prism has two flat faces (front/back = transform.forward plane).
        /// When laser hits the front face it exits the side face — 90° turn.
        /// The turn direction (left or right) depends on which side the laser
        /// is coming from relative to the prism's right axis.
        ///
        ///   prism.forward = face normal (the flat face the laser hits)
        ///   prism.right   = the axis along which the laser deflects
        ///
        /// Dot product of incoming dir with prism.right tells us which way to turn.
        /// </summary>
        Vector3 ComputeRefractorDeflection(Vector3 incomingDir, Transform prism)
        {
            // Project incoming direction onto the prism's right axis (XZ plane only)
            Vector3 prismRight = prism.right;
            prismRight.y = 0f;
            prismRight.Normalize();

            Vector3 prismForward = prism.forward;
            prismForward.y = 0f;
            prismForward.Normalize();

            float dotRight = Vector3.Dot(incomingDir, prismRight);
            float dotFwd = Vector3.Dot(incomingDir, prismForward);

            // Laser coming along prism.forward axis → deflect along prism.right
            // Laser coming along prism.right axis  → deflect along prism.forward
            // Choose dominant axis of incoming direction
            if (Mathf.Abs(dotFwd) >= Mathf.Abs(dotRight))
            {
                // Incoming mainly along forward/back → exit through right or left side
                return dotRight >= 0 ? prismRight : -prismRight;
            }
            else
            {
                // Incoming mainly along right/left → exit through forward or back face
                return dotFwd >= 0 ? prismForward : -prismForward;
            }
        }

        public void ResetLaser() { }
    }
}
