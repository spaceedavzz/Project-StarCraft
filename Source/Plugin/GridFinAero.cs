// KAESA StarCraft - GridFinAero
// Aerodynamic force on a lattice ("grid") fin, written from scratch for StarCraft v2 (2026-11-19). Plain C#, no Unity types,
// so it can be compiled and checked outside the game (Source/tools/GridFinAeroTest.cs).
//
// The fin is a box of thin plates crossing at 90 deg, set at +-45 deg to the fin. The air goes THROUGH the cells, so:
//  - the force does not come from one flat face. Any flow that crosses the cell axis (the "depth" axis d) pushes on both plate
//    families. A 90 deg lattice reacts the same to cross-flow along the chord (c) and along the span (s). The normal force
//    follows the whole cross-flow vector, not one axis.
//  - normal force: Cn(a) = slope * sin a cos a + crossflowCd * sin a |sin a|. Linear at small angles, no sharp stall (short
//    cells), and it saturates to plain cross-flow drag at 90 deg.
//  - axial force: drag through the cells, along d, = axialCd * cos a.
//  - Mach: around Mach 1 the cells choke. The normal-force slope drops and the axial drag peaks; both recover above
//    Mach ~1.5. A table of (Mach, slope factor, axial factor) sets this.
//  - flying backwards (the booster falls engines first) is the same physics with the flow reversed through the cells: only
//    |w . d| enters the angles, and the forces follow the actual flow.
namespace KAESA.StarCraft
{
    public struct GV
    {
        public double x, y, z;
        public GV(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static GV operator +(GV a, GV b) { return new GV(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static GV operator -(GV a, GV b) { return new GV(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static GV operator -(GV a) { return new GV(-a.x, -a.y, -a.z); }
        public static GV operator *(GV a, double k) { return new GV(a.x * k, a.y * k, a.z * k); }
        public static GV operator *(double k, GV a) { return new GV(a.x * k, a.y * k, a.z * k); }
        public static double Dot(GV a, GV b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        public static GV Cross(GV a, GV b) { return new GV(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
        public double Length { get { return System.Math.Sqrt(x * x + y * y + z * z); } }
        public static readonly GV Zero = new GV(0, 0, 0);
    }

    public struct GridFinFlow
    {
        public double speed, mach, q;        // m/s, -, Pa
        public double flowAngle;             // angle between the airflow and the cell axis, deg (0 = straight through)
        public double cn, ca;                // normal and axial coefficients used
        public GV force;                     // N, world axes
    }

    public sealed class GridFinAero
    {
        public double area = 3.0;            // frontal area of the lattice outline, m^2
        public double slope = 2.4;           // normal-force slope at low Mach, per radian (on the frontal area)
        public double crossflowCd = 1.1;     // normal force coefficient with the flow straight across the cells
        public double axialCd = 0.45;        // through-the-cells drag at low Mach

        // Mach effects: (Mach, slope factor, axial factor), linear between points, flat outside
        public double[] machKeys = { 0.0, 0.6, 0.85, 1.0, 1.3, 2.0, 3.0, 5.0 };
        public double[] slopeKeys = { 1.0, 1.0, 0.80, 0.60, 0.72, 0.85, 0.75, 0.60 };
        public double[] axialKeys = { 1.0, 1.05, 1.7, 2.4, 1.9, 1.4, 1.1, 0.9 };

        public static double Interp(double[] xs, double[] ys, double x)
        {
            if (xs == null || ys == null || xs.Length == 0) return 1.0;
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) { double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]); return ys[i - 1] + (ys[i] - ys[i - 1]) * t; }
            return ys[ys.Length - 1];
        }

        public double NormalCoeff(double a, double mach)
        {
            double sa = System.Math.Sin(a), cA = System.Math.Cos(a);
            return slope * Interp(machKeys, slopeKeys, mach) * sa * cA + crossflowCd * sa * System.Math.Abs(sa);
        }

        /// <summary>Force on the fin. air = velocity of the air relative to the fin (m/s, world), unit axes of the fin:
        /// s = span (hinge), d = cell axis (depth), c = chord. rho in kg/m^3, sound speed in m/s.</summary>
        public GridFinFlow Force(GV air, GV s, GV d, GV c, double rho, double soundSpeed)
        {
            GridFinFlow r = new GridFinFlow();
            double v = air.Length;
            r.speed = v;
            if (v < 0.5 || rho <= 0.0) return r;
            GV w = air * (1.0 / v);
            double wd = GV.Dot(w, d), wc = GV.Dot(w, c), ws = GV.Dot(w, s);
            double cross = System.Math.Sqrt(wc * wc + ws * ws);
            double a = System.Math.Atan2(cross, System.Math.Abs(wd));          // 0 .. pi/2
            r.flowAngle = a * 180.0 / System.Math.PI;
            r.mach = soundSpeed > 1.0 ? v / soundSpeed : 0.0;
            r.q = 0.5 * rho * v * v;
            r.cn = NormalCoeff(a, r.mach);
            r.ca = axialCd * Interp(machKeys, axialKeys, r.mach) * System.Math.Cos(a);
            GV crossDir = cross > 1e-9 ? (c * wc + s * ws) * (1.0 / cross) : GV.Zero;   // the way the air crosses the cells
            GV axialDir = wd >= 0.0 ? d : -d;                                            // the way it goes through them
            r.force = (crossDir * r.cn + axialDir * r.ca) * (r.q * area);
            return r;
        }
    }
}
