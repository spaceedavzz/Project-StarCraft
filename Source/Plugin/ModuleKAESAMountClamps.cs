// KAESA StarCraft - ModuleKAESAMountClamps
// Launch Mount (2026-11-20): plays the mount's "ClampsRetract" animation - the 20 hold-down clamp jaws swing up and back
// and the two booster quick disconnect arms swing away - when the part's stage fires (the same stage as its decoupler,
// which lets the booster go). Retract / Extend from the part menu or action groups too (e.g. to check them in the VAB).
// The pose is saved (retracted) and restored on load without replaying.
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAMountClamps : PartModule
    {
        private const string GRP = "kaesaMountClamps";
        private const string GRP_NAME = "Launch mount";

        [KSPField] public string animationName = "ClampsRetract";
        [KSPField] public float animSpeed = 1f;

        [KSPField(isPersistant = true)] public bool retracted = false;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Clamps", groupName = GRP, groupDisplayName = GRP_NAME)]
        public string status = "Holding";

        private Animation anim;

        public override void OnStart(StartState state)
        {
            Animation[] a = part.FindModelAnimators(animationName);
            if (a != null && a.Length > 0)
            {
                anim = a[0];
                AnimationState s = anim[animationName];
                if (s != null) s.wrapMode = WrapMode.ClampForever;
            }
            SetPose(retracted ? 1f : 0f);
        }

        // staging: the part's stage fired (its ModuleDecouple releases the booster in the same stage)
        public override void OnActive()
        {
            if (!retracted) Play(true);
        }

        private void SetPose(float t)
        {
            status = t > 0.5f ? "Retracted" : "Holding";
            if (anim == null) return;
            AnimationState s = anim[animationName];
            if (s == null) return;
            s.normalizedTime = t; s.speed = 0f; s.weight = 1f; s.enabled = true;
            anim.Play(animationName);
            anim.Sample();
        }

        private void Play(bool retract)
        {
            retracted = retract;
            status = retract ? "Retracting" : "Extending";
            if (anim == null || !HighLogic.LoadedSceneIsFlight) { SetPose(retract ? 1f : 0f); return; }
            AnimationState s = anim[animationName];
            if (s == null) { SetPose(retract ? 1f : 0f); return; }
            s.speed = retract ? animSpeed : -animSpeed;
            if (retract && s.normalizedTime >= 1f) s.normalizedTime = 0f;
            if (!retract && s.normalizedTime <= 0f) s.normalizedTime = 1f;
            s.enabled = true;
            anim.Play(animationName);
        }

        public void Update()
        {
            if (anim == null || !HighLogic.LoadedSceneIsFlight) return;
            if (status == "Retracting" || status == "Extending")
            {
                AnimationState s = anim[animationName];
                if (s == null) return;
                if (retracted && s.normalizedTime >= 1f) status = "Retracted";
                else if (!retracted && s.normalizedTime <= 0f) status = "Holding";
            }
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Retract clamps", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void RetractClamps() { Play(true); }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Extend clamps", groupName = GRP, groupDisplayName = GRP_NAME)]
        public void ExtendClamps() { Play(false); }

        [KSPAction("Retract clamps")] public void ActRetract(KSPActionParam p) { Play(true); }
        [KSPAction("Extend clamps")] public void ActExtend(KSPActionParam p) { Play(false); }

        public override string GetModuleDisplayName() { return "Hold-down clamps"; }

        public override string GetInfo()
        {
            return "20 hold-down clamps and 2 booster quick disconnects that swing out of the way when the mount's stage fires " +
                   "(with the booster release). Retract / extend them from the part menu or action groups too.";
        }
    }
}
