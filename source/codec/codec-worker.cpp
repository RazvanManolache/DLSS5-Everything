#include "pyrowave-bridge.h"
#include <cstdio>
#include <cstdlib>
#include <vector>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif
static uint32_t get32(const uint8_t *p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }
static void put32(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; ++i) p[i] = uint8_t(v >> (8*i)); }
int main(int argc, char **argv) {
    if (argc != 4) return 1;
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
#endif
    const int w = atoi(argv[1]), h = atoi(argv[2]), budget = atoi(argv[3]);
    void *context = pw_create(w, h, budget);
    uint8_t ready[4]; put32(ready, context ? 1u : 0u); fwrite(ready, 1, 4, stdout); fflush(stdout);
    if (!context) return 2;
    for (;;) {
        uint8_t header[5]; if (fread(header, 1, 5, stdin) != 5) break;
        uint32_t size = get32(header + 1);
        const uint32_t rgba = uint32_t(w * h * 4);
        if ((header[0] != 1 && header[0] != 2) || (header[0] == 1 && size != rgba) ||
            (header[0] == 2 && (size < 8 || size > 16u * 1024 * 1024))) break;
        std::vector<uint8_t> input(size), output(header[0] == 1 ? 16u*1024*1024 : rgba);
        if (fread(input.data(), 1, size, stdin) != size) break;
        int result = header[0] == 1 ? pw_encode(context, input.data(), int(size), output.data(), int(output.size())) :
            pw_decode(context, input.data(), int(size), output.data(), int(output.size()));
        uint8_t reply[4]; put32(reply, result > 0 ? uint32_t(result) : 0u);
        fwrite(reply, 1, 4, stdout);
        if (result > 0) fwrite(output.data(), 1, size_t(result), stdout);
        fflush(stdout); if (result <= 0) break;
    }
    pw_destroy(context); return 0;
}
