param(
  [Parameter(Mandatory = $true)]
  [string]$PptxPath,

  [string]$OutputDir = "",
  [string]$Voice = "",
  [int]$DefaultSecondsPerSlide = 5
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$PathValue) {
  $resolved = Resolve-Path -LiteralPath $PathValue -ErrorAction Stop
  return $resolved.Path
}

function Get-SlideNotes($slide) {
  $texts = New-Object System.Collections.Generic.List[string]
  foreach ($shape in $slide.NotesPage.Shapes) {
    try {
      if ($shape.HasTextFrame -and $shape.TextFrame.HasText) {
        $text = $shape.TextFrame.TextRange.Text.Trim()
        if ($text -and $text -notmatch '^\d+$') {
          $texts.Add($text)
        }
      }
    } catch {
      continue
    }
  }
  return ($texts -join "`n").Trim()
}

$pptx = Resolve-FullPath $PptxPath
if (-not $OutputDir) {
  $baseName = [IO.Path]::GetFileNameWithoutExtension($pptx)
  $OutputDir = Join-Path ([IO.Path]::GetDirectoryName($pptx)) "$baseName-video"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$slidesDir = Join-Path $OutputDir "slides"
$audioDir = Join-Path $OutputDir "audio"
$segmentsDir = Join-Path $OutputDir "segments"
New-Item -ItemType Directory -Force -Path $slidesDir, $audioDir, $segmentsDir | Out-Null

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if (-not $ffmpeg) {
  throw "ffmpeg was not found. Install ffmpeg and add it to PATH."
}

Add-Type -AssemblyName System.Speech
$speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
if ($Voice) {
  $speaker.SelectVoice($Voice)
}
$speaker.Rate = 0
$speaker.Volume = 100

$powerPoint = New-Object -ComObject PowerPoint.Application
$powerPoint.Visible = [Microsoft.Office.Core.MsoTriState]::msoTrue
$presentation = $null

try {
  $presentation = $powerPoint.Presentations.Open($pptx, $false, $false, $false)
  $segmentListPath = Join-Path $OutputDir "segments.txt"
  if (Test-Path $segmentListPath) { Remove-Item -LiteralPath $segmentListPath -Force }

  for ($i = 1; $i -le $presentation.Slides.Count; $i++) {
    $slide = $presentation.Slides.Item($i)
    $index = "{0:D3}" -f $i
    $imagePath = Join-Path $slidesDir "slide-$index.png"
    $audioPath = Join-Path $audioDir "slide-$index.wav"
    $segmentPath = Join-Path $segmentsDir "segment-$index.mp4"

    $slide.Export($imagePath, "PNG", 1920, 1080)

    $notes = Get-SlideNotes $slide
    if (-not $notes) {
      $notes = " "
    }

    $speaker.SetOutputToWaveFile($audioPath)
    $speaker.Speak($notes)
    $speaker.SetOutputToDefaultAudioDevice()

    $duration = $DefaultSecondsPerSlide
    if ((Get-Item -LiteralPath $audioPath).Length -gt 44) {
      $probe = & ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $audioPath 2>$null
      if ($probe) {
        $duration = [Math]::Max([double]$probe, $DefaultSecondsPerSlide)
      }
    }

    & ffmpeg -y -loop 1 -framerate 30 -i $imagePath -i $audioPath -t $duration -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $segmentPath | Out-Null
    Add-Content -LiteralPath $segmentListPath -Value "file '$($segmentPath.Replace('\','/'))'"
  }

  $outputVideo = Join-Path $OutputDir "$([IO.Path]::GetFileNameWithoutExtension($pptx)).mp4"
  & ffmpeg -y -f concat -safe 0 -i $segmentListPath -c copy $outputVideo | Out-Null
  Write-Host "Video generated: $outputVideo"
} finally {
  if ($presentation) {
    $presentation.Close()
  }
  $powerPoint.Quit()
  [Runtime.InteropServices.Marshal]::ReleaseComObject($powerPoint) | Out-Null
  $speaker.Dispose()
}
