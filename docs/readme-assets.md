# How the README images are made

The screenshots and the full-flow GIF are taken from a real deployment (the local k3d cluster, see [kubernetes.md](kubernetes.md)) right
after a demo reset, so they show the seeded data and nothing a test left behind. Back to the [README](../README.md).

## 1. Fresh demo data

```powershell
kubectl --context k3d-procure -n procure create job "readme-reset-$(Get-Date -Format HHmmss)" --from=cronjob/demo-reset
```

Wait for the Job to complete (about 1.5 minutes); its log ends with `Demo reset and seed took ... exit code 0`.

## 2. The full-flow GIF

The GIF is the end-to-end test `FullFlowTests.Tom_requests_maria_approves_order_is_received_invoiced_and_paid`, recorded. Two opt-in
settings of the test fixture make that possible: `PROCUREAI_E2E_VIDEO_DIR` records a video of every page
(`<test>-<person>.<page start>-<context end>.webm`, Unix milliseconds), and `PROCUREAI_E2E_SLOW_MO_MS` slows every browser action
down so a viewer can follow it.

```powershell
$env:PROCUREAI_E2E_GATEWAY_URL = "https://procure.localtest.me"
$env:PROCUREAI_E2E_VIDEO_DIR = "$env:TEMP\procure-video"
$env:PROCUREAI_E2E_SLOW_MO_MS = "600"
dotnet run --project tests/ProcureAi.EndToEndTests -- -method "*FullFlowTests*"
./tools/readme/New-FullFlowGif.ps1 -VideoDirectory $env:TEMP\procure-video
```

Each person has their own browser, so there are three videos. [`New-FullFlowGif.ps1`](../tools/readme/New-FullFlowGif.ps1) (ffmpeg,
`winget install Gyan.FFmpeg`) cuts them into one story with a caption per step, at 6 frames a second and 960 px wide. The cut points are
seconds into each person's video and depend on the run: the page start times in the file names show where one person's video sits
on the others' timeline, and a contact sheet (`ffmpeg -i <video> -vf "fps=1,scale=320:-1,tile=8x6" -frames:v 1 sheet.png`) shows
what happens when. Pass new ones with `-Cuts`. Keep the GIF under 5 MB.

## 3. The screenshots

[`tools/readme/screenshots.js`](../tools/readme/screenshots.js) signs in with the demo buttons, opens each screen in a light theme at
1440 x 900 and saves `docs/screenshots/<name>.png`. It needs `playwright-core` (for example the copy inside the global `@playwright/cli`)
and a Chromium:

```powershell
$env:PLAYWRIGHT_CORE = "$(npm root -g)\@playwright\cli\node_modules\playwright-core"
$env:ARGOCD_PASSWORD = kubectl --context k3d-procure -n argocd get secret argocd-initial-admin-secret -o jsonpath='{.data.password}' |
  ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) }
node tools/readme/screenshots.js docs/screenshots                 # every shot
node tools/readme/screenshots.js docs/screenshots assistant budgets # just these
Remove-Item Env:ARGOCD_PASSWORD
```

Look at every image before committing it: no personal data (the demo users are fictitious; the Argo CD application tree shows commit
authors, so it is not used), nothing clipped, amounts on one line. The older screenshots (`dev-panel.png`, `invoice-scan.png`,
`reconnecting.png`) were taken under the AppHost at 1100 to 1280 px and are only used in the docs pages.
