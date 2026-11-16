// Generates the voiceover with ElevenLabs text-to-speech: one MP3 per scene, plus out/durations.json for the recorder.
//   Key:   ELEVENLABS_API_KEY, or a file containing just the key (default ~/.config/elevenlabs/api_key, or ELEVENLABS_KEY_FILE)
//   Voice: ELEVENLABS_VOICE_ID (default: the first voice your account lists); model: ELEVENLABS_MODEL (default eleven_multilingual_v2)
// Only the narration text is sent. The key is never printed or written anywhere.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { OUT, narration } from "./lib.mjs";

const keyFile = process.env.ELEVENLABS_KEY_FILE ?? path.join(homedir(), ".config", "elevenlabs", "api_key");
// Accept the bare key, or a pasted ".env" line such as  ELEVENLABS_API_KEY="..."  or  export ELEVENLABS_API_KEY=...
const clean = (t) => t.trim().replace(/^export\s+/, "").replace(/^[A-Z][A-Z0-9_]*\s*=\s*/, "").replace(/^["']|["']$/g, "").trim();
const key = clean(process.env.ELEVENLABS_API_KEY ?? (existsSync(keyFile) ? readFileSync(keyFile, "utf8") : ""));
if (!key) {
  console.error(`No ElevenLabs key found. Set ELEVENLABS_API_KEY, or save the key alone in ${keyFile}`);
  process.exit(1);
}
const api = "https://api.elevenlabs.io/v1";
const headers = { "xi-api-key": key };

async function pickVoice() {
  if (process.env.ELEVENLABS_VOICE_ID) return { id: process.env.ELEVENLABS_VOICE_ID, name: "(from ELEVENLABS_VOICE_ID)" };
  const res = await fetch(`${api}/voices`, { headers });
  // A key restricted to text-to-speech cannot list voices; fall back to ElevenLabs' stock "Rachel" voice.
  if (res.status === 401 || res.status === 403) return { id: "21m00Tcm4TlvDq8ikWAM", name: "Rachel (default; key cannot list voices)" };
  if (!res.ok) throw new Error(`listing voices failed: ${res.status} ${(await res.text()).slice(0, 300)}`);
  const voices = (await res.json()).voices ?? [];
  if (voices.length === 0) throw new Error("this account lists no voices; set ELEVENLABS_VOICE_ID");
  const v = voices.find((x) => /rachel|adam|brian|daniel|george|charlie|sarah/i.test(x.name)) ?? voices[0];
  return { id: v.voice_id, name: v.name };
}

const voice = await pickVoice();
const model = process.env.ELEVENLABS_MODEL ?? "eleven_multilingual_v2";
console.log(`voice: ${voice.name} · model: ${model}`);

const dir = path.join(OUT, "audio");
mkdirSync(dir, { recursive: true });
const durations = {};
const only = process.argv.slice(2);

for (const sc of narration()) {
  const file = path.join(dir, `${sc.id}.mp3`);
  if (only.length === 0 || only.includes(sc.id) || !existsSync(file)) {
    const res = await fetch(`${api}/text-to-speech/${voice.id}?output_format=mp3_44100_128`, {
      method: "POST",
      headers: { ...headers, "Content-Type": "application/json", Accept: "audio/mpeg" },
      body: JSON.stringify({
        text: sc.text,
        model_id: model,
        voice_settings: { stability: 0.5, similarity_boost: 0.75, style: 0.0, use_speaker_boost: true },
      }),
    });
    if (!res.ok) {
      console.error(`✘ ${sc.id}: ${res.status} ${(await res.text()).slice(0, 400)}`);
      process.exit(1);
    }
    writeFileSync(file, Buffer.from(await res.arrayBuffer()));
  }
  const secs = Number(
    execFileSync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]).toString().trim(),
  );
  durations[sc.id] = secs + 0.8; // breathing room after each scene
  console.log(`✓ ${sc.id}  ${secs.toFixed(1)}s`);
}
writeFileSync(path.join(OUT, "durations.json"), JSON.stringify(durations, null, 2));
console.log("wrote out/durations.json. Now re-run: node record.mjs   (on a freshly reset stack), then: node assemble.mjs");
