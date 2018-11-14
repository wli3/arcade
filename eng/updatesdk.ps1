[CmdletBinding(PositionalBinding=$false)]
Param(
  [string] $BarToken,
  [string] $GithubPat
)


$ErrorActionPreference = "Stop"
. $PSScriptRoot\common\tools.ps1

function Check-ExitCode ($exitCode)
{
  if ($exitCode -ne 0) {
    Write-Host "Arcade self-build failed"
    ExitWithExitCode $exitCode
  }
}

try {
  Write-Host "Starting Arcade SDK Package Update"
  Write-Host "STEP 1: Build and create local packages"
  Push-Location $PSScriptRoot
  $packagesSource = "$PSScriptRoot\..\artifacts\packages\debug\NonShipping"
  . .\common\build.ps1 -restore -build -pack
  Check-ExitCode $lastExitCode

  Write-Host "STEP 2: Build using the local packages"
  $packagesSource = Resolve-Path $packagesSource
  "Adding local nuget source..."
  nuget sources add -Name local -Source $packagesSource

  Write-Host "Updating Dependencies using Darc..."
  . .\common\darc-init.ps1
  darc update-dependencies --packages-folder $packagesSource --password $BarToken --github-pat $GithubPat
  Check-ExitCode $lastExitCode

  Stop-Process -Name "dotnet"

  Write-Host "Building with updated dependencies"
  . $PSScriptRoot\common\build.ps1 -configuration $Configuration -restore -build -test -sign -restoreSources $packagesSource
  Check-ExitCode $lastExitCode
}
catch {
  Write-Host $_
  Write-Host $_.Exception
  Write-Host $_.ScriptStackTrace
  ExitWithExitCode 1
}
finally {
  Write-Host "Cleaning up workspace for official build..."
  nuget sources remove -Name local
  # TODO: Might be enough to clean up the artifacts folder?
  git clean -dfx
  git checkout -- Version.Details.xml
  git checkout -- Versions.props
  git checkout ../global.json
  stop-process -Name "dotnet"
  Remove-Item -Path $packagesSource -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
  Pop-Location
  Write-Host "Finished building Arcade SDK with updated packages"
}







