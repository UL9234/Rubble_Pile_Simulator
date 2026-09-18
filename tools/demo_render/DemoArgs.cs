// RubbleSim demo-render harness (isolated tooling, not part of the simulator's business logic).
// Authoritative source lives in tools/demo_render/. A synced copy is exposed to Unity under Assets/DemoRender/.
//
// Minimal command-line parser, independent of the project's own CustomArgs so that demo-harness flags
// can never collide with simulation flags. Demo flags are all prefixed "-demo" by convention.

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public static class DemoArgs
{
    private static Dictionary<string, string> map;

    public static void Init()
    {
        if (map != null) return;
        map = new Dictionary<string, string>();
        string[] argv = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (string.IsNullOrEmpty(a) || a[0] != '-') continue;
            string key = a.TrimStart('-').ToLowerInvariant();
            string val = "";
            if (i + 1 < argv.Length)
            {
                string next = argv[i + 1];
                float probe;
                // Accept the next token as a value unless it looks like another flag.
                if (next.Length > 0 && (next[0] != '-' || float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out probe)))
                {
                    val = next;
                    i++;
                }
            }
            map[key] = val;
        }
    }

    public static bool Has(string key)
    {
        Init();
        return map.ContainsKey(key.ToLowerInvariant());
    }

    public static string Get(string key, string fallback)
    {
        Init();
        string v;
        if (map.TryGetValue(key.ToLowerInvariant(), out v) && !string.IsNullOrEmpty(v)) return v;
        return fallback;
    }

    public static float GetF(string key, float fallback)
    {
        string v = Get(key, null);
        if (v == null) return fallback;
        float f;
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
    }

    public static int GetI(string key, int fallback)
    {
        return Mathf.RoundToInt(GetF(key, fallback));
    }

    public static bool GetB(string key, bool fallback)
    {
        string v = Get(key, null);
        if (v == null) return fallback;
        v = v.ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes" || v == "on";
    }

    /// <summary>Parses "x,y,z" (also accepts "x y z" via shell splitting).</summary>
    public static Vector3 GetV3(string key, Vector3 fallback)
    {
        string v = Get(key, null);
        if (v == null) return fallback;
        v = v.Replace(" ", ",");
        string[] parts = v.Split(',');
        if (parts.Length < 3) return fallback;
        float x, y, z;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            return new Vector3(x, y, z);
        return fallback;
    }

    /// <summary>Human-readable dump of all parsed args, used for the per-shot metadata file.</summary>
    public static string Dump()
    {
        Init();
        var keys = new List<string>(map.Keys);
        keys.Sort();
        var sb = new System.Text.StringBuilder();
        foreach (string k in keys) sb.Append('-').Append(k).Append(' ').Append(map[k]).Append('\n');
        return sb.ToString();
    }
}
