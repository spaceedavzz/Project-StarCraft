// KAESA StarCraft - ModuleKAESAFlap
// Starship-style flap on top of the stock control surface:
//  - one-sided travel: 0 deg = fully out (max drag) .. maxFold = folded toward the leeward side;
//  - a rest angle the flap holds with no input; pitch / yaw / roll input moves it around that angle;
//  - modes: Control (rest + input), Retracted (maxFold), Extended (0), with action groups;
//  - aero follows the real flap: the lift base is the static "flapPivot" transform whose X axis is the hinge, so the
//    stock lift rotation (AngleAxis(deflection, baseTransform.right)) and the visual rotation of ctrlSurface
//    (AngleAxis(deflection, right) * neutral, in the pivot's frame) are the same rotation.
// Input mixing is our own: the stock one takes Dot(input axis, hinge axis), and a Starship flap's hinge runs along the
// hull, so pitch and yaw always came out as zero (the flaps never moved with WASD). Here each flap works out the force
// CHANGE it makes when it opens from the rest angle (flat plate: normal force ~ cos(fold), along the plate normal; the
// difference between "opened by the range" and "folded by the range": mostly more drag against the airflow plus a little
// outward side force, because an opened flap loses the inward lift it had while folded), the torque of that change about
// the CoM, and opens or folds by how well that torque matches the torque the pilot / SAS asks for (same axes and signs
// as a reaction wheel). With four flaps that gives Starship's scheme by itself: pitch = front vs back, roll = left vs
// right, yaw = the diagonals.
// SAS / autopilots: the stock potential-torque report (ITorqueProvider) would be ~0 for these flaps for the same reason,
// so SAS tuned itself to the reaction wheel only and over-corrected; this module re-implements ITorqueProvider with the
// real torque (dynamic pressure x plate area x the force change above).
// The stock module still does the lift / drag forces, authority limiter, ignore pitch / yaw / roll and actuator speed.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[assembly: KSPAssembly("KAESA.StarCraft", 1, 1)]
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)]

