<#
 .SYNOPSIS
  Solution helpers.

 .DESCRIPTION
  Commands for managing nugets. 
  These methods may be executed from the `Developer PowerShell` terminal window.
  Import-Module ./solution-helper
#>

function Publish-Packages() {

	try {
		Write-Host "Building and publishing packages"
		msbuild -v:m -p:PublishPackage=true
	}
	catch {
		Write-Error "Publish-Packages failed. Make sure you are executing from a `Developer PowerShell` session."
	}
}

function Clean-Packages() {
    Param(
        [string] $Name = 'hyperbee.',
        [int] $Keep = 10,
        [string] $ToolsFolder = "C:\Development\Tools",
        [string] $Source = 'proget'
    )

	Write-Host "Collecting outdated packages from $source ..."

    # get the nuget cli
    $exePath = "$ToolsFolder/Nuget.exe"

    if ( !(Test-Path $ToolsFolder) ) {
        Write-Host "Creating tools folder '$ToolsFolder'"
        mkdir $toolsFolder | Out-Null
    }
    
    if ( !(Test-Path $exePath) ) {
        Write-Host 'Downloading nuget.exe'
        Invoke-WebRequest https://dist.nuget.org/win-x86-commandline/latest/nuget.exe -OutFile $exePath
    }

    # get unique packages
    $packages = & $exePath list -source $Source -prerelease $Name

    foreach( $package in $packages ) {
        $packageName = $package.split()[0]  # format is "packagename latestversion"

        # get all versions for this package
        $items = & $exePath list -source $Source -prerelease -allversions $packageName | Sort-Object
        Write-Host "Cleaning '$packageName'. $($items.Count) Packages."
        
        if ( $items.Count -gt $Keep ) {
            $removeCount = $items.Count - $Keep
            Write-Host "$removeCount Packages will be removed."

            foreach( $p in ($items | Select-Object -Skip $Keep ) ) {
                $pi = $p.split()
                & $exePath delete $pi[0] $pi[1] -source $Source -noninteractive
            }
        }
    }
}

Export-ModuleMember -Function 'Publish-Packages'
Export-ModuleMember -Function 'Remove-Packages'
