// Joins each scene's video with its narration (or silence, for a preview) into out/knowledge-assistant-demo.mp4.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { OUT, narration, durations, targetFor } from "./lib.mjs";

const out = process.argv[2] ?? "knowledge-assistant-demo.mp4";
const known = durations();
const dir = path.join(OUT, "clips");
mkdirSync(dir, { recursive: true });
const ff = (args) => execFileSync("ffmpeg", ["-y", "-loglevel", "error", ...args], { stdio: "inherit" });

const list = [];
for (const sc of narration()) {
  const video = path.join(OUT, "video", `${sc.id}.webm`);
  const meta = path.join(OUT, "video", `${sc.id}.json`);
  if (!existsSync(video)) { console.warn(`skip ${sc.id}: no video`); continue; }
  const { offset, elapsed } = JSON.parse(readFileSync(meta, "utf8"));
  const audio = path.join(OUT, "audio", `${sc.id}.mp3`);
  // Never cut a scene's on-screen action short: if it ran longer than the narration, keep it all (audio is padded with silence).
  const len = Math.max(targetFor(sc, known), elapsed + 0.6);
  const clip = path.join(dir, `${sc.id}.mp4`);
  const audioIn = existsSync(audio) ? ["-i", audio] : ["-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo"];
  ff([
    "-ss", offset.toFixed(2), "-i", video, ...audioIn,
    "-filter_complex", `[0:v]fps=30,scale=1280:720:flags=lanczos,tpad=stop_mode=clone:stop_duration=8,fade=t=in:st=0:d=0.25[v];[1:a]aresample=44100,apad[a]`,
    "-map", "[v]", "-map", "[a]", "-t", len.toFixed(2),
    "-c:v", "libx264", "-crf", "19", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", clip,
  ]);
  list.push(`file '${clip.replaceAll("'", "'\\''")}'`);
  console.log(`✓ ${sc.id}  ${len.toFixed(1)}s ${existsSync(audio) ? "with voiceover" : "(silent preview)"}`);
}
const listFile = path.join(OUT, "clips.txt");
writeFileSync(listFile, list.join("\n"));
const dest = path.join(OUT, out);
ff(["-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", dest]);
const total = execFileSync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", dest]).toString().trim();
console.log(`\n${dest}\n${(Number(total) / 60).toFixed(1)} minutes`);
