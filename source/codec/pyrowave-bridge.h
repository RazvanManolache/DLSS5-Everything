#pragma once
#include <stdint.h>
#ifdef _WIN32
#ifdef PW_BUILD
#define PW_API __declspec(dllexport)
#else
#define PW_API __declspec(dllimport)
#endif
#else
#define PW_API __attribute__((visibility("default")))
#endif
// The bridge owns one GPU encoder and decoder. Serialize all calls, including destroy.
extern "C" {
PW_API void *pw_create(int width, int height, int frame_budget);
// Thread-local, non-sensitive initialization diagnostic; never contains paths or frames.
PW_API const char *pw_create_error();
PW_API int pw_encode(void *context, const uint8_t *rgba, int rgba_bytes, uint8_t *output, int capacity);
PW_API int pw_decode(void *context, const uint8_t *input, int input_bytes, uint8_t *rgba, int rgba_bytes);
PW_API void pw_destroy(void *context);
}
