# BitCraft Overlay — notes for Claude Code

## Portable app data — clean before every release

App data (settings.json, terrain cache, WebView2 profile, debug logs) lives in a
`Data/` folder **next to the .exe** (`Settings.AppDataRoot` = `AppContext.BaseDirectory/Data`),
not `%LocalAppData%`. This is deliberate — the app is meant to be portable (unzip
anywhere, settings/cache travel with it).

**Before zipping any release build**: delete the `Data/` folder from the publish
output first. It gets created there during local dev/testing (running the exe from
`bin/.../publish` to smoke-test), and if left in place it ships whoever built it own
settings/cache to every user. `.gitignore` already excludes `Data/` from commits —
this is specifically about the *release zip*, a separate step.
