<#
.SYNOPSIS
  Cuts the three page videos of the full-flow end-to-end test into the README GIF (docs/screenshots/full-flow.gif).

.DESCRIPTION
  Run the test first with PROCUREAI_E2E_VIDEO_DIR (and PROCUREAI_E2E_SLOW_MO_MS=600 so a viewer can follow it); see
  docs/readme-assets.md. Each video is named <test>-<person>.<page start>-<context end>.webm (Unix milliseconds), so the cut points
  below are seconds into each person's own video. They depend on the run: check a contact sheet of each video and adjust them
  (-Cuts). The output must stay under 5 MB.

.EXAMPLE
  ./tools/readme/New-FullFlowGif.ps1 -VideoDirectory $env:TEMP\procure-video -Output docs/screenshots/full-flow.gif
#>
param(
    [Parameter(Mandatory)] [string] $VideoDirectory,
    [string] $Output = "docs/screenshots/full-flow.gif",
    # person, from (s), to (s), caption: in story order.
    [object[]] $Cuts = @(
        @("tom", 0.7, 7, "Tom (requester) raises a request"),
        @("maria", 0.7, 4, "Maria (manager) approves it"),
        @("tom", 9, 16.5, "Order sent, confirmed, delivery recorded"),
        @("thandi", 1.8, 3.5, "Thandi (finance) sees the matched invoice"),
        @("thandi", 12.5, 17.5, "Thandi (finance) pays it"),
        @("tom", 29.5, 35.2, "Completed, every data change audited")
    ),
    [int] $Fps = 6,
    [int] $Width = 960
)
$ErrorActionPreference = "Stop"
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw "ffmpeg is not on PATH (winget install Gyan.FFmpeg)." }

$people = @("tom", "maria", "thandi")
$inputs = foreach ($person in $people) {
    $file = Get-ChildItem $VideoDirectory -Filter "*-$person.*.webm" | Select-Object -First 1
    if (-not $file) { throw "No video for $person in $VideoDirectory." }
    $file.FullName
}

# drawtext needs a font file; a relative path avoids escaping the drive colon inside the filter graph.
$work = Join-Path ([IO.Path]::GetTempPath()) "procure-gif-$PID"
New-Item -ItemType Directory $work -Force | Out-Null
Copy-Item "$env:WINDIR\Fonts\segoeuib.ttf" (Join-Path $work "font.ttf")

$chains = @(); $labels = ""
for ($i = 0; $i -lt $Cuts.Count; $i++) {
    $person, $from, $to, $caption = $Cuts[$i]
    $index = [array]::IndexOf($people, $person)
    $pad = if ($i -eq $Cuts.Count - 1) { "tpad=stop_mode=clone:stop_duration=2.5," } else { "" }
    $text = "drawtext=fontfile=font.ttf:text='$caption':x=w-tw-24:y=h-th-24:fontsize=28:fontcolor=white:box=1:boxcolor=0x312e81@0.92:boxborderw=12"
    $chains += "[${index}:v]trim=${from}:${to},setpts=PTS-STARTPTS,$pad$text[s$i]"
    $labels += "[s$i]"
}
$graph = ($chains -join ";") + ";${labels}concat=n=$($Cuts.Count):v=1:a=0,fps=$Fps,scale=${Width}:-1:flags=lanczos,split[x][y];" +
    "[x]palettegen=max_colors=96:stats_mode=diff[p];[y][p]paletteuse=dither=none:diff_mode=rectangle"

$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
Push-Location $work
try {
    ffmpeg -v error -y -i $inputs[0] -i $inputs[1] -i $inputs[2] -filter_complex $graph -loop 0 $target
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
    Remove-Item $work -Recurse -Force
}
$size = (Get-Item $target).Length
"{0}: {1:N1} MB" -f $target, ($size / 1MB)
if ($size -ge 5MB) { Write-Warning "Over 5 MB: lower -Fps or -Width, or shorten the cuts." }
