// KAESA StarCraft - ModuleKAESAFlap (v3, written 2026-11-26)
//
// A Starship flap built on KSP's own stock control surface (ModuleControlSurface): the stock code still makes the lift and
// drag forces, the actuator speed, the authority limiter, ignore pitch / yaw / roll and the deploy switch, exactly as players
// know them. This module adds what a Starship flap needs on top:
//  - one-sided travel: 0 deg = fully out (most drag) .. maxFold = folded against the leeward side;
//  - a rest angle the flap holds with no input; steering moves it around that by up to ctrlSurfaceRange;
//  - modes: Control (rest + steering), Retracted (folded), Extended (fully out), with action groups;
//  - steering through the vessel-wide KAESA allocation (KAESAControl.cs), shared with the grid fins. The stock mixing reads
//    how the input axis lines up with the hinge, and a Starship flap's hinge runs along the hull, so stock pitch / yaw come
//    out as ~0. Here each flap reports the torque it makes when it opens from "folded by the range" to "opened by the
//    range" (simple flat-plate estimate: normal force ~ cos(fold), air on the belly, area = deflectionLiftCoeff x 3.52 m2),
//    and gets its share of pitch / roll / yaw: four flaps give pitch = front against back, roll = left against right,
//    yaw = the diagonals;
//  - a real torque report for SAS (ITorqueProvider), and through a small Harmony prefix for MechJeb, which asks the stock
//    control surface directly.
//
// History:
//  - v1 (2026-11-14) followed the idea of SEP's ModuleSEPControlSurface. The SEP team pointed out in 2026-11 that it was too
//    close to their code.
//  - On 2026-11-26 it was rewritten twice:
//    - first as its own module with its own aerodynamics (kept in KAESA_backup/flap_propria_2026-11-26);
//    - then, by the user's choice of the safer path, as this new file on the stock control surface again, so the flight feel
//      stays the one already tested.
//    - The steering estimate is v1's on purpose (the user asked for the old behaviour, rewritten, with its bugs fixed).
//      Fixes are marked "fix:" below:
//      - plate Cn missing from the estimate;
//      - authority limiter outside the allocation;
//      - stale surface list after a dock / undock.
//  - SEP's code was not looked at. v1 was read for the model's axis conventions, the names that crafts save (restFold,
//    flapMode, the actions) and its steering estimate.
//  - SEP's DLL was compared afterwards by structure only: SEP's flap derives from ModuleLiftingSurface and has its own
//    deploy / flip / landing angles; this one derives from ModuleControlSurface.
//
// Model axes ("flapPivot" transform): X = hinge, Z = plate normal at fold 0 (out to windward), Y = chord at fold 0. The flap
// transform (ctrlSurface) is a child of the pivot. The pivot is handed to the stock code as baseTransform, so the stock lift
// turns with the flap about the real hinge.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KAESA.StarCraft
{
    // MechJeb calls ModuleControlSurface.GetPotentialTorque directly (not through ITorqueProvider), and that stock method is
    // ~0 for a hinge along the hull. This prefix answers for KAESA surfaces only; every other control surface is untouched.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KAESAFlapTorquePatch : MonoBehaviour
    {
        private void Awake()
        {
            try
            {
                MethodInfo target = typeof(ModuleControlSurface).GetMethod("GetPotentialTorque", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType() }, null);
                MethodInfo prefix = typeof(KAESAFlapTorquePatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
                new HarmonyLib.Harmony("kaesa.starcraft.flaptorque").Patch(target, new HarmonyLib.HarmonyMethod(prefix));
            }
            catch (Exception e)
            {
                Debug.LogError("[KAESA] flap torque patch not applied (MechJeb will see the stock value): " + e.Message);
            }
        }

        public static bool Prefix(ModuleControlSurface __instance, ref Vector3 pos, ref Vector3 neg)
        {
            IKAESAControl k = __instance as IKAESAControl;
            if (k == null) return true;
            k.GetPotentialTorque(out pos, out neg);
            return false;
        }
    }

    public class ModuleKAESAFlap : ModuleControlSurface, ITorqueProvider, IKAESAControl
    {
        private const string GRP = "kaesaFlap";
        private const string GRP_NAME = "Starship flap";
        private const float M2_PER_LIFT_UNIT = 3.52f;    // plate area per unit of deflectionLiftCoeff (the cfgs' convention)

        [KSPField] public string pivotTransformName = "flapPivot";
        [KSPField] public float maxFold = 90f;           // deg, folded end of the travel
        [KSPField] public float foldSign = -1f;          // which sign of the stock deflection folds the flap
        [KSPField] public Vector3 copOffset = new Vector3(0f, 0.93f, 0f);   // plate centre in ctrlSurface's frame (lever arm)
        [KSPField] public float plateCn = 1.2f;          // flat-plate normal-force coefficient, for the steering estimate only

        // saved in crafts (names kept from v1)
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Rest angle", guiUnits = "°", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_FloatRange(minValue = 0f, maxValue = 90f, stepIncrement = 1f, scene = UI_Scene.All)]
        public float restFold = 39f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Flap mode", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_ChooseOption(options = new string[] { "Control", "Retracted", "Extended" }, scene = UI_Scene.All)]
        public string flapMode = "Control";

        [KSPField(guiActive = true, guiName = "Flap angle", guiUnits = "°", guiFormat = "F0", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float currentFold = 0f;

        [KSPField(guiActive = true, guiName = "Flap command", guiFormat = "F2", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float flapCommand = 0f;

        private Transform pivot;
        private float fold = -1f;
        private readonly List<IKAESAControl> controls = new List<IKAESAControl>();
        private int controlsPartCount = -1;
        private Vessel controlsVessel;

        // ================================================================ setup
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            pivot = part.FindModelTransform(pivotTransformName);
            if (pivot != null) baseTransform = pivot;
            else Debug.LogWarning("[KAESA] ModuleKAESAFlap: no '" + pivotTransformName + "' in " + part.partInfo.name);
            UI_FloatRange r = Fields["restFold"].uiControlEditor as UI_FloatRange; if (r != null) r.maxValue = maxFold;
            r = Fields["restFold"].uiControlFlight as UI_FloatRange; if (r != null) r.maxValue = maxFold;
            restFold = Mathf.Clamp(restFold, 0f, maxFold);
            fold = Target(0f);
            Show();
        }

        // ================================================================ travel
        // fold for a steering command: +1 opens by the whole range (less fold), -1 folds by it
        private float Target(float cmd)
        {
            if (flapMode == "Retracted") return maxFold;
            if (flapMode == "Extended") return 0f;
            return Mathf.Clamp(restFold - cmd * Mathf.Abs(ctrlSurfaceRange) * authorityLimiter * 0.01f, 0f, maxFold);
        }

        // the stock code reads "deflection" for its lift; the flap model follows the same rotation about the hinge
        private void Show()
        {
            deflection = foldSign * fold;
            currentFold = fold;
            if (ctrlSurface != null) ctrlSurface.localRotation = Quaternion.AngleAxis(deflection, Vector3.right) * neutral;
        }

        protected override void CtrlSurfaceUpdate(Vector3 vel)
        {
            flapCommand = KaesaActive && vessel != null
                ? KAESAControlAllocator.Command(vessel, Controls(), this, ignorePitch, ignoreRoll, ignoreYaw) : 0f;
            action = flapCommand * ctrlSurfaceRange * authorityLimiter * 0.01f;     // stock field, in degrees like the stock surface's
            if (fold < 0f) fold = Target(0f);
            fold = Mathf.MoveTowards(fold, Target(flapCommand), Mathf.Max(actuatorSpeed, 1f) * TimeWarp.fixedDeltaTime);
            Show();
        }

        protected override void CtrlSurfaceEditorUpdate(Vector3 CoM)
        {
            flapCommand = 0f; action = 0f;
            fold = Mathf.MoveTowards(fold < 0f ? Target(0f) : fold, Target(0f), Mathf.Max(actuatorSpeed, 1f) * Time.deltaTime);
            Show();
        }

        // ================================================================ allocation (shared with the grid fins)
        public bool KaesaActive
        {
            get { return flapMode == "Control" && pivot != null && ctrlSurface != null && part != null && !part.ShieldedFromAirstream; }
        }

        // the list of KAESA surfaces, built again when the vessel or its part count changes (fix: v1 only watched the count,
        // so a dock / undock that kept the count kept a stale list)
        private List<IKAESAControl> Controls()
        {
            if (vessel != controlsVessel || vessel.parts.Count != controlsPartCount || controls.Count == 0)
            {
                KAESAControlAllocator.Collect(vessel, controls);
                controlsPartCount = vessel.parts.Count;
                controlsVessel = vessel;
            }
            return controls;
        }

        // flat-plate normal force per unit (q x area x Cn) at a fold, air hitting the windward face: the plate normal at that
        // fold is n0 cos f + o sin f (n0 = windward normal at fold 0, o = chord out from the hinge), and a plate's normal force
        // goes roughly with the cosine of how far it is turned from facing the air
        private static Vector3 PlatePush(float foldDeg, Vector3 n0, Vector3 o)
        {
            float f = foldDeg * Mathf.Deg2Rad, c = Mathf.Cos(f);
            return -(n0 * c + o * Mathf.Sin(f)) * c;
        }

        // vessel-axis torque per Pa of dynamic pressure (N m / Pa, the same unit as the grid fins'): the force change from
        // "folded by the range" to "opened by the range" around the rest angle, at the plate's centre. Same simple estimate
        // as before; fixes: the plate Cn is now in (v1 left it out here, so flaps and grid fins were mixed 1.2x off), and the
        // range follows the authority limiter like the real travel does.
        // Air from the leeward side flips the sign; with no airflow (on the pad) it assumes the belly flop.
        public Vector3 UnitTorque(Vector3 com, Transform rt)
        {
            if (!KaesaActive) return Vector3.zero;
            Vector3 n0 = pivot.forward, o = pivot.up;
            float s = 1f;
            if (part.Rigidbody != null)
            {
                Vector3 vel = part.Rigidbody.GetPointVelocity(ctrlSurface.position) + Krakensbane.GetFrameVelocityV3f();
                if (vel.sqrMagnitude > 1f)
                {
                    float d = Vector3.Dot(vel.normalized, n0);
                    if (Mathf.Abs(d) > 0.05f) s = Mathf.Sign(d);
                }
            }
            float range = Mathf.Abs(ctrlSurfaceRange) * authorityLimiter * 0.01f;
            float fOpen = Mathf.Clamp(restFold - range, 0f, maxFold), fFold = Mathf.Clamp(restFold + range, 0f, maxFold);
            Vector3 change = s * (PlatePush(fOpen, n0, o) - PlatePush(fFold, n0, o)) * (deflectionLiftCoeff * M2_PER_LIFT_UNIT * plateCn);
            return rt.InverseTransformDirection(Vector3.Cross(ctrlSurface.TransformPoint(copOffset) - com, change));
        }

        // SAS / autopilots: this flap's share of what all the KAESA surfaces can do on each axis (kN m, one way)
        public new void GetPotentialTorque(out Vector3 pos, out Vector3 neg)
        {
            pos = neg = Vector3.zero;
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || !KaesaActive) return;
            List<IKAESAControl> list = Controls();
            float gp, gr, gy; Vector3 full;
            if (!KAESAControlAllocator.Allocate(vessel, list, this, out gp, out gr, out gy, out full)) return;
            int n = KAESAControlAllocator.ActiveCount(list);
            if (n == 0) return;
            // q in kPa x (N m / Pa) = kN m; half a full swing each way. The authority limiter is already in UnitTorque's range
            // (fix: v1 applied it here only, while the allocation itself ignored it).
            float k = (float)part.dynamicPressurekPa * 0.5f / n;
            pos = neg = new Vector3(ignorePitch ? 0f : full.x * k, ignoreRoll ? 0f : full.y * k, ignoreYaw ? 0f : full.z * k);
        }

        // ================================================================ actions (names kept from v1) and info
        [KSPAction("Flaps: retract")]
        public void KaesaRetract(KSPActionParam p) { flapMode = "Retracted"; }

        [KSPAction("Flaps: extend")]
        public void KaesaExtend(KSPActionParam p) { flapMode = "Extended"; }

        [KSPAction("Flaps: control")]
        public void KaesaControl(KSPActionParam p) { flapMode = "Control"; }

        [KSPAction("Flaps: toggle retract")]
        public void KaesaToggleRetract(KSPActionParam p) { flapMode = flapMode == "Retracted" ? "Control" : "Retracted"; }

        public override string GetModuleDisplayName() { return "Starship Flap"; }

        public override string GetInfo()
        {
            return "Starship flap: 0° (out) to " + maxFold.ToString("F0") + "° (folded), rest angle " + restFold.ToString("F0") +
                   "°, ±" + Mathf.Abs(ctrlSurfaceRange).ToString("F0") + "° of steering around it, shared with the other flaps " +
                   "and the grid fins.\n\n" + base.GetInfo();
        }
    }
}
