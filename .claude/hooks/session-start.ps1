# SessionStart: prints the branch, the working tree and whether Docker answers. Stdout is
# added to the session's context, so Claude knows up front whether the test suite can run.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$raw = $reader.ReadToEnd()
$hook = if ($raw.Trim()) { $raw | ConvertFrom-Json } else { $null }
$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hook.cwd) { $hook.cwd } else { (Get-Location).Path }

$branch = & git -C $root rev-parse --abbrev-ref HEAD 2>$null
$status = @(& git -C $root status --short 2>$null)

# docker info hangs for a while when the daemon is down, so it gets 15 seconds.
$out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
try {
    $p = Start-Process docker -ArgumentList 'info' -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    if ($p.WaitForExit(15000)) {
        $docker = if ($p.ExitCode -eq 0) { 'running: the test suite can run (docker compose run --rm --build tests)' }
                  else { 'NOT running: the test suite cannot run until Docker Desktop is started' }
    } else {
        $p.Kill()
        $docker = 'did not answer within 15 s: treat the test suite as unable to run'
    }
} catch {
    $docker = 'not found: the test suite cannot run'
} finally {
    Remove-Item $out, $err -ErrorAction SilentlyContinue
}

"Branch: $branch"
if ($status.Count -eq 0) { 'git status --short: clean' } else { 'git status --short:'; $status | ForEach-Object { "  $_" } }
"Docker: $docker"
exit 0
