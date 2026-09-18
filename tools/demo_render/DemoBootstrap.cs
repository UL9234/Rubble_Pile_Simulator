// RubbleSim demo-render harness (isolated tooling).
//
// Single entry point: injects DemoDirector at runtime when (and only when) a "-demoshot" argument is
// present. This keeps the harness fully inert for normal simulator runs and requires no scene edits.

using UnityEngine;

public static class DemoBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        if (!DemoArgs.Has("demoshot")) return;
        GameObject go = new GameObject("DemoDirector");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<DemoDirector>();
    }
}
