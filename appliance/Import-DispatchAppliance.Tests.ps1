# Pester tests for the Hyper-V import helper. The real Hyper-V cmdlets need the Hyper-V role (absent on CI
# runners), so we declare stub functions for them and Pester-Mock those - validating the script's LOGIC
# (folder layout, VLAN tagging, Secure Boot template, validation/guards) without an actual hypervisor.
# Run on windows-latest, where the runner is an Administrator (so the script's access check passes) and the
# Windows identity APIs work. This is NOT a substitute for a live import - it's the part we can automate.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Import-DispatchAppliance.ps1'

    # Stub the Hyper-V cmdlets (with the parameters the script actually passes) so Mock can intercept them.
    function Get-VM { [CmdletBinding()] param($Name) }
    function Get-VMSwitch { [CmdletBinding()] param($Name) }
    function Get-VMHost { [CmdletBinding()] param() }
    function New-VM { [CmdletBinding()] param($Name, $Generation, $MemoryStartupBytes, $VHDPath, $SwitchName, $Path) }
    function Set-VMProcessor { [CmdletBinding()] param($VM, $Count) }
    function Set-VMFirmware { [CmdletBinding()] param($VM, $EnableSecureBoot, $SecureBootTemplate) }
    function Set-VMMemory { [CmdletBinding()] param($VM, $DynamicMemoryEnabled) }
    function Set-VMNetworkAdapterVlan { [CmdletBinding()] param($VMName, [switch]$Access, $VlanId) }
    function Start-VM { [CmdletBinding()] param($VM, $Name) }
}

Describe 'Import-DispatchAppliance (unattended)' {
    BeforeEach {
        $script:Vhdx  = (New-Item -ItemType File -Path (Join-Path $TestDrive 'dispatch-appliance.vhdx') -Force).FullName
        $script:Store = Join-Path $TestDrive 'store'

        Mock Get-VM { $null }                                                  # VM does not already exist
        Mock Get-VMSwitch { [pscustomobject]@{ Name = 'External'; SwitchType = 'External' } }
        Mock Get-VMHost { [pscustomobject]@{ VirtualMachinePath = 'C:\HyperV' } }
        Mock New-VM { [pscustomobject]@{ Name = $Name } }
        Mock Set-VMProcessor {}
        Mock Set-VMFirmware {}
        Mock Set-VMMemory {}
        Mock Set-VMNetworkAdapterVlan {}
        Mock Start-VM {}
        # Create the destination, rather than mocking the copy away to nothing. The script verifies the
        # copy landed (Test-Path -PathType Leaf) before handing the path to New-VM - a real guard, since a
        # silently failed copy would otherwise produce a VM pointed at a missing disk. A no-op mock made
        # that guard throw on every test that got as far as the copy, which is why four of the six here have
        # failed since they were written.
        Mock Copy-Item { New-Item -ItemType File -Path $Destination -Force | Out-Null }
    }

    It 'keeps VM config + disk together under <storage>\<name>' {
        & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'External' -VmPath $Store
        $vmDir = Join-Path $Store 'TestVM'
        Should -Invoke New-VM -Times 1 -ParameterFilter { $Path -eq $vmDir }
        Should -Invoke Copy-Item -Times 1 -ParameterFilter { $Destination -eq (Join-Path $vmDir 'dispatch-appliance.vhdx') }
    }

    It 'tags the adapter with the VLAN when one is given' {
        & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'External' -VmPath $Store -VlanId 20
        Should -Invoke Set-VMNetworkAdapterVlan -Times 1 -ParameterFilter { $VlanId -eq 20 -and $Access }
    }

    It 'leaves the adapter untagged when no VLAN is given' {
        & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'External' -VmPath $Store
        Should -Invoke Set-VMNetworkAdapterVlan -Times 0
    }

    It 'sets the Linux Secure Boot template and disables Dynamic Memory' {
        & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'External' -VmPath $Store
        Should -Invoke Set-VMFirmware -ParameterFilter { $SecureBootTemplate -eq 'MicrosoftUEFICertificateAuthority' }
        Should -Invoke Set-VMMemory  -ParameterFilter { $DynamicMemoryEnabled -eq $false }
    }

    It 'fails clearly when the named switch does not exist' {
        Mock Get-VMSwitch { $null } -ParameterFilter { $Name -eq 'Nope' }
        { & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'Nope' -VmPath $Store } | Should -Throw '*switch*not found*'
    }

    It 'fails when a VM of that name already exists' {
        Mock Get-VM { [pscustomobject]@{ Name = 'TestVM' } }
        { & $ScriptPath -VhdxPath $Vhdx -Name 'TestVM' -SwitchName 'External' -VmPath $Store } | Should -Throw '*already exists*'
    }
}
