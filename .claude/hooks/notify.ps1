# Notification: shows a Windows toast with Claude's message, or beeps if a toast can't be shown.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$raw = $reader.ReadToEnd()
$hook = if ($raw.Trim()) { $raw | ConvertFrom-Json } else { $null }
$message = if ($hook.message) { [string]$hook.message } else { 'Claude Code needs your attention' }

try {
    [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]

    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $escaped = [Security.SecurityElement]::Escape($message)
    $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>Claude Code - Encore</text><text>$escaped</text></binding></visual></toast>")

    # Windows PowerShell's own app id, so the toast needs no app registration.
    $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show(
        [Windows.UI.Notifications.ToastNotification]::new($xml))
} catch {
    [Console]::Beep(880, 300)
}

exit 0
