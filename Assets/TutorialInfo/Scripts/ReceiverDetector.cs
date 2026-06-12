using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach to every Receiver prefab.
    /// - Turns green when laser hits this frame
    /// - Opens the door only after sustained hit (sustainTime seconds)
    /// - Resets fully when the level is regenerated
    ///
    /// Fix: OpenDoor now destroys ALL objects tagged "Door" in the scene,
    /// not just the first one found, and guards against acting on a stale
    /// state from the previous level.
    /// </summary>
    public class ReceiverDetector : MonoBehaviour
    {
        [Header("Sustain threshold (seconds laser must hit before door opens)")]
        public float sustainTime = 0.3f;

        private bool doorOpened = false;
        private bool hitThisFrame = false;
        private float hitTimer = 0f;

        private static readonly Color ColorIdle = new Color(0.90f, 0.15f, 0.10f);
        private static readonly Color ColorActive = new Color(0.10f, 0.90f, 0.20f);

        // Called by GridVisualizer after every GenerateLevel() to fully reset state.
        public void ResetState()
        {
            doorOpened = false;
            hitThisFrame = false;
            hitTimer = 0f;
            SetColor(ColorIdle);
        }

        void Update()
        {
            if (hitThisFrame)
            {
                SetColor(ColorActive);
                hitTimer += Time.deltaTime;

                if (!doorOpened && hitTimer >= sustainTime)
                {
                    doorOpened = true;
                    OpenDoor();
                }
            }
            else
            {
                SetColor(ColorIdle);
                hitTimer = 0f;
            }

            hitThisFrame = false;
        }

        /// Called by LaserSystem every frame the laser hits this receiver.
        public void OnLaserHit() => hitThisFrame = true;

        /// True only after the door has been opened by a sustained hit.
        public bool IsDoorOpen => doorOpened;

        void OpenDoor()
        {
            Debug.Log("[ReceiverDetector] Laser sustained — opening door!");

            // Destroy ALL Door-tagged objects to handle multi-door edge cases
            var doors = GameObject.FindGameObjectsWithTag("Door");
            if (doors.Length > 0)
            {
                foreach (var d in doors) Destroy(d);
                Debug.Log($"[ReceiverDetector] {doors.Length} door(s) destroyed.");
            }
            else
            {
                Debug.LogWarning("[ReceiverDetector] No Door found — check Tag 'Door'.");
            }
        }

        void SetColor(Color c)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_Color", c);
                r.SetPropertyBlock(mpb);
            }
        }
    }
}