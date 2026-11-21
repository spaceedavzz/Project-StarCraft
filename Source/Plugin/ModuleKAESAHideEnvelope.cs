// KAESA StarCraft - ModuleKAESAHideEnvelope
// The Firefly reentry envelope (a mesh named "atmofx_envelope", a bit wider than the hull) must never be seen. Firefly turns
// its renderer off itself, but only in flight when it builds a vessel's effects; in the VAB / SPH (no Firefly there) the
// envelope rendered as a thin black cover round the Super Massive. This turns the renderer off as soon as the part starts,
// in every scene. Firefly still finds it (it looks for the transform and its MeshFilter / MeshRenderer, enabled or not) and
// draws it into its own command buffer, so the reentry plasma is unchanged.
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAHideEnvelope : PartModule
    {
        [KSPField] public string envelopeName = "atmofx_envelope";

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Hide();
        }

        public override void OnAwake()
        {
            base.OnAwake();
            Hide();
        }

        private void Hide()
        {
            if (part == null) return;
            Transform[] ts = part.FindModelTransforms(envelopeName);
            for (int i = 0; i < ts.Length; i++)
            {
                MeshRenderer r = ts[i].GetComponent<MeshRenderer>();
                if (r != null) r.enabled = false;
            }
        }
    }
}
