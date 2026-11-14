# Registers (or removes) the Windows scheduled task that starts the reader's systemd unit inside WSL.
# Run by install.sh / uninstall.sh; see "Running it every day" in pc-reader/README.md.
#
# Why Windows and not a systemd timer: WSL starts lazily, and a user timer only fires while the distro is up. Task Scheduler
# is what is running when the PC is on, so it is the trigger; `wsl.exe` starts the distro (and its systemd) if it is down,
# and keeps it up until the run is over.
param(
  [Parameter(Mandatory = $true)][ValidateSet('install', 'uninstall')][string]$Mode,
  [string]$Distro = 'Ubuntu',
  [string]$LinuxUser = '',
  [string]$Unit = 'basket-pc-reader.service'
)
$ErrorActionPreference = 'Stop'
$taskName = 'Basket PC reader (daily)'

if ($Mode -eq 'uninstall') {
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Output "removed task '$taskName'"
  } else {
    Write-Output "no task '$taskName'"
  }
  return
}

# `systemctl start` on a oneshot unit returns when the run is over; a trigger that fires while one is running just waits for
# it (and the reader's once-a-day stamp makes the second one a no-op). `conhost.exe --headless` hosts the console app without
# a window, so nothing flashes or stays open on the desktop.
$userArg = if ($LinuxUser) { "-u $LinuxUser " } else { '' }
$action = New-ScheduledTaskAction -Execute 'conhost.exe' `
  -Argument "--headless wsl.exe -d $Distro $userArg--exec /usr/bin/systemctl --user start $Unit"

$me = "$env:USERDOMAIN\$env:USERNAME"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# Several triggers on purpose, the reader reads at most once a local day whatever fires:
#  - at logon (the PC just came on; the delay lets Tailscale, which runs inside WSL, come up);
#  - at 12:00, repeated every 3 hours for 9 hours, which both covers a PC that was off at logon time and retries a run that
#    found basket unreachable. StartWhenAvailable runs a missed 12:00 as soon as the PC is back.
$logon = New-ScheduledTaskTrigger -AtLogOn -User $me
$logon.Delay = 'PT2M'
$daily = New-ScheduledTaskTrigger -Daily -At '12:00'
$daily.Repetition = (New-ScheduledTaskTrigger -Once -At '12:00' -RepetitionInterval (New-TimeSpan -Hours 3) -RepetitionDuration (New-TimeSpan -Hours 9)).Repetition
$triggers = @($logon, $daily)

if ($admin) {
  # Elevated install only: Windows refuses boot triggers and password-less "run whether logged on or not" (S4U) to an
  # ordinary user. With them the reader runs even if nobody has logged in since the PC came on.
  $boot = New-ScheduledTaskTrigger -AtStartup
  $boot.Delay = 'PT5M'
  $triggers += $boot
  $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType S4U -RunLevel Limited
  $how = 'runs whether or not anyone is logged on (boot, logon and 12:00 triggers)'
} else {
  $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
  $how = 'runs while the user is logged on (logon and 12:00 triggers); install from an elevated PowerShell for boot-time runs with nobody logged in'
}

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
  -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $settings `
  -Principal $principal -Description 'Reads Aldi and Dunnes prices into basket once a day (pc-reader, WSL).' -Force | Out-Null
Write-Output "registered task '$taskName': $how"
