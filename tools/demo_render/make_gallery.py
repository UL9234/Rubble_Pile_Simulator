#!/usr/bin/env python3
"""RubbleSim demo-render harness (isolated tooling).

Builds the browser gallery page the user opens to watch the rendered clips:

  /data1/chh/dataset/rubble_dataset/demo/index.html

Each clip gets an inline <video> player, a poster frame, a contact sheet and a table of the exact
simulation/render parameters taken from the harness's per-shot shot.json. No external assets, no CDN.
"""

import argparse
import html
import json
import os
import re
import subprocess
import sys

SHOT_TITLES = {
    "01_overview_accum": "01 · 全景俯视：碎块如雨落下、逐层堆积（宏观）",
    "02_low_impact": "02 · 低机位冲击视角：不同层数/边界参数预设",
    "03_orbit_settled": "03 · 堆积完成后环绕运镜",
    "04_closeup_settled": "04 · 近景推镜：堆积体细节与雾",
    "05_fpv_robot": "05 · 机器人第一视角：落入废墟并向前推进",
    "06_sensor_rgb": "06 · 机载 RGB 传感器视角（90° FOV，随机种子预设）",
}

INTERESTING_ARGS = [
    ("numlayers", "堆积层数 numlayers"),
    ("numobjs", "每层碎块数 numobjs"),
    ("spawnboundx", "堆积区 X 尺度"),
    ("spawnboundz", "堆积区 Z 尺度"),
    ("spawnposy", "堆积起始高度"),
    ("randomseed", "随机种子"),
    ("fogdensity", "雾密度"),
    ("fogintensity", "雾强度"),
    ("lighttype", "光源类型"),
    ("lightintensity", "光强"),
    ("exportstl", "导出 STL"),
]


def ffprobe(path):
    try:
        out = subprocess.check_output([
            "ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,nb_frames,duration",
            "-of", "json", path], text=True)
        st = json.loads(out)["streams"][0]
        return {
            "width": int(st.get("width", 0)),
            "height": int(st.get("height", 0)),
            "frames": int(st.get("nb_frames", 0) or 0),
            "duration": float(st.get("duration", 0) or 0),
        }
    except Exception:
        return {}


