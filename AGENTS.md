# Engineering rules

- Preserve HARMOVUE branding, the music and video libraries, one playback service and two independent queues.
- Original media is read-only. Do not save tags back to files or add implicit deletion of user media. Fixtures and test profiles must be isolated.
- Use mature playback libraries; do not replace libmpv with a homemade decoder.
- On the original development machine, keep new projects, tools, caches and generated files on E:, with temporary work under E:\Codex\Temp. Public contributors may configure paths for their environment; scripts must not silently overwrite existing output or Data.
- Visual quality, smooth core interaction and stable behavior are all acceptance requirements. Preserve the approved light/dark glass direction. If the local Unslop skill and policy are available, read them before UI work; do not claim to have used a missing skill.
- Inspect the actual Windows application for UI changes, including normal/narrow windows, empty/loading/error states and keyboard focus. Include representative native screenshots; static mockups do not prove real playback or interaction.
- Keep cold-start, warm-path, backend PTS, PCM, physical audio and visible picture evidence separate. Compilation, assertions and synthetic traces do not replace real playback.
- Public source does not automatically clear bundled third-party binaries for release. Keep licenses, versions and source correspondence accurate. Do not commit personal paths, private diagnostics, credentials, media, Data, binaries or local maintenance archives.
