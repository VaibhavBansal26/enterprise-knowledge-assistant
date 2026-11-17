# Demo video

Scripted walkthrough of the running app (Playwright records the browser) with an ElevenLabs voiceover, joined with ffmpeg.
Generated files go to `demo/out/` (git-ignored). Needs Node 22+, ffmpeg, Docker, and the Playwright browsers from `tests/e2e`.

```bash
# 1. Stack with telemetry and the eval report mounted (API on :8090), then the SPA built against it on :5173
docker compose -f docker-compose.yml -f demo/docker-compose.demo.yml --profile full --profile observability up -d --build
(cd web && VITE_API_URL=http://localhost:8090 npm run build && npx vite preview --port 5173 --strictPort &)

# 2. Voiceover (sends only the narration text to ElevenLabs). Key: ELEVENLABS_API_KEY, or a file at
#    ~/.config/elevenlabs/api_key containing the key alone (a pasted "ELEVENLABS_API_KEY=..." line also works).
#    Optional: ELEVENLABS_VOICE_ID, ELEVENLABS_MODEL.
node demo/tts.mjs

# 3. Record every scene. Scenes build on each other (workspace, members, uploads), so start from empty data:
docker compose --profile full --profile observability down -v && docker compose -f docker-compose.yml -f demo/docker-compose.demo.yml --profile full --profile observability up -d
node demo/record.mjs            # or: node demo/record.mjs 13-ops   to redo one read-only scene

# 4. Join video and audio
node demo/assemble.mjs          # -> demo/out/knowledge-assistant-demo.mp4
```

Edit the wording in `narration.json`, then re-run steps 2 to 4. Answers in the video come from the offline stand-in
(`Llm:Provider=extractive`), so no model key is needed to record it. Prerequisite: `evals/results/latest.json`
(`dotnet test --filter Category=Eval`) so the Operations page has scores to show.