def parse_args(shot_json):
    meta = {}
    try:
        with open(shot_json) as fh:
            data = json.load(fh)
        raw = data.get("arguments", "")
        for m in re.finditer(r"-([a-z0-9]+)\s+([^\s-][^\s]*)", raw):
            meta[m.group(1)] = m.group(2)
        data["_parsed"] = meta
        return data
    except Exception:
        return {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default="/data1/chh/dataset/rubble_dataset/demo")
    ap.add_argument("--port", type=int, default=8099)
    args = ap.parse_args()

    demo = os.path.abspath(args.dir)
    thumbs = os.path.join(demo, "thumbs")
    work = os.path.join(demo, "_frames")
    os.makedirs(thumbs, exist_ok=True)

    clips = sorted(f for f in os.listdir(demo) if f.endswith(".mp4"))
    if not clips:
        print("no mp4 files found in " + demo, file=sys.stderr)
        return 1

    rows = []
    for clip in clips:
        name = clip[:-4]
        path = os.path.join(demo, clip)
        info = ffprobe(path)
        sj = os.path.join(work, name, "shot.json")
        meta = parse_args(sj) if os.path.exists(sj) else {}
        parsed = meta.get("_parsed", {})

        param_rows = []
        for key, label in INTERESTING_ARGS:
            if key in parsed:
                param_rows.append((label, parsed[key]))
        param_rows.append(("渲染分辨率", "%dx%d @ %s fps" % (
            info.get("width", meta.get("width", 0)), info.get("height", meta.get("height", 0)),
            meta.get("fps", 30))))
        if info.get("frames"):
            param_rows.append(("帧数 / 时长", "%d 帧 / %.1f 秒" % (info["frames"], info.get("duration", 0))))
        param_rows.append(("机位预设", meta.get("preset", "-")))
        if meta.get("pile_bounds_size"):
            param_rows.append(("堆积体包围盒", meta["pile_bounds_size"]))
        if meta.get("drive") and meta["drive"] != "none":
            param_rows.append(("机器人驱动", meta["drive"]))
        if meta.get("teleport_robot"):
            param_rows.append(("机器人落点", "自动投放到堆积体顶部"))

        argstr = ""
        if os.path.exists(sj):
            try:
                with open(sj) as fh:
                    argstr = json.load(fh).get("arguments", "").replace("\\n", "\n")
            except Exception:
                pass

        rows.append({
            "name": name,
            "title": SHOT_TITLES.get(name, name),
            "file": clip,
            "poster": "thumbs/%s_poster.jpg" % name if os.path.exists(os.path.join(thumbs, name + "_poster.jpg")) else "",
            "sheet": "thumbs/%s_sheet.jpg" % name if os.path.exists(os.path.join(thumbs, name + "_sheet.jpg")) else "",
            "params": param_rows,
            "args": argstr,
            "info": info,
        })

    style = """
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #0d1117; color: #e6edf3;
           font-family: -apple-system, "Segoe UI", "Noto Sans CJK SC", "Microsoft YaHei", Roboto, sans-serif; }
    header { padding: 28px 32px 8px; }
    h1 { margin: 0 0 6px; font-size: 24px; }
    .sub { color: #8b949e; font-size: 14px; line-height: 1.7; }
    .sub code { background: #161b22; padding: 1px 6px; border-radius: 4px; }
    main { padding: 16px 32px 64px; display: grid; gap: 28px;
           grid-template-columns: repeat(auto-fit, minmax(520px, 1fr)); }
    .card { background: #161b22; border: 1px solid #30363d; border-radius: 12px; overflow: hidden; }
    .card h2 { font-size: 15px; margin: 0; padding: 14px 16px; border-bottom: 1px solid #30363d; font-weight: 600; }
    video { width: 100%; display: block; background: #000; }
    .body { padding: 12px 16px 18px; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    td { padding: 4px 6px; border-bottom: 1px solid #21262d; vertical-align: top; }
    td.k { color: #8b949e; width: 46%; }
    details { margin-top: 10px; }
    summary { cursor: pointer; color: #8b949e; font-size: 12px; }
    pre { background: #0d1117; border: 1px solid #21262d; border-radius: 8px; padding: 10px;
          font-size: 11px; overflow-x: auto; color: #7ee787; }
    img.sheet { width: 100%; border-top: 1px solid #30363d; }
    a { color: #58a6ff; }
    """

    parts = []
    parts.append("<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'>")
    parts.append("<meta name='viewport' content='width=device-width, initial-scale=1'>")
    parts.append("<title>RubbleSim demo 渲染结果</title><style>%s</style></head><body>" % style)
    parts.append("<header><h1>RubbleSim · demo 渲染结果</h1>")
    parts.append("<div class='sub'>项目：<code>software/sim/Rubble_Pile_Simulator</code>　"
                 "渲染：Unity 2022.3.62f2 · Linux 播放器 · Xvfb + Vulkan(NVIDIA A800)　"
                 "帧级离线渲染（<code>Time.captureFramerate</code>）保证画面平滑。<br>"
                 "全部脚本与参数位于 <code>tools/demo_render/</code>，未改动项目原有业务逻辑。"
                 "本页由 <code>tools/demo_render/make_gallery.py</code> 生成，服务端口 %d。</div></header>" % args.port)
    parts.append("<main>")

    for r in rows:
        parts.append("<section class='card'>")
        parts.append("<h2>%s</h2>" % html.escape(r["title"]))
        poster = " poster='%s'" % r["poster"] if r["poster"] else ""
        parts.append("<video controls playsinline preload='metadata'%s src='%s'></video>"
                     % (poster, html.escape(r["file"])))
        parts.append("<div class='body'><table>")
        for k, v in r["params"]:
            parts.append("<tr><td class='k'>%s</td><td>%s</td></tr>" % (html.escape(str(k)), html.escape(str(v))))
        parts.append("<tr><td class='k'>文件</td><td><a href='%s'>%s</a></td></tr>"
                     % (html.escape(r["file"]), html.escape(r["file"])))
        parts.append("</table>")
        if r["args"]:
            parts.append("<details><summary>完整命令行参数</summary><pre>%s</pre></details>"
                         % html.escape(r["args"]))
        parts.append("</div>")
        if r["sheet"]:
            parts.append("<img class='sheet' src='%s' alt='contact sheet'>" % html.escape(r["sheet"]))
        parts.append("</section>")

    parts.append("</main></body></html>")

    out = os.path.join(demo, "index.html")
    with open(out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(parts))
    print("wrote " + out + " (%d clips)" % len(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
