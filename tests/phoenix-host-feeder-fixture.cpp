#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "../source/host/phoenix-feeder-api.h"

#ifndef PHOENIX_TEST_ABI
#define PHOENIX_TEST_ABI 1
#endif

#ifndef PHOENIX_TEST_NO_VERSION
extern "C" __declspec(dllexport) uint32_t WINAPI CnrPHOENIXFeederVersion()
{
    return PHOENIX_TEST_ABI;
}
#endif

#ifndef PHOENIX_TEST_NO_EVALUATE
extern "C" __declspec(dllexport) HRESULT WINAPI
CnrPHOENIXFeederEvaluate(const PhoenixFeederFrameV1 *frame, PhoenixFeederResultV1 *result)
{
    if (!frame || !result || !PhoenixFeederContractV1(*frame, *result)) return E_INVALIDARG;
    // CPU fixture validates the exported calling convention without GPU objects.
    result->status = PhoenixFeederStatusV1::Recorded;
    result->recorded_frames = 1;
    return S_OK;
}
#endif
