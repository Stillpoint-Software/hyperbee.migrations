<#
 .SYNOPSIS
  Solution helpers.

 .DESCRIPTION
  Commands for managing nugets. 
  These methods may be executed from the `Developer PowerShell` terminal window.
  Import-Module ./solution-helper
#>

function Publish-Packages() {
    Param(
        [Parameter(Position = 0)]
        [Alias("c")]
        [string] $Configuration = 'Debug',

        [Alias("t")]
        [string] $Tag = 'local'
    )

	try {
        $Tag = ($Tag -replace '\s+', '').ToLower()
		Write-Host "Building and publishing packages for '$Configuration' with tag '$Tag'."

        $timestamp = [System.DateTime]::UtcNow.ToString( 'yyMMddHHmmss' )

        if ( !$Tag ) {
            Write-Error "Non-semver publication is not supported."
            throw
        }

        dotnet pack --no-build --configuration $Configuration --output ./output --version-suffix "$Tag.$timestamp" -p:PushAfterPack=true
	}
	catch {
		Write-Error "Publish-Packages failed. Make sure you are executing from a `Developer PowerShell` session."
	}
}

function Resize-Feed() {
    Param(
        [string] $Name = '*',
        [int] $Keep = 5,
        [string] $Source = 'local'
    )

	Write-Host "Collecting package versions from $source ..."

    # get unique packages
    $packages = Find-Package $Name -source $Source

    foreach( $package in $packages ) {
        $packageName = $package.Name

        # get all versions for this package

        $versions = Find-Package $packageName -source $source -allversions | Sort-Object

        Write-Host "Found '$packageName'. $($versions.Count) Packages."
        
        if ( $versions.Count -gt $Keep ) {
            $removeCount = $versions.Count - $Keep
            Write-Host "$removeCount Packages will be removed."

            foreach( $p in ($versions | Select-Object -Skip $Keep ) ) {
                dotnet nuget delete $p.Name $p.Version --source $Source --non-interactive
            }
        }
    }
}

function Update-Version() {
   Param(
        [Parameter(Position = 0,Mandatory=$true)]
        [ValidateSet('Major','Minor','Patch', IgnoreCase = $true)]
        [string] $Type,
        [string] $Path = 'Directory.Build.Props'
    )

    try {
        if (!(Test-Path $Path)) {
            Write-Error "The version file '$Path' was not found in the current directory."
            throw
        }

        $Type = (Get-Culture).TextInfo.ToTitleCase($Type) # e.g. convert 'major' to 'Major'
        $propName = $Type + "Version"

        $xml = [xml](Get-Content $Path)
        $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $ns.AddNamespace("ns", $xml.DocumentElement.NamespaceURI)

        $node = $xml.SelectSingleNode("//ns:Project/ns:PropertyGroup[ns:$propName]", $ns)
        $version = $node.$propName -as [Int]
        $node.$propName = ($version + 1) -as [String]

        Write-Host "$propName is now '$($node.$PropName)'."

        $xml.Save($Path)
    }
    catch {
		Write-Error "Update-Version failed. Make sure you are executing from a `Developer PowerShell`."
	}
}

Export-ModuleMember -Function 'Publish-Packages'
Export-ModuleMember -Function 'Resize-Feed'
Export-ModuleMember -Function 'Update-Version'