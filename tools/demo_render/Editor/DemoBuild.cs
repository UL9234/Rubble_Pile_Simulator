// RubbleSim demo-render harness (isolated tooling).
//
// Editor-only build entry point, invoked with:
//   unity2022 -batchmode -nographics -quit -executeMethod DemoBuild.PerformBuild -buildout <dir>
//
// This lives in an "Editor" folder so Unity compiles it into Assembly-CSharp-Editor and it is never
// part of a player build. It exists only so the demo harness can produce a standalone Linux player
// without anyone having to click through the Editor UI.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class DemoBuild
{
    public static void PerformBuild()
    {
        string outDir = Arg("-buildout", Path.Combine(Directory.GetCurrentDirectory(), "Builds/Linux"));
        Directory.CreateDirectory(outDir);

        var scenes = new List<string>();
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (s.enabled) scenes.Add(s.path);
        }
        if (scenes.Count == 0) scenes.Add("Assets/MITLL/Scenes/MainScene_OS.unity");

        string exe = Path.Combine(outDir, "RubbleSim.x86_64");
        var options = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = exe,
            target = BuildTarget.StandaloneLinux64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        Debug.Log("[DemoBuild] building scenes: " + string.Join(", ", scenes));
        Debug.Log("[DemoBuild] output: " + exe);

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[DemoBuild] result={0} errors={1} warnings={2} size={3} bytes time={4}",
            summary.result, summary.totalErrors, summary.totalWarnings, summary.totalSize, summary.totalTime));

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError("[DemoBuild] BUILD FAILED");
            EditorApplication.Exit(1);
        }
        Debug.Log("[DemoBuild] BUILD OK");
        EditorApplication.Exit(0);
    }

    private static string Arg(string key, string fallback)
    {
        string[] argv = Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], key, StringComparison.OrdinalIgnoreCase)) return argv[i + 1];
        }
        return fallback;
    }
}
