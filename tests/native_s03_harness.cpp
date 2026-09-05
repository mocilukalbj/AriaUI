#include <iostream>
#include <cassert>
#include <cstring>
#include <vector>
#include "../native/bridge/aria2_bridge.h"

int main() {
    std::cout << "=== S03 Native ASan/UBSan Diagnostic Harness ===" << std::endl;

    // 1. Check ABI Version
    uint32_t abi_ver = a2_bridge_get_abi_version();
    assert(abi_ver == 1);
    std::cout << "[S03 Native] ABI Version verified: " << abi_ver << std::endl;

    // 2. Initialize engine
    A2InitOptions initOpts;
    std::memset(&initOpts, 0, sizeof(initOpts));
    initOpts.struct_size = sizeof(A2InitOptions);
    initOpts.abi_version = 1;
    initOpts.keep_running = 1;
    initOpts.use_signal_handler = 0;
    initOpts.download_dir = "/tmp";
    initOpts.session_file = nullptr;

    const char* keys[] = { "max-download-limit", "save-session-interval" };
    const char* vals[] = { "0", "30" };
    initOpts.option_keys = keys;
    initOpts.option_values = vals;
    initOpts.option_count = 2;

    A2SessionHandle session = nullptr;
    int32_t initRes = a2_engine_init(&initOpts, &session);
    assert(initRes == A2_STATUS_OK);
    assert(session != nullptr);
    std::cout << "[S03 Native] Engine initialized successfully." << std::endl;

    // 3. Test Global Stat
    A2GlobalStat stat;
    std::memset(&stat, 0, sizeof(stat));
    stat.struct_size = sizeof(A2GlobalStat);
    int32_t statRes = a2_engine_get_global_stat(session, &stat);
    assert(statRes == A2_STATUS_OK);
    std::cout << "[S03 Native] GlobalStat active: " << stat.num_active << std::endl;

    // 4. Test Add URI
    uint64_t gid = 0;
    const char* headers[] = { "User-Agent: AriaUI-S03-Test/1.0", "X-Custom-Header: True" };
    int32_t addRes = a2_download_add_uri(session, "http://192.0.2.1/s03-asan-test.bin", headers, 2, "/tmp", "s03-test.bin", &gid);
    assert(addRes == A2_STATUS_OK);
    assert(gid != 0);
    std::cout << "[S03 Native] Add URI succeeded, GID: " << std::hex << gid << std::dec << std::endl;

    // 5. Test GID Conversion
    char hex16[17] = {0};
    a2_gid_to_hex(gid, hex16);
    assert(std::strlen(hex16) == 16);
    uint64_t convertedBack = a2_hex_to_gid(hex16);
    assert(convertedBack == gid);

    // 6. Test Pause & Unpause
    int32_t pauseRes = a2_download_pause(session, gid, 0);
    assert(pauseRes >= 0);

    int32_t unpauseRes = a2_download_unpause(session, gid);
    assert(unpauseRes >= 0);

    // 7. Test Poll Events
    A2EngineEvent events[16];
    for (int i = 0; i < 16; i++) {
        events[i].struct_size = sizeof(A2EngineEvent);
    }
    uint32_t eventCount = 0;
    int32_t pollRes = a2_engine_poll_events(session, events, 16, &eventCount);
    assert(pollRes == A2_STATUS_OK);
    std::cout << "[S03 Native] Polled events count: " << eventCount << std::endl;

    // 8. Test Options Inspection & Modification
    const char* optKeys[] = { "max-download-limit" };
    const char* optVals[] = { "1048576" };
    int32_t changeOptRes = a2_download_change_option(session, gid, optKeys, optVals, 1);
    assert(changeOptRes == A2_STATUS_OK);

    // Buffer too small query protocol check
    uint32_t reqLen = 0;
    int32_t queryLenRes = a2_download_get_option_value(session, gid, "max-download-limit", nullptr, 0, &reqLen);
    assert(queryLenRes == A2_STATUS_BUFFER_TOO_SMALL || queryLenRes == A2_STATUS_OK);
    if (reqLen > 0) {
        std::vector<char> optBuf(reqLen + 1);
        uint32_t actualLen = 0;
        int32_t fullQueryRes = a2_download_get_option_value(session, gid, "max-download-limit", optBuf.data(), reqLen + 1, &actualLen);
        assert(fullQueryRes == A2_STATUS_OK);
    }

    // 9. Test Remove & Purge
    int32_t removeRes = a2_download_remove(session, gid, 1);
    assert(removeRes >= 0);

    uint32_t purgedCount = 0;
    int32_t purgeRes = a2_download_purge_results(session, &purgedCount);
    assert(purgeRes >= 0);

    // 10. Shutdown and Destroy
    int32_t shutdownRes = a2_engine_shutdown(session, 1);
    assert(shutdownRes == A2_STATUS_OK);

    int32_t runRes = a2_engine_run_once(session);
    (void)runRes;

    int32_t destroyRes = a2_engine_destroy(session);
    assert(destroyRes == A2_STATUS_OK);

    std::cout << "[S03 Native] All C ABI checks passed with 0 ASan/UBSan violations." << std::endl;
    return 0;
}
