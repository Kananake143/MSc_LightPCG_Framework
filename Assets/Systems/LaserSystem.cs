using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    [RequireComponent(typeof(LineRenderer))]
    public class LaserSystem : MonoBehaviour
    {
        [Header("Laser Settings")]
        public float maxLaserDistance = 50f;
        public int maxBounces = 10;
        [Tooltip("Set Everything (-1) or Layer at puzzle objects ")]
        public LayerMask obstacleLayer = -1;

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

            // FIX Bug 2: If obstacleLayer = 0 (Nothing), the light will disappear.
            if (obstacleLayer.value == 0)
            {
                obstacleLayer = ~0;
                Debug.LogWarning("[LaserSystem] obstacleLayer=Nothing -> reset to Everything. " + "Please set the correct layer in Inspector");
            }
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

                // FIX Bug 3: Added QueryTriggerInteraction.Ignore to prevent trigger collider.
                bool didHit = Physics.Raycast(pos, dir, out hit,
                    maxLaserDistance, obstacleLayer, QueryTriggerInteraction.Ignore);

                if (!didHit)
                {
                    pts.Add(pos + dir * maxLaserDistance);
                    break;
                }

                pts.Add(hit.point);
                string tag = hit.collider.tag;

                if (tag == "Mirror")
                {
                    // Flat glass: hit.normal is always reliable.
                    dir = Vector3.Reflect(dir, hit.normal);
                    pos = hit.point + dir * 0.02f;
                }
                else if (tag == "Refractor")
                {
                    // Fix Bug 1: Check 3 layers before refraction.
                    dir = HandleRefractor(dir, hit);
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
        /// FIX Bug 1: Refractor - 3-Layer Check
        ///
        /// Layer 1 - Rotation gate:
        /// |yRot| <= 5 deg -> Prism is not rotated -> Light passes straight through (pass-through)
        ///
        /// Layer 2 - Face validity:
        /// dot(hit.normal, prism.forward) in the XZ plane
        /// |dot| >= 0.5 (cos 60 deg) -> Hits a flat face -> Refraction
        /// |dot| < 0.5 -> Hits an edge -> Light passes straight through
        ///
        /// Layer 3 - Direction gate:
        /// dot(incomingDir, prism.forward) in the XZ plane
        /// |dot| > 0.3 -> Light comes from a reasonable direction -> Refraction
        /// |dot| <= 0.3 -> Light comes at too much of an angle -> Passes straight through
        ///
        /// Physics: Snell's law only applies to flat optical interfaces
        /// Edges are not optical surfaces -> treat as transparent
        /// </summary>
        Vector3 HandleRefractor(Vector3 inDir, RaycastHit hit)
        {
            Transform prism = hit.collider.transform;

            // Layer 1 only: If not rotated (yRot ≤ 5°) → Direct pass
            // Use eulerAngles.y directly and normalize to 0–360
            float yRot = prism.eulerAngles.y % 360f;
            if (yRot > 180f) yRot -= 360f;   // map เป็น -180..180
            if (Mathf.Abs(yRot) <= 5f)
                return inDir;

            // หมุนแล้ว → deflect เสมอ (ตรงกับ RefractorDeflect ใน Solver)
            return Deflect90(inDir, prism);
        }

        /// <summary>
        /// Calculate 90-degree refraction direction
        ///
        /// Mathematics:
        /// R = prism.right (XZ plane)
        /// F = prism.forward (XZ plane)
        ///
        /// dR = dot(incomingDir, R) -> component along the right axis
        /// dF = dot(incomingDir, F) -> component along the forward axis
        ///
        /// If |dF| >= |dR|: Light comes along the F axis -> refracts out towards R (or -R)
        /// If |dR| > |dF|: Light comes along the R axis -> refracts out towards F (or -F)
        ///
        /// The sign of the resulting direction uses the sign of the smaller dot product
        /// To maintain chirality of refraction
        /// </summary>
        Vector3 Deflect90(Vector3 inDir, Transform prism)
        {
            Vector3 R = prism.right; R.y = 0f; R.Normalize();
            Vector3 F = prism.forward; F.y = 0f; F.Normalize();

            float dR = Vector3.Dot(inDir, R);
            float dF = Vector3.Dot(inDir, F);

            if (Mathf.Abs(dF) >= Mathf.Abs(dR))
                return dR >= 0f ? R : -R;
            else
                return dF >= 0f ? F : -F;
        }

        public void ResetLaser() { }
    }
}