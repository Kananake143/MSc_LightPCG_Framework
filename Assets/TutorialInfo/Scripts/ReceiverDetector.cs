using UnityEngine;

namespace LightPCG.Systems
{
    /// <summary>
    /// Attach this to every Receiver prefab.
    /// - Changes colour to green when laser hits it THIS frame
    /// - Opens the door only when laser is sustained (not just a flash)
    /// - Resets to red when laser stops hitting
    /// </summary>
    public class ReceiverDetector : MonoBehaviour
    {
        [Header("Sustain threshold (seconds laser must hit before door opens)")]
        public float sustainTime = 0.3f;

        private bool  doorOpened   = false;
        private bool  hitThisFrame = false;
        private float hitTimer     = 0f;

        // Colours
        private static readonly Color ColorIdle   = new Color(0.90f, 0.15f, 0.10f); // red
        private static readonly Color ColorActive = new Color(0.10f, 0.90f, 0.20f); // green

        void Update()
        {
            if (hitThisFrame)
            {
                // Laser is hitting this frame
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
                // Laser not hitting — reset
                SetColor(ColorIdle);
                hitTimer = 0f;
                // Note: door stays open once opened
            }

            // Reset flag — will be set again next frame if laser still hits
            hitThisFrame = false;
        }

        /// Called by LaserSystem every frame the laser hits this receiver
        public void OnLaserHit()
        {
            hitThisFrame = true;
        }

        void OpenDoor()
        {
            Debug.Log("[ReceiverDetector] Laser sustained — opening door!");
            GameObject door = GameObject.FindWithTag("Door");
            if (door != null)
            {
                Destroy(door);
                Debug.Log("[ReceiverDetector] Door destroyed — exit open!");
            }
            else
            {
                Debug.LogWarning("[ReceiverDetector] Door not found — check Tag 'Door'.");
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
