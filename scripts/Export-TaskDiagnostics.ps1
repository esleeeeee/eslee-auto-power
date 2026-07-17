param(
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$taskPath = '\eslee\AutoPower\'
$rows = foreach ($task in Get-ScheduledTask -TaskPath $taskPath) {
    $info = Get-ScheduledTaskInfo -InputObject $task
    [pscustomobject]@{
        TaskName = $task.TaskName
        State = [string]$task.State
        Principal = $task.Principal.UserId
        LogonType = [string]$task.Principal.LogonType
        RunLevel = [string]$task.Principal.RunLevel
        LastRunTime = $info.LastRunTime
        LastTaskResult = $info.LastTaskResult
        NextRunTime = $info.NextRunTime
        Xml = Export-ScheduledTask -TaskName $task.TaskName -TaskPath $taskPath
    }
}

$payload = [pscustomobject]@{
    CapturedAt = [DateTimeOffset]::Now
    ComputerName = $env:COMPUTERNAME
    Tasks = @($rows)
}

$directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($directory) | Out-Null
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    ($payload | ConvertTo-Json -Depth 10),
    [Text.UTF8Encoding]::new($false))
