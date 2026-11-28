// KAESA StarCraft - shared control allocation for every KAESA control surface on a vessel (Starship flaps, Super Heavy
// grid fins). Moved out of ModuleKAESAFlap unchanged: B = torque (vessel axes) per unit command of each surface; for each
// axis the minimum-norm set of commands that makes a PURE torque on that axis (g = B^T (B B^T + lambda)^-1 e_k), scaled so
// the busiest surface uses its full travel. Pitch / roll / yaw keys then add those three patterns.
using System.Collections.Generic;
using UnityEngine;

namespace KAESA.StarCraft
{
    public interface IKAESAControl
    {
        // taking part in the allocation right now (control mode, in the airstream)
        bool KaesaActive { get; }
        // torque (vessel axes: x pitch, y roll, z yaw) of a full swing of the command (-1 -> +1), x surface area
        Vector3 UnitTorque(Vector3 com, Transform rt);
        // ITorqueProvider-style report (SAS reads it; MechJeb gets the flaps' one through KAESAFlapTorquePatch)
        void GetPotentialTorque(out Vector3 pos, out Vector3 neg);
    }

    public static class KAESAControlAllocator
    {
        public static void Collect(Vessel vessel, List<IKAESAControl> list)
        {
            list.Clear();
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                for (int k = 0; k < p.Modules.Count; k++)
                {
                    IKAESAControl c = p.Modules[k] as IKAESAControl;
                    if (c != null) list.Add(c);
                }
            }
        }

        public static int ActiveCount(List<IKAESAControl> list)
        {
            int n = 0;
            for (int j = 0; j < list.Count; j++) if (list[j].KaesaActive) n++;
            return n;
        }

        // this surface's share of each axis pattern (gp, gr, gy) and the vessel torque of each full pattern
        public static bool Allocate(Vessel vessel, List<IKAESAControl> list, IKAESAControl self, out float gp, out float gr, out float gy,
            out Vector3 full)
        {
            gp = gr = gy = 0f; full = Vector3.zero;
            if (vessel == null || vessel.ReferenceTransform == null) return false;
            int n = list.Count, me = list.IndexOf(self);
            if (n == 0 || me < 0) return false;
            Vector3 com = vessel.CurrentCoM; Transform rt = vessel.ReferenceTransform;
            Vector3[] b = new Vector3[n];
            float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            for (int j = 0; j < n; j++)
            {
                b[j] = list[j].UnitTorque(com, rt); Vector3 v = b[j];
                m00 += v.x * v.x; m01 += v.x * v.y; m02 += v.x * v.z; m11 += v.y * v.y; m12 += v.y * v.z; m22 += v.z * v.z;
            }
            float tr = m00 + m11 + m22;
            if (tr < 1e-9f) return false;
            float lam = 1e-3f * tr / 3f + 1e-9f; m00 += lam; m11 += lam; m22 += lam;
            // inverse of the symmetric 3x3 (adjugate / determinant)
            float c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
            float det = m00 * c00 + m01 * c01 + m02 * c02;
            if (Mathf.Abs(det) < 1e-20f) return false;
            float c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
            Vector3[] inv = { new Vector3(c00, c01, c02) / det, new Vector3(c01, c11, c12) / det, new Vector3(c02, c12, c22) / det };
            float[] mine = new float[3];
            for (int k = 0; k < 3; k++)
            {
                float mx = 0f, g_me = 0f; Vector3 t = Vector3.zero;
                for (int j = 0; j < n; j++)
                {
                    float g = Vector3.Dot(b[j], inv[k]);
                    mx = Mathf.Max(mx, Mathf.Abs(g));
                    if (j == me) g_me = g;
                    t += b[j] * g;
                }
                if (mx < 1e-12f) continue;
                mine[k] = g_me / mx; t /= mx;
                full[k] = Mathf.Abs(t[k]);
            }
            gp = mine[0]; gr = mine[1]; gy = mine[2];
            return true;
        }

        // command (-1..1) for a surface from the pilot / SAS input: wanted torque = -(pitch, roll, yaw), like ModuleReactionWheel
        public static float Command(Vessel vessel, List<IKAESAControl> list, IKAESAControl self, bool ignorePitch, bool ignoreRoll, bool ignoreYaw)
        {
            float gp, gr, gy; Vector3 full;
            if (!Allocate(vessel, list, self, out gp, out gr, out gy, out full)) return 0f;
            FlightCtrlState c = vessel.ctrlState;
            float e = 0f;
            if (!ignorePitch) e -= c.pitch * gp;
            if (!ignoreRoll) e -= c.roll * gr;
            if (!ignoreYaw) e -= c.yaw * gy;
            return Mathf.Clamp(e, -1f, 1f);
        }

        // equal share of what the whole set makes for a pure command on each axis (kN m), times dynamic pressure,
        // normal force coefficient and the authority limiter; a full swing is half each way
        public static void PotentialTorque(Vessel vessel, List<IKAESAControl> list, IKAESAControl self, double qkPa, float plateCn,
            float authorityLimiter, bool ignorePitch, bool ignoreRoll, bool ignoreYaw, out Vector3 pos, out Vector3 neg)
        {
            pos = neg = Vector3.zero;
            if (qkPa <= 0.0) return;
            float gp, gr, gy; Vector3 full;
            if (!Allocate(vessel, list, self, out gp, out gr, out gy, out full)) return;
            int nActive = ActiveCount(list);
            if (nActive == 0) return;
            float k = (float)(qkPa * plateCn) * 0.5f * Mathf.Abs(authorityLimiter) * 0.01f / nActive;
            Vector3 a = new Vector3(ignorePitch ? 0f : full.x * k, ignoreRoll ? 0f : full.y * k, ignoreYaw ? 0f : full.z * k);
            pos = a; neg = a;
        }
    }
}
