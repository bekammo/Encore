# PreToolUse (Bash|PowerShell): blocks a push to main, a forced push, and git reset --hard.
# Exit 2 sends the reason on stderr back to Claude and cancels the call.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$hook = $reader.ReadToEnd() | ConvertFrom-Json
$command = [string]$hook.tool_input.command
if (-not $command) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hook.cwd) { $hook.cwd } else { (Get-Location).Path }

function Stop-Call([string]$reason) {
    [Console]::Error.WriteLine("Blocked by .claude/hooks/guard-git.ps1: $reason")
    exit 2
}

# Each piece between ; && || | or a newline is checked on its own.
foreach ($segment in ($command -split '&&|\|\||[;|\r\n]')) {
    $s = $segment.Trim()
    if ($s -notmatch '(^|\s|["''(])git(\.exe)?\s') { continue }

    if ($s -match '\breset\b.*--hard\b') {
        Stop-Call 'git reset --hard discards work. Use git stash, or git reset --keep.'
    }

    if ($s -match '\bpush\b') {
        if ($s -match '(\s)(--force(-with-lease|-if-includes)?\b|-f\b|-[a-zA-Z]*f[a-zA-Z]*\b)' -or $s -match '\s\+\S') {
            Stop-Call 'forced pushes are not allowed. Push a new commit instead.'
        }
        if ($s -match '(\s|:)(refs/heads/)?main(\s|$)') {
            Stop-Call 'pushing to main is not allowed. Push a branch and open a pull request.'
        }
        # A bare `git push` (or `git push origin`) pushes the current branch.
        $afterPush = ($s -replace '^.*?\bpush\b', '').Trim() -split '\s+' | Where-Object { $_ -and -not $_.StartsWith('-') }
        if (@($afterPush).Count -le 1) {
            $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
            if ($branch -eq 'main') {
                Stop-Call 'the current branch is main, so this push would go to main. Push a branch and open a pull request.'
            }
        }
    }
}

exit 0
