// KAESA StarCraft - ModuleKAESAGridFin (v2, rewritten from scratch 2026-11-19)
// The Super Massive (Super Heavy V3) grid fin as its own part module. It is NOT built on the stock control surface:
//  - its own aerodynamics (GridFinAero): air through a lattice, cross-flow on both fin axes, transonic choking, works the same
//    when the booster falls engines first. The force is applied at the lattice centre each physics frame, and the part has
//    no stock drag (dragModelType = none in the cfg), so nothing is counted twice;
//  - its own actuator: first-order lag then a rate limit, travel +-maxDeflection about the hinge (the "finPivot" transform:
//    X = hinge / span, Y = cell axis, Z = chord), the "fin" transform turns with it;
//  - steering through the vessel-wide KAESA allocation shared with the StarCraft flaps (KAESAControl.cs): each fin reports the
//    torque a full swing makes RIGHT NOW (real coefficients, real flow direction), and gets its share of pitch / roll / yaw;
//  - real numbers for SAS and MechJeb (ITorqueProvider; MechJeb reads any ITorqueProvider module) and for the editor's
//    centre-of-lift marker (ILiftProvider).
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KAESA.StarCraft
{
    public class ModuleKAESAGridFin : PartModule, ITorqueProvider, ILiftProvider, IKAESAControl
    {
        private const string GRP = "kaesaGridFin";
        private const string GRP_NAME = "Grid fin";

        // ---------- model
        [KSPField] public string pivotTransformName = "finPivot";
        [KSPField] public string finTransformName = "fin";
        [KSPField] public Vector3 copOffset = new Vector3(1.21f, -0.05f, 0f);   // lattice centre, in the fin's frame
        // ---------- actuator
        [KSPField] public float maxDeflection = 30f;     // deg each way
        [KSPField] public float servoRate = 30f;         // deg/s
        [KSPField] public float servoLag = 0.08f;        // s, first-order lag before the rate limit
        // ---------- aerodynamics (see GridFinAero; MACH_EFFECTS { key = mach slopeFactor axialFactor } overrides the table)
        [KSPField] public float refArea = 3.0f;          // m^2, frontal area of the lattice outline
        [KSPField] public float normalSlope = 2.4f;      // per rad
        [KSPField] public float crossflowCd = 1.1f;
        [KSPField] public float axialCd = 0.45f;

        // ---------- part menu
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Fin mode", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_ChooseOption(options = new string[] { "Control", "Locked" }, scene = UI_Scene.All)]
        public string finMode = "Control";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Fin authority", guiUnits = "%", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_FloatRange(minValue = 0f, maxValue = 100f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float finAuthority = 100f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Fin pitch", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool usePitch = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Fin yaw", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool useYaw = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Fin roll", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool useRoll = true;

        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Test deflection", guiUnits = "°", groupName = GRP, groupDisplayName = GRP_NAME)]
        [UI_FloatRange(minValue = -30f, maxValue = 30f, stepIncrement = 1f, scene = UI_Scene.Editor)]
        public float editorTest = 0f;

        [KSPField(guiActive = true, guiName = "Fin angle", guiUnits = "°", guiFormat = "F1", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float shownAngle = 0f;
        [KSPField(guiActive = true, guiName = "Fin command", guiFormat = "F2", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float shownCommand = 0f;
        [KSPField(guiActive = true, guiName = "Flow through cells", guiUnits = "°", guiFormat = "F1", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float shownFlow = 0f;
        [KSPField(guiActive = true, guiName = "Fin force", guiUnits = " kN", guiFormat = "F1", groupName = GRP, groupDisplayName = GRP_NAME)]
        public float shownForce = 0f;

        // ---------- state
        private Transform pivot, fin;
        private Quaternion finRest = Quaternion.identity;
        private Vector3 finRestPos = Vector3.zero;
        private GridFinAero aero;
        private float angle;             // deg, where the fin is
        private float lagged;            // deg, command after the lag
        private readonly List<IKAESAControl> controls = new List<IKAESAControl>();
        private int controlsPartCount = -1;

        // ================================================================ setup
        public override void OnStart(StartState state)
        {
            pivot = part.FindModelTransform(pivotTransformName);
            fin = part.FindModelTransform(finTransformName);
            if (pivot == null || fin == null)
                Debug.LogWarning("[KAESA] ModuleKAESAGridFin: transforms '" + pivotTransformName + "' / '" + finTransformName + "' missing in " + part.partInfo.name);
            else { finRest = fin.localRotation; finRestPos = fin.localPosition; }
            BuildAero();
            UI_FloatRange t = Fields["editorTest"].uiControlEditor as UI_FloatRange;
            if (t != null) { t.minValue = -maxDeflection; t.maxValue = maxDeflection; }
            angle = lagged = 0f;
            Turn(0f);
        }

        private void BuildAero()
        {
            aero = new GridFinAero { area = refArea, slope = normalSlope, crossflowCd = crossflowCd, axialCd = axialCd };
            ConfigNode cfg = OwnConfig();
            ConfigNode mach = cfg != null ? cfg.GetNode("MACH_EFFECTS") : null;
            if (mach == null) return;
            string[] keys = mach.GetValues("key");
            var m = new List<double>(); var sl = new List<double>(); var ax = new List<double>();
            for (int i = 0; i < keys.Length; i++)
            {
                string[] p = keys[i].Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                double a, b, c;
                if (p.Length >= 3 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) { m.Add(a); sl.Add(b); ax.Add(c); }
            }
            if (m.Count >= 2) { aero.machKeys = m.ToArray(); aero.slopeKeys = sl.ToArray(); aero.axialKeys = ax.ToArray(); }
        }

        private ConfigNode OwnConfig()
        {
            if (part.partInfo == null || part.partInfo.partConfig == null) return null;
            ConfigNode[] mods = part.partInfo.partConfig.GetNodes("MODULE");
            for (int i = 0; i < mods.Length; i++)
                if (mods[i].GetValue("name") == moduleName) return mods[i];
            return null;
        }

        // ================================================================ geometry
        private void Turn(float deg)
        {
            if (fin != null) fin.localRotation = Quaternion.AngleAxis(deg, Vector3.right) * finRest;
        }

        // fin axes in world space at a given deflection: s = hinge (span), d = cell axis, c = chord
        private void Axes(float deg, out Vector3 s, out Vector3 d, out Vector3 c)
        {
            s = pivot.right;
            Quaternion r = Quaternion.AngleAxis(deg, s);
            d = r * pivot.up; c = r * pivot.forward;
        }

        private Vector3 CopAt(float deg)
        {
            return pivot.TransformPoint(finRestPos + Quaternion.AngleAxis(deg, Vector3.right) * (finRest * copOffset));
        }

        // velocity of the air relative to the fin at a point. Same frame as the stock aero (ModuleLiftingSurface /
        // FlightIntegrator: point velocity + Krakensbane frame velocity, no planet-rotation term): in the atmosphere KSP's
        // frame already turns with the planet, so subtracting getRFrmVel would add a false crosswind of the surface speed.
        private Vector3 AirAt(Vector3 pos)
        {
            return -(part.Rigidbody.GetPointVelocity(pos) + Krakensbane.GetFrameVelocityV3f());
        }

        private static GV G(Vector3 v) { return new GV(v.x, v.y, v.z); }
        private static Vector3 U(GV v) { return new Vector3((float)v.x, (float)v.y, (float)v.z); }

        private GridFinFlow FlowAt(float deg, Vector3 air, double rho)
        {
            Vector3 s, d, c; Axes(deg, out s, out d, out c);
            return aero.Force(G(air), G(s), G(d), G(c), rho, vessel != null ? vessel.speedOfSound : 340.0);
        }

        // ================================================================ allocation (shared with the flaps)
        public bool KaesaActive
        {
            get { return finMode == "Control" && pivot != null && fin != null && part != null && !part.ShieldedFromAirstream; }
        }

        private List<IKAESAControl> Controls()
        {
            if (vessel.parts.Count != controlsPartCount || controls.Count == 0)
            {
                KAESAControlAllocator.Collect(vessel, controls);
                controlsPartCount = vessel.parts.Count;
            }
            return controls;
        }

        // vessel-axis torque of a full swing (-max -> +max) per unit dynamic pressure (m^3), with the flow as it is now
        public Vector3 UnitTorque(Vector3 com, Transform rt)
        {
            if (!KaesaActive || part.Rigidbody == null || vessel == null) return Vector3.zero;
            Vector3 air = AirAt(CopAt(angle));
            float v = air.magnitude;
            if (v < 1f) return Vector3.zero;
            double rhoUnit = 2.0 / ((double)v * v);                 // makes q = 1 Pa
            Vector3 tPos = Vector3.Cross(CopAt(maxDeflection) - com, U(FlowAt(maxDeflection, air, rhoUnit).force));
            Vector3 tNeg = Vector3.Cross(CopAt(-maxDeflection) - com, U(FlowAt(-maxDeflection, air, rhoUnit).force));
            return rt.InverseTransformDirection(tPos - tNeg);
        }

        // this fin's share of what all the KAESA surfaces can do on each axis right now (kN m, one way)
        public void GetPotentialTorque(out Vector3 pos, out Vector3 neg)
        {
            pos = neg = Vector3.zero;
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.Rigidbody == null || !KaesaActive) return;
            List<IKAESAControl> list = Controls();
            float gp, gr, gy; Vector3 full;
            if (!KAESAControlAllocator.Allocate(vessel, list, this, out gp, out gr, out gy, out full)) return;
            int n = KAESAControlAllocator.ActiveCount(list);
            if (n == 0) return;
            double q = 0.5 * part.atmDensity * AirAt(CopAt(angle)).sqrMagnitude;
            float k = (float)(q * 0.001) * 0.5f * finAuthority * 0.01f / n;
            Vector3 a = new Vector3(usePitch ? full.x * k : 0f, useRoll ? full.y * k : 0f, useYaw ? full.z * k : 0f);
            pos = a; neg = a;
        }

        // ================================================================ physics frame
        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || pivot == null || fin == null || aero == null) return;
            float dt = TimeWarp.fixedDeltaTime;
            float cmd = 0f;
            if (KaesaActive && !vessel.packed && part.Rigidbody != null)
                cmd = KAESAControlAllocator.Command(vessel, Controls(), this, !usePitch, !useRoll, !useYaw);
            float target = finMode == "Locked" ? 0f : Mathf.Clamp(cmd * maxDeflection * finAuthority * 0.01f, -maxDeflection, maxDeflection);
            lagged = servoLag > 0.001f ? Mathf.Lerp(lagged, target, 1f - Mathf.Exp(-dt / servoLag)) : target;
            angle = Mathf.MoveTowards(angle, lagged, Mathf.Max(servoRate, 1f) * dt);
            Turn(angle);
            shownAngle = angle; shownCommand = cmd;

            if (vessel.packed || part.Rigidbody == null || part.ShieldedFromAirstream) { shownFlow = 0f; shownForce = 0f; return; }
            Vector3 cop = CopAt(angle);
            GridFinFlow f = FlowAt(angle, AirAt(cop), part.atmDensity);
            if (f.q > 0.0)
            {
                Vector3 force = U(f.force) * 0.001f;                // N -> kN (KSP masses are in tonnes)
                part.AddForceAtPosition(force, cop);
                shownForce = force.magnitude;
            }
            else shownForce = 0f;
            shownFlow = (float)f.flowAngle;
        }

        public void Update()
        {
            if (HighLogic.LoadedSceneIsEditor && fin != null) Turn(Mathf.Clamp(editorTest, -maxDeflection, maxDeflection));
        }

        // ================================================================ editor centre of lift
        public bool DisableBodyLift { get { return false; } }
        public bool IsLifting { get { return true; } }

        public void OnCenterOfLiftQuery(CenterOfLiftQuery qry)
        {
            qry.pos = part.transform.position; qry.dir = Vector3.zero; qry.lift = 1e-5f;
            if (pivot == null || aero == null || part.ShieldedFromAirstream || qry.refVector.sqrMagnitude < 1e-6f) return;
            float deg = HighLogic.LoadedSceneIsEditor ? Mathf.Clamp(editorTest, -maxDeflection, maxDeflection) : angle;
            Vector3 s, d, c; Axes(deg, out s, out d, out c);
            double sound = FlightGlobals.GetHomeBody() != null
                ? FlightGlobals.GetHomeBody().GetSpeedOfSound(qry.refStaticPressure, qry.refAirDensity) : 340.0;
            Vector3 vel = qry.refVector;
            GridFinFlow f = aero.Force(G(-vel), G(s), G(d), G(c), qry.refAirDensity, sound);
            Vector3 F = U(f.force) * 0.001f, vn = vel.normalized;
            Vector3 lift = F - Vector3.Dot(F, vn) * vn;
            qry.pos = CopAt(deg);
            if (lift.sqrMagnitude > 1e-12f) { qry.lift = lift.magnitude; qry.dir = lift / qry.lift; }
        }

        // ================================================================ actions and info
        [KSPAction("Grid fins: control")]
        public void ActControl(KSPActionParam p) { finMode = "Control"; }

        [KSPAction("Grid fins: lock")]
        public void ActLock(KSPActionParam p) { finMode = "Locked"; }

        [KSPAction("Grid fins: toggle lock")]
        public void ActToggle(KSPActionParam p) { finMode = finMode == "Locked" ? "Control" : "Locked"; }

        public override string GetModuleDisplayName() { return "Grid Fin"; }

        public override string GetInfo()
        {
            return "Lattice fin with its own aerodynamics: works through the cells in both directions (also falling engines " +
                   "first), loses some bite around Mach 1 and gets it back above.\n" +
                   "<b>Travel:</b> ±" + maxDeflection.ToString("F0") + "° at " + servoRate.ToString("F0") + "°/s\n" +
                   "<b>Lattice area:</b> " + refArea.ToString("F2") + " m²\n" +
                   "Steers pitch, yaw and roll together with the StarCraft flaps; Locked holds it at 0°.";
        }
    }
}
