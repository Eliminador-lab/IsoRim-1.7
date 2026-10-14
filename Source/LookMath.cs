using UnityEngine;

namespace IsoRim
{
    // The colour grade's maths, kept apart from the game so it can be checked on its own.
    //
    // A look works on each colour channel by itself. Channel() is exactly what the see-through
    // sheets of the 1.5/1.6 grade did with blending, step by step, each step left out when its sheet
    // was. Strip() turns it into a lookup table (LUT) for the 1.7 grade shader, in the layout of the
    // LUT files: N slices of N x N side by side, blue picks the slice, red runs across, green up.
    public static class LookMath
    {
        public const float Pivot = 0.3f;   // the grey colours are pulled toward

        // The squaring curve: x is first scaled by s, then multiplied by (cc + itself). s and cc are
        // picked so that white stays white and nothing overflows on the way.
        public static void ContrastTerms(float contrast, out float s, out float cc)
        {
            s = 1f - 0.25f * Mathf.Clamp01(contrast);
            cc = (1f - s * s) / s;
        }

        public static bool HasContrast(LookPreset p)
        {
            return p.contrast > 0.001f;
        }

        public static bool HasGrey(LookPreset p)
        {
            return 1f - Mathf.Clamp01(p.desat) < 0.995f || p.bright < 0.995f;
        }

        public static bool HasGain(LookPreset p)
        {
            return p.gain.r < 0.995f || p.gain.g < 0.995f || p.gain.b < 0.995f;
        }

        public static bool HasLift(LookPreset p)
        {
            return p.lift.r > 0.002f || p.lift.g > 0.002f || p.lift.b > 0.002f;
        }

        public static bool HasExposure(LookPreset p)
        {
            return p.exposure > 0.002f;
        }

        // True if the look changes nothing (no sheet would have been drawn).
        public static bool Identity(LookPreset p)
        {
            return !HasContrast(p) && !HasGrey(p) && !HasGain(p) && !HasLift(p) && !HasExposure(p);
        }

        // One channel (0 red, 1 green, 2 blue) of a colour under a look.
        public static float Channel(LookPreset p, float x, int i)
        {
            if (HasContrast(p))
            {
                float s, cc;
                ContrastTerms(p.contrast, out s, out cc);
                float y = s * x;
                x = Mathf.Clamp01(y * (cc + y));
            }
            if (HasGrey(p))
            {
                float keep = 1f - Mathf.Clamp01(p.desat);
                x = Mathf.Clamp01(p.bright * (1f - keep) * Pivot + x * p.bright * keep);
            }
            if (HasGain(p))
            {
                x = Mathf.Clamp01(x * p.gain[i]);
            }
            if (HasLift(p))
            {
                x = Mathf.Clamp01(x + p.lift[i] * (1f - x));
            }
            if (HasExposure(p))
            {
                x = Mathf.Clamp01(x * (1f + p.exposure));
            }
            return x;
        }

        public static Color Apply(LookPreset p, Color c)
        {
            return new Color(Channel(p, c.r, 0), Channel(p, c.g, 1), Channel(p, c.b, 2), 1f);
        }

        // The lookup table of a look, n slices of n x n, in Texture2D.SetPixels32 order (row 0 is
        // green 0). into is reused when it has the right size.
        public static Color32[] Strip(LookPreset p, int n, Color32[] into)
        {
            int w = n * n;
            if (into == null || into.Length != w * n)
            {
                into = new Color32[w * n];
            }
            byte[][] curve = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                curve[i] = new byte[n];
                for (int k = 0; k < n; k++)
                {
                    curve[i][k] = (byte)Mathf.RoundToInt(Channel(p, k / (float)(n - 1), i) * 255f);
                }
            }
            for (int g = 0; g < n; g++)
            {
                int row = g * w;
                for (int b = 0; b < n; b++)
                {
                    int at = row + b * n;
                    for (int r = 0; r < n; r++)
                    {
                        into[at + r] = new Color32(curve[0][r], curve[1][g], curve[2][b], 255);
                    }
                }
            }
            return into;
        }

        // What the grade shader's Lookup() gives for a colour, worked out on the CPU from a table in
        // SetPixels32 order: two bilinear reads in the two nearest blue slices, mixed by blue.
        public static Color Lookup(Color32[] strip, int n, Color c)
        {
            float slice = Mathf.Clamp01(c.b) * (n - 1);
            int s0 = Mathf.FloorToInt(slice);
            int s1 = Mathf.Min(s0 + 1, n - 1);
            float f = slice - s0;
            float x = Mathf.Clamp01(c.r) * (n - 1);
            float y = Mathf.Clamp01(c.g) * (n - 1);
            Color a = Bilinear(strip, n, x + s0 * n, y);
            Color b = Bilinear(strip, n, x + s1 * n, y);
            return Color.Lerp(a, b, f);
        }

        private static Color Bilinear(Color32[] strip, int n, float x, float y)
        {
            int w = n * n;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, n - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, n - 1);
            float fx = x - x0;
            float fy = y - y0;
            Color c00 = strip[y0 * w + x0];
            Color c10 = strip[y0 * w + x1];
            Color c01 = strip[y1 * w + x0];
            Color c11 = strip[y1 * w + x1];
            return Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
        }
    }
}
