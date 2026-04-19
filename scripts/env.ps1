function Initialize-MiniArchScriptEnvironment {
    function Get-SpecialFolderValue {
        param([System.Environment+SpecialFolder]$Folder)

        $value = [Environment]::GetFolderPath($Folder)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }

        return $null
    }

    $userProfile = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::UserProfile)
    $appData = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::ApplicationData)
    $localAppData = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::LocalApplicationData)
    $programData = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::CommonApplicationData)
    $windowsDir = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::Windows)
    $programFiles = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = Get-SpecialFolderValue -Folder ([System.Environment+SpecialFolder]::ProgramFilesX86)

    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $programFilesX86 = $programFiles
    }

    if ([string]::IsNullOrWhiteSpace($userProfile)) {
        $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE')
    }

    $defaults = [ordered]@{
        'ProgramFiles' = $programFiles
        'ProgramFiles(x86)' = $programFilesX86
        'ProgramW6432' = $programFiles
        'ProgramData' = $programData
        'APPDATA' = $appData
        'LOCALAPPDATA' = $localAppData
        'HOME' = $userProfile
        'SystemDrive' = if ($windowsDir) { Split-Path -Qualifier $windowsDir } else { [Environment]::GetEnvironmentVariable('SystemDrive') }
        'SystemRoot' = $windowsDir
        'windir' = $windowsDir
        'XDG_CONFIG_HOME' = $appData
    }

    foreach ($pair in $defaults.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace($pair.Value)) {
            $current = [Environment]::GetEnvironmentVariable($pair.Key)
            if ([string]::IsNullOrWhiteSpace($current)) {
                Set-Item -Path ("Env:{0}" -f $pair.Key) -Value $pair.Value
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($appData)) {
        $currentNuGet = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES')
        if ([string]::IsNullOrWhiteSpace($currentNuGet)) {
            $fallbackNuGet = Join-Path $userProfile '.nuget\packages'
            if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
                Set-Item -Path 'Env:NUGET_PACKAGES' -Value $fallbackNuGet
            }
        }
    }
}
