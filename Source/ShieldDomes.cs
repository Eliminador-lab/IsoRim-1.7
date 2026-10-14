using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Shield bubbles (CompProjectileInterceptor: broadshield, mech shield generators, shield
    // pawns) in 3D. The game draws each as a flat square with its round force-field texture
    // laid on the ground; seen tilted that is a flat ring on the ground. In 3D it is drawn as a
    // dome over the same circle instead: the same material, colour and pulse, the texture laid
    // onto the dome straight from above, so from directly overhead it looks exactly as the game's.
    // The interception flash (the same draw, turned toward the hit) goes on the dome too.
    // CompProjectileInterceptor.PostDraw's Graphics.DrawMesh calls are pointed here (Patches).
    public static class ShieldDomes
    {
        // The game's square is 1.1601562 times the shield's diameter: the circle in the texture
        // has this radius in the square's own units (the square spans -0.5..0.5).
        private const float CircleRadius = 0.5f / 1.1601562f;
        private const int Rings = 10;
        private const int Segments = 48;

        private static Mesh dome;

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            if (!View3D.Ready || mesh != MeshPool.plane10)
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties);
                return;
            }
            Vector3 at = matrix.GetColumn(3);
            Vector3 x = matrix.GetColumn(0);
            Vector3 z = matrix.GetColumn(2);
            float size = x.magnitude;
            Quaternion turn = Quaternion.LookRotation(z.normalized, Vector3.up);
            float s = View3D.Squash;
            // On the ground, true height (the map is drawn squashed flat, see View3D).
            Matrix4x4 m = Matrix4x4.Translate(new Vector3(at.x, 0f, at.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(turn)
                * Matrix4x4.Scale(new Vector3(size, size, size));
            Graphics.DrawMesh(Dome(), m, material, layer, camera, submeshIndex, properties);
        }

        // A half sphere over the texture's circle, in the game square's units, with the square's
        // texture coordinates seen from above (u = x + 0.5, v = z + 0.5).
        private static Mesh Dome()
        {
            if (dome != null)
            {
                return dome;
            }
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();
            for (int r = 0; r <= Rings; r++)
            {
                float a = r / (float)Rings * Mathf.PI * 0.5f;   // 0 at the top, 90 degrees at the ground
                float ring = Mathf.Sin(a) * CircleRadius;
                float y = Mathf.Cos(a) * CircleRadius;
                for (int k = 0; k <= Segments; k++)
                {
                    float b = k / (float)Segments * Mathf.PI * 2f;
                    float px = Mathf.Cos(b) * ring;
                    float pz = Mathf.Sin(b) * ring;
                    verts.Add(new Vector3(px, y, pz));
                    uvs.Add(new Vector2(px + 0.5f, pz + 0.5f));
                }
            }
            int row = Segments + 1;
            for (int r = 0; r < Rings; r++)
            {
                for (int k = 0; k < Segments; k++)
                {
                    int a0 = r * row + k, a1 = a0 + 1, b0 = a0 + row, b1 = b0 + 1;
                    // Clockwise seen from outside (the game's front faces).
                    tris.Add(a0); tris.Add(a1); tris.Add(b0);
                    tris.Add(a1); tris.Add(b1); tris.Add(b0);
                }
            }
            dome = new Mesh();
            dome.SetVertices(verts);
            dome.SetUVs(0, uvs);
            dome.SetTriangles(tris, 0);
            dome.RecalculateBounds();
            dome.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return dome;
        }
    }
}
