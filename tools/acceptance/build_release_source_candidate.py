"""Build a release-compatible fix: tagged core protocol, unchanged published kernels/runners."""
from pathlib import Path
import hashlib,io,json,shutil,subprocess,sys,tarfile,tempfile,zipfile
APP=Path(__file__).resolve().parents[2];BASE=APP.parent;PROJECT=BASE/'dlss5-phoenix-addon';OUT=APP/'local-build/validation-current'
sys.path.insert(0,str(BASE/'dlss5-everything-testing/tools'))
from live_candidate import candidate_with_telemetry
destination=PROJECT/'artifacts/current/everything-fixes';archive=PROJECT/'artifacts/current/release-0.81/DLSS5-Phoenix-0.81-Windows.zip'
release=json.loads((OUT/'github-release.json').read_text(encoding='utf-8-sig'))
with archive.open('rb') as stream:assert 'sha256:'+hashlib.file_digest(stream,'sha256').hexdigest()==next(a['digest'] for a in release['assets'] if a['name']==archive.name)
commit=subprocess.check_output(['git','rev-parse','0.81^{commit}'],cwd=PROJECT,text=True).strip()
assert commit==release['target_commitish']
with tempfile.TemporaryDirectory(prefix='release-source-fixes-',dir=PROJECT/'build') as directory:
    scratch=Path(directory);snapshot=scratch/'source';snapshot.mkdir();baseline=scratch/'release';baseline.mkdir()
    with zipfile.ZipFile(archive) as z:z.extractall(baseline)
    tree=subprocess.check_output(['git','archive','0.81'],cwd=PROJECT)
    with tarfile.open(fileobj=io.BytesIO(tree)) as tar:tar.extractall(snapshot,filter='data')
    (snapshot/'build').mkdir(exist_ok=True)
    changed={}
    for relative in ('src/integration/compatibility/host/dlss5-feed-host64.cpp',
        'src/integration/compatibility/host/phoenix-host-config.h','src/integration/reshade/feeder_api.h',
        'src/integration/compatibility/readback_bridge.h','src/integration/compatibility/pixel_transport.h',
        'tests/native/graphics_transport_tests.cpp','tests/native/module_capability_tests.cpp',
        'src/native/support/locked_capture.h','native_runtime/device_dependencies.h','tools/build_addon.py'):
        source=PROJECT/relative;target=snapshot/relative;target.write_bytes(source.read_bytes())
        changed[relative]=hashlib.sha256(target.read_bytes()).hexdigest()
    addon=snapshot/'src/integration/reshade/addon.cpp';text=addon.read_text(encoding='utf-8')
    old='if (status == "presenting_reprojected_result")';assert old in text
    text=text.replace(old,'if (status == "composing_reprojected_result" || status == "presenting_reprojected_result")')
    current_addon=(PROJECT/'src/integration/reshade/addon.cpp').read_text(encoding='utf-8')
    begin=current_addon.index('extern "C" __declspec(dllexport) HRESULT WINAPI CnrPHOENIXFeederEvaluateLocked(',current_addon.index('CnrPHOENIXFeederEvaluate(const'))
    end=current_addon.index('// Optional explicit engine registration.',begin)
    text=text.replace('// Optional explicit engine registration.',current_addon[begin:end]+'// Optional explicit engine registration.')
    addon.write_text(text,encoding='utf-8')
    def patch(relative,old,new):
        path=snapshot/relative;text=path.read_text(encoding='utf-8');assert text.count(old)==1,(relative,old)
        path.write_text(text.replace(old,new),encoding='utf-8')
    patch('src/native/support/executor.h','#include "routing.h"','#include "routing.h"\n#include "locked_capture.h"')
    patch('src/native/support/executor.h','uint64_t serial = 0, completed_serial = 0;','uint64_t serial = 0, completed_serial = 0;\n    uint64_t locked_input_token = 0;')
    patch('src/native/support/production_direct.h','frame.completed_serial = done;','frame.completed_serial = done;\n                frame.locked_input_token = locked_capture_token;')
    patch('src/native/support/offload.h','uint64_t serial = 0, tick = 0;','uint64_t serial = 0, tick = 0;\n        uint64_t locked_input_token = 0;')
    patch('src/native/support/offload.h','uint64_t max_ms_, last_serial_ = 0, capture_fence_ = 0, upload_fence_ = 0;','uint64_t max_ms_, last_serial_ = 0, capture_fence_ = 0, upload_fence_ = 0;\n    uint64_t current_locked_input_ = 0;')
    patch('src/native/support/offload.h','serial - slot.serial <= max_frames_ && tick - slot.tick <= max_ms_;','capture_age_valid(slot.locked_input_token, current_locked_input_, serial-slot.serial, tick-slot.tick, max_frames_, max_ms_);')
    patch('src/native/support/offload.h','last_serial_ = frame.serial;','last_serial_ = frame.serial;\n        current_locked_input_ = frame.locked_input_token;')
    patch('src/native/support/offload.h','slot.tick = tick;','slot.tick = tick;\n                slot.locked_input_token = frame.locked_input_token;')
    patch('src/native/support/offload.h','pixel[channel] = result_[iteration_index * 3 + channel] -\n                                                 color_[iteration_index * 3 + channel];\n                                residual_mean_ += std::abs(pixel[channel]);',
          'const float residual = result_[iteration_index * 3 + channel] -\n                                                       color_[iteration_index * 3 + channel];\n                                pixel[channel] = residual;\n                                residual_mean_ += std::abs(residual);')
    patch('src/native/support/offload.h','std::max(residual_max_, double(std::abs(pixel[channel])))','std::max(residual_max_, double(std::abs(residual)))')
    patch('native_runtime/native_execution_client.h','#include "broker_client.h"','#include "broker_client.h"\n#include "device_dependencies.h"')
    patch('native_runtime/native_execution_client.h','if (!valid_execution_device(device_id) || device_id == "native")\n                continue;',
          'if (!valid_execution_device(device_id) || device_id == "native")\n                continue;\n            if (!device_dependencies_present(root, config, device_id)) continue;')
    panel=snapshot/'src/integration/reshade/shared_panel.h';text=panel.read_text(encoding='utf-8')
    current=(PROJECT/'src/integration/reshade/shared_panel.h').read_text(encoding='utf-8')
    begin=text.index('            changed =\n                ImGui::Combo("Upscaler"')
    end=text.index('            if (addon_state.panel.upscaler)',begin)
    replacement=current[current.index('            if (ImGui::BeginCombo("Upscaler"'):current.index('            if (addon_state.panel.upscaler)',current.index('            if (ImGui::BeginCombo("Upscaler"'))]
    panel.write_text(text[:begin]+replacement+text[end:])
    # Source-native diagnostics are absent from the old core; add transport
    # timings to its control snapshot without changing neural/runtime protocols.
    controller=snapshot/'src/integration/compatibility/controller.h';text=controller.read_text(encoding='utf-8')
    marker='        {"delivered", delivered(value)}, {"presented", presented(value)},';assert marker in text
    controller.write_text(text.replace(marker,marker+'\n        {"transfer_cpu_ms", value.readback ? value.readback->transfer_timings() : Json::object()},'))
    with candidate_with_telemetry(snapshot,baseline,BASE/'dlss5-everything-testing/games/dx11-x64/dxgi.dll',BASE/'external/Streamline/external/ngx-sdk/include',scratch) as (package,receipt):
        for name in ('Phoenix.addon64','Phoenix.addon32','phoenix_graphics_host.exe','FILES_SHA256.json'):shutil.copy2(package/name,destination/name)
        receipt.update(candidate_kind='public-0.81-compatible-source-hotfix',public_baseline='0.81',source_commit=commit,
            core_rebuilt=True,runners_rebuilt=False,kernels_rebuilt=False,model_included=False,patched_source_hashes=changed)
        (destination/'validation.json').write_text(json.dumps(receipt,indent=2));(OUT/'release-source-fixes-build.json').write_text(json.dumps(receipt,indent=2))
print(json.dumps(receipt))