namespace KAESA.StarCraft
{
    // MechJeb (and anything else that asks a ModuleControlSurface directly instead of through ITorqueProvider) calls the
    // stock, sealed ModuleControlSurface.GetPotentialTorque, which is ~0 for these flaps. A Harmony prefix hands those
    // callers the flap's real torque (ModuleKAESAFlap.GetPotentialTorque below); other control surfaces are untouched.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KAESAStarCraftPatcher : MonoBehaviour
    {
        private void Awake()
        {
            try
            {
                var harmony = new HarmonyLib.Harmony("kaesa.starcraft.flaps");
                MethodInfo orig = typeof(ModuleControlSurface).GetMethod("GetPotentialTorque", BindingFlags.Public | BindingFlags.Instance, null,
                    new Type[] { typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType() }, null);
                MethodInfo pre = typeof(KAESAStarCraftPatcher).GetMethod("TorquePrefix", BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(orig, new HarmonyLib.HarmonyMethod(pre));
                Debug.Log("[KAESA] ModuleControlSurface.GetPotentialTorque patched for ModuleKAESAFlap (MechJeb / autopilots)");
            }
            catch (Exception e)
            {
                Debug.LogError("[KAESA] Harmony patch for the flaps failed: " + e);
            }
        }

        public static bool TorquePrefix(ModuleControlSurface __instance, ref Vector3 pos, ref Vector3 neg)
        {
            IKAESAControl f = __instance as IKAESAControl;       // flaps and grid fins
            if (f == null) return true;
            f.GetPotentialTorque(out pos, out neg);
            return false;
        }
    }

    public class ModuleKAESAFlap : ModuleControlSurface, ITorqueProvider, IKAESAControl
    {
        // static transform at the hinge: X = hinge axis, transformDir axis = plate normal at fold 0
        [KSPField] public string pivotTransformName = "flapPivot";
        // full travel (deg) and which sign of the stock deflection folds the flap toward the leeward side
        [KSPField] public float maxFold = 78f;
        [KSPField] public float foldSign = -1f;
        // centre of pressure of the plate in ctrlSurface's frame (lever arm for the torque)
        [KSPField] public Vector3 copOffset = new Vector3(0f, 0.93f, 0f);
        // (kept for old configs; the whole-vessel allocation below replaced the per-flap gain)
        [KSPField] public float controlGain = 5f;
        // plate area per unit of deflectionLiftCoeff (stock: 1 lift unit ~ 3.52 m2) and flat-plate normal force coefficient
        [KSPField] public float areaPerLiftCoeff = 3.52f;
        [KSPField] public float plateCn = 1.2f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Rest angle", guiUnits = "°",
            groupName = "kaesaFlap", groupDisplayName = "Starship flap")]
        [UI_FloatRange(minValue = 0f, maxValue = 78f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float restFold = 39f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Flap mode", groupName = "kaesaFlap",
            groupDisplayName = "Starship flap")]
        [UI_ChooseOption(options = new string[] { "Control", "Retracted", "Extended" }, scene = UI_Scene.All)]
        public string flapMode = "Control";

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Flap angle", guiUnits = "°", guiFormat = "F0",
            groupName = "kaesaFlap", groupDisplayName = "Starship flap")]
        public float currentFold = -1f;

        // -1 (fold) .. +1 (open): what the pilot / SAS input asks of this flap
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Flap command", guiFormat = "F2",
            groupName = "kaesaFlap", groupDisplayName = "Starship flap")]
        public float flapCommand = 0f;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Transform pivot = part.FindModelTransform(pivotTransformName);
            if (pivot != null)
                baseTransform = pivot;
            else
                Debug.LogWarning("[KAESA] ModuleKAESAFlap: transform '" + pivotTransformName + "' not found in " + part.partInfo.name);
            SetRange(Fields["restFold"].uiControlEditor as UI_FloatRange);
            SetRange(Fields["restFold"].uiControlFlight as UI_FloatRange);
            restFold = Mathf.Clamp(restFold, 0f, maxFold);
            currentFold = TargetFold(0f);
            Apply();
        }

        private void SetRange(UI_FloatRange r)
        {
            if (r != null) r.maxValue = maxFold;
        }

        // act = how far (deg) to open the flap past the rest angle (negative folds it further)
        private float TargetFold(float act)
        {
            if (flapMode == "Retracted") return maxFold;
            if (flapMode == "Extended") return 0f;
            return Mathf.Clamp(restFold - act, 0f, maxFold);
        }

        protected override void CtrlSurfaceUpdate(Vector3 vel)
        {
            flapCommand = flapMode == "Control" ? ControlCommand(vel) : 0f;
            action = flapCommand * ctrlSurfaceRange * authorityLimiter * 0.01f;
            Step(TimeWarp.fixedDeltaTime);
        }

        protected override void CtrlSurfaceEditorUpdate(Vector3 CoM)
        {
            flapCommand = 0f;
            action = 0f;
            Step(Time.deltaTime);
        }

        // normal force on the plate per unit (q * area * Cn) with the air coming from the windward side, at a given fold
        private Vector3 PlateForce(float foldDeg, Vector3 n0, Vector3 o)
        {
            float f = foldDeg * Mathf.Deg2Rad, c = Mathf.Cos(f), sn = Mathf.Sin(f);
            return -(n0 * c + o * sn) * c;
        }

        // force change (per unit q * area * Cn) between the flap opened and folded by the control range around the rest
        // angle; n0 = plate normal at fold 0 (windward), o = chord at fold 0 (outward); flipped if the air comes from leeward
        private Vector3 OpenForceChange(Vector3 vel)
        {
            Vector3 n0 = baseTransform.forward, o = baseTransform.up;
            float s = 1f;                                        // no airflow (on the pad): assume the belly flop
            if (vel.sqrMagnitude > 1f)
            {
                float d = Vector3.Dot(vel.normalized, n0);
                if (Mathf.Abs(d) > 0.05f) s = Mathf.Sign(d);
            }
            float rest = Mathf.Clamp(restFold, 0f, maxFold), range = Mathf.Abs(ctrlSurfaceRange);
            float fo = Mathf.Clamp(rest - range, 0f, maxFold), ff = Mathf.Clamp(rest + range, 0f, maxFold);
            return s * (PlateForce(fo, n0, o) - PlateForce(ff, n0, o));
        }

        // ---------------------------------------------------------------- whole-vessel allocation
        // Every KAESA control surface on the vessel (flaps, grid fins) works out the same allocation (KAESAControlAllocator):
        // B = torque (vessel axes) per unit command of each surface; for each axis the minimum-norm set of commands that makes
        // a PURE torque on that axis, scaled so the busiest surface uses its full travel. This decouples the axes even though
        // a flap's opening makes a mix of pitch, roll and yaw (the forward flaps face a little outward, so opening both left
        // flaps rolls AND yaws; the diagonals cancel that).
        private readonly List<IKAESAControl> flaps = new List<IKAESAControl>();
        private int flapsPartCount = -1;

        private void RefreshFlaps()
        {
            if (vessel.parts.Count == flapsPartCount && flaps.Count > 0) return;
            KAESAControlAllocator.Collect(vessel, flaps);
            flapsPartCount = vessel.parts.Count;
        }

        private bool Active
        {
            get { return flapMode == "Control" && ctrlSurface != null && baseTransform != null && part != null && !part.ShieldedFromAirstream; }
        }

        public bool KaesaActive { get { return Active; } }

        // torque per unit command (opening = +1, full swing), vessel axes (x pitch, y roll, z yaw), x plate area
        public Vector3 UnitTorque(Vector3 com, Transform rt)
        {
            if (!Active) return Vector3.zero;
            Vector3 vel = part.Rigidbody != null ? part.Rigidbody.GetPointVelocity(ctrlSurface.position) + Krakensbane.GetFrameVelocityV3f() : Vector3.zero;
            Vector3 r = ctrlSurface.TransformPoint(copOffset) - com;
            return rt.InverseTransformDirection(Vector3.Cross(r, OpenForceChange(vel)) * (deflectionLiftCoeff * areaPerLiftCoeff));
        }

        private float ControlCommand(Vector3 vel)
        {
            if (vessel == null) return 0f;
            RefreshFlaps();
            return KAESAControlAllocator.Command(vessel, flaps, this, ignorePitch, ignoreRoll, ignoreYaw);
        }

        // SAS / autopilots ask every ITorqueProvider how much torque it can make (vessel axes: x pitch, y roll, z yaw).
        // Each surface reports an equal share of what the whole set makes for a pure command on each axis.
        public new void GetPotentialTorque(out Vector3 pos, out Vector3 neg)
        {
            pos = neg = Vector3.zero;
            if (!HighLogic.LoadedSceneIsFlight || !Active || vessel == null) return;
            RefreshFlaps();
            KAESAControlAllocator.PotentialTorque(vessel, flaps, this, part.dynamicPressurekPa, plateCn, authorityLimiter,
                ignorePitch, ignoreRoll, ignoreYaw, out pos, out neg);
        }

        private void Step(float dt)
        {
            if (currentFold < 0f) currentFold = TargetFold(0f);
            currentFold = Mathf.MoveTowards(currentFold, TargetFold(action), Mathf.Max(actuatorSpeed, 1f) * dt);
            Apply();
        }

        private void Apply()
        {
            deflection = foldSign * currentFold;
            if (ctrlSurface != null)
                ctrlSurface.localRotation = Quaternion.AngleAxis(deflection, Vector3.right) * neutral;
        }

        [KSPAction("Flaps: retract")]
        public void KaesaRetract(KSPActionParam p) { flapMode = "Retracted"; }

        [KSPAction("Flaps: extend")]
        public void KaesaExtend(KSPActionParam p) { flapMode = "Extended"; }

        [KSPAction("Flaps: control")]
        public void KaesaControl(KSPActionParam p) { flapMode = "Control"; }

        [KSPAction("Flaps: toggle retract")]
        public void KaesaToggleRetract(KSPActionParam p) { flapMode = flapMode == "Retracted" ? "Control" : "Retracted"; }

        public override string GetModuleDisplayName()
        {
            return "Starship Flap";
        }

        public override string GetInfo()
        {
            return "Starship-style flap: 0° (out) to " + maxFold.ToString("F0") + "° (folded), rest angle " +
                   restFold.ToString("F0") + "°, ±" + ctrlSurfaceRange.ToString("F0") + "° of control around it.\n\n" +
                   base.GetInfo();
        }
    }
}
