// RubbleSim demo-render harness (isolated tooling).
//
// Renders one camera into an off-screen RenderTexture and writes lossless PNG frames to disk.
// Rendering into an RT (instead of grabbing the X11 window) keeps the output resolution independent
// of the player window, guarantees frame-exact capture when combined with Time.captureFramerate,
// and lets us record cameras that are not the screen camera.

using System.IO;
using UnityEngine;

public class FrameRecorder
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    private Camera cam;
    private RenderTexture rt;
    private Texture2D tex;
    private string dir;

    public FrameRecorder(Camera camera, int width, int height, string framesDir, bool srgb)
    {
        cam = camera;
        Width = width;
        Height = height;
        dir = framesDir;
        Directory.CreateDirectory(dir);

        // sRGB read/write keeps the PNG visually identical to what the player would show on screen.
        rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                               srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.antiAliasing = 1;
        rt.Create();

        tex = new Texture2D(width, height, TextureFormat.RGB24, false, false);
        cam.targetTexture = rt;
    }

    public string FramePath(int index)
    {
        return Path.Combine(dir, string.Format("frame_{0:D5}.png", index));
    }

    public void Capture(int index)
    {
        RenderTexture prev = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
        tex.Apply(false);
        RenderTexture.active = prev;
        File.WriteAllBytes(FramePath(index), tex.EncodeToPNG());
    }

    public void Release()
    {
        if (cam != null) cam.targetTexture = null;
        if (rt != null)
        {
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }
        if (tex != null)
        {
            Object.Destroy(tex);
            tex = null;
        }
    }
}
