"""Apply the explicit software-device opt-in to the pinned upstream checkout.

Ordinary ReShade behavior stays unchanged unless APP/IncludeSoftwareDevices=1.
No driver type, adapter identity or rendering API is changed.
"""
from pathlib import Path
import argparse


def patch(root):
    for version in (10, 11):
        path = root/f'source/d3d{version}/d3d{version}.cpp'
        source = path.read_text(encoding='utf-8')
        if 'DLSS5 software-device opt-in' in source:
            continue
        include = '#include "addon_manager.hpp"'
        if include not in source:
            raise ValueError('Pinned upstream includes changed: '+str(path))
        source = source.replace(include, include+'\n#include "ini_file.hpp"', 1)
        marker = f'\tD3D{version}Device *device_proxy = nullptr;'
        if source.count(marker) != 1:
            raise ValueError('Pinned upstream device creation changed: '+str(path))
        source = source.replace(marker,
            '\t// DLSS5 software-device opt-in: preserve the real WARP/reference device.\n'
            '\tbool include_software_devices = false;\n'
            '\treshade::global_config().get("APP", "IncludeSoftwareDevices", include_software_devices);\n'+marker)
        driver_type = 'D3D10_DRIVER_TYPE' if version == 10 else 'D3D_DRIVER_TYPE'
        old = f'if (DriverType == {driver_type}_WARP || DriverType == {driver_type}_REFERENCE)'
        if source.count(old) != 1:
            raise ValueError('Pinned upstream software gate changed')
        source = source.replace(old, f'if (!include_software_devices && (DriverType == {driver_type}_WARP || DriverType == {driver_type}_REFERENCE))')
        if version == 11:
            old = 'adapter_desc.DeviceId == 0x8C /* Microsoft Basic Render Driver */)'
            if old not in source:
                raise ValueError('Pinned upstream software-adapter gate changed')
            source = source.replace(old, 'adapter_desc.DeviceId == 0x8C /* Microsoft Basic Render Driver */ && !include_software_devices)')
            source += '\n// Capability marker for installers; this is a modified, opt-in ReShade build.\n'
            source += 'extern "C" __declspec(dllexport) unsigned CnrDLSS5SoftwareDevices() noexcept { return 1; }\n'
            source += '#if defined(_M_IX86)\n#pragma comment(linker, "/EXPORT:CnrDLSS5SoftwareDevices=_CnrDLSS5SoftwareDevices")\n#endif\n'
        path.write_text(source, encoding='utf-8')

    # Keep all compiler/shader outputs in the caller's temporary tree.
    common = root/'Common.props'
    source = common.read_text(encoding='utf-8')
    if 'PhoenixBuildRoot' not in source:
        source = source.replace('</PropertyGroup>',
            '  <OutDir Condition="\'$(PhoenixBuildRoot)\'!=\'\'">$(PhoenixBuildRoot)\\bin\\$(Platform)\\$(Configuration)\\</OutDir>\n'
            '  <IntDir Condition="\'$(PhoenixBuildRoot)\'!=\'\'">$(PhoenixBuildRoot)\\obj\\$(ProjectName)\\$(Platform)\\$(Configuration)\\</IntDir>\n'
            '  </PropertyGroup>', 1)
        common.write_text(source, encoding='utf-8')
    project = root/'ReShade.vcxproj'
    source = project.read_text(encoding='utf-8')
    source = source.replace('<ObjectFileOutput>res\\shaders\\%(Filename).cso</ObjectFileOutput>',
        '<ObjectFileOutput>$(PhoenixBuildRoot)\\res\\shaders\\%(Filename).cso</ObjectFileOutput>')
    if '<AdditionalIncludeDirectories>$(PhoenixBuildRoot)' not in source:
        source = source.replace('<ResourceCompile>\n', '<ResourceCompile>\n      <AdditionalIncludeDirectories>$(PhoenixBuildRoot)\\res;%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>\n')
    project.write_text(source, encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('checkout', type=Path)
    args = parser.parse_args()
    patch(args.checkout.resolve())
