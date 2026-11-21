// KAESA StarCraft - ModuleKAESACatchArm
// Marks a Gigazilla arm as a catch surface: the rail transform (local +Z along the rail, +Y up, origin at the middle of
// the rail top) and its length. ModuleKAESACatchPin (on the booster's grid fin pods) looks for these. Written for
// StarCraft v2 (2026-11-20).
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESACatchArm : PartModule
    {
        [KSPField] public string railTransformName = "CatchRail";
        [KSPField] public float railLength = 6f;          // m along the rail's local +Z, centred on the transform

        public Transform Rail { get; private set; }

        public override void OnStart(StartState state)
        {
            Rail = part.FindModelTransform(railTransformName);
            if (Rail == null) Debug.LogWarning("[KAESA] ModuleKAESACatchArm: no transform '" + railTransformName + "' in " + part.partInfo.name);
        }

        public override string GetModuleDisplayName() { return "Catch Rail"; }

        public override string GetInfo()
        {
            return "A rail the booster's catch pins can rest on (" + railLength.ToString("F1") + " m).";
        }
    }
}
