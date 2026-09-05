#include "aria2_bridge.h"
#include <aria2/aria2.h>

#include <vector>
#include <string>
#include <cstring>
#include <mutex>
#include <chrono>
#include <unistd.h>
#include <sys/syscall.h>

static pid_t get_current_tid() {
    return (pid_t)syscall(SYS_gettid);
}

struct BridgeSession {
    aria2::Session* aria2_session = nullptr;
    pid_t owner_tid = 0;
    bool is_initialized = false;
    bool is_stopping = false;

    static const size_t EVENT_CAPACITY = 128;
    A2EngineEvent events[EVENT_CAPACITY];
    size_t event_head = 0;
    size_t event_tail = 0;
    size_t event_count = 0;
    std::mutex event_mutex;
    bool event_overflow = false;

    void push_event(uint32_t type, uint64_t gid) {
        std::lock_guard<std::mutex> lock(event_mutex);
        if (event_count >= EVENT_CAPACITY) {
            event_overflow = true;
            return;
        }
        auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch()).count();

        A2EngineEvent& ev = events[event_tail];
        ev.struct_size = sizeof(A2EngineEvent);
        ev.event_type = type;
        ev.gid = gid;
        ev.timestamp_ms = (int64_t)now;
        ev.user_data = 0;

        event_tail = (event_tail + 1) % EVENT_CAPACITY;
        event_count++;
    }

    size_t pop_events(A2EngineEvent* out_events, size_t max_events) {
        std::lock_guard<std::mutex> lock(event_mutex);
        size_t to_copy = (event_count < max_events) ? event_count : max_events;
        for (size_t i = 0; i < to_copy; ++i) {
            out_events[i] = events[event_head];
            event_head = (event_head + 1) % EVENT_CAPACITY;
        }
        event_count -= to_copy;
        return to_copy;
    }
};

static int on_download_event(aria2::Session* session, aria2::DownloadEvent event, aria2::A2Gid gid, void* userData) {
    (void)session;
    auto* s = reinterpret_cast<BridgeSession*>(userData);
    if (!s) return 0;
    uint32_t ev_type = 0;
    switch (event) {
        case aria2::EVENT_ON_DOWNLOAD_START: ev_type = A2_EVENT_DOWNLOAD_START; break;
        case aria2::EVENT_ON_DOWNLOAD_PAUSE: ev_type = A2_EVENT_DOWNLOAD_PAUSE; break;
        case aria2::EVENT_ON_DOWNLOAD_STOP: ev_type = A2_EVENT_DOWNLOAD_STOP; break;
        case aria2::EVENT_ON_DOWNLOAD_COMPLETE: ev_type = A2_EVENT_DOWNLOAD_COMPLETE; break;
        case aria2::EVENT_ON_DOWNLOAD_ERROR: ev_type = A2_EVENT_DOWNLOAD_ERROR; break;
        case aria2::EVENT_ON_BT_DOWNLOAD_COMPLETE: ev_type = A2_EVENT_BT_DOWNLOAD_COMPLETE; break;
    }
    if (ev_type != 0) {
        s->push_event(ev_type, (uint64_t)gid);
    }
    return 0;
}

extern "C" {

uint32_t a2_bridge_get_abi_version(void) {
    return A2_ABI_VERSION;
}

int32_t a2_engine_init(const A2InitOptions* options, A2SessionHandle* out_session) {
    if (!options || !out_session) return A2_STATUS_INVALID_ARGUMENT;
    if (options->abi_version != A2_ABI_VERSION) return A2_STATUS_INVALID_ARGUMENT;

    int r = aria2::libraryInit();
    if (r != 0) return A2_STATUS_FATAL;

    auto* s = new BridgeSession();
    s->owner_tid = get_current_tid();

    aria2::SessionConfig config;
    config.keepRunning = (options->keep_running != 0);
    config.useSignalHandler = (options->use_signal_handler != 0);
    config.downloadEventCallback = on_download_event;
    config.userData = s;

    aria2::KeyVals keyVals;
    if (options->download_dir && strlen(options->download_dir) > 0) {
        keyVals.emplace_back("dir", options->download_dir);
    }
    if (options->session_file && strlen(options->session_file) > 0) {
        keyVals.emplace_back("input-file", options->session_file);
        keyVals.emplace_back("save-session", options->session_file);
    }

    s->aria2_session = aria2::sessionNew(keyVals, config);
    if (!s->aria2_session) {
        delete s;
        aria2::libraryDeinit();
        return A2_STATUS_ERROR;
    }

    s->is_initialized = true;
    *out_session = s;
    return A2_STATUS_OK;
}

int32_t a2_engine_shutdown(A2SessionHandle session, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    s->is_stopping = true;
    int r = aria2::shutdown(s->aria2_session, force != 0);
    return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
}

int32_t a2_engine_destroy(A2SessionHandle session) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    if (s->aria2_session) {
        aria2::sessionFinal(s->aria2_session);
        s->aria2_session = nullptr;
    }
    delete s;
    aria2::libraryDeinit();
    return A2_STATUS_OK;
}

int32_t a2_engine_run_once(A2SessionHandle session) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    try {
        int r = aria2::run(s->aria2_session, aria2::RUN_ONCE);
        return r;
    } catch (...) {
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_add_uri(A2SessionHandle session,
                            const char* uri,
                            const char* const* headers,
                            uint32_t header_count,
                            const char* dir,
                            const char* out_filename,
                            uint64_t* out_gid) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !uri) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    std::vector<std::string> uris = { uri };
    aria2::KeyVals options;

    if (dir && strlen(dir) > 0) {
        options.emplace_back("dir", dir);
    }
    if (out_filename && strlen(out_filename) > 0) {
        options.emplace_back("out", out_filename);
    }
    if (headers && header_count > 0) {
        for (uint32_t i = 0; i < header_count; ++i) {
            if (headers[i]) {
                options.emplace_back("header", headers[i]);
            }
        }
    }

    aria2::A2Gid gid = 0;
    int r = aria2::addUri(s->aria2_session, &gid, uris, options);
    if (r == 0) {
        if (out_gid) *out_gid = (uint64_t)gid;
        return A2_STATUS_OK;
    }
    return A2_STATUS_ERROR;
}

int32_t a2_download_pause(A2SessionHandle session, uint64_t gid, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    int r = aria2::pauseDownload(s->aria2_session, (aria2::A2Gid)gid, force != 0);
    return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
}

int32_t a2_download_unpause(A2SessionHandle session, uint64_t gid) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    int r = aria2::unpauseDownload(s->aria2_session, (aria2::A2Gid)gid);
    return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
}

int32_t a2_download_remove(A2SessionHandle session, uint64_t gid, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    int r = aria2::removeDownload(s->aria2_session, (aria2::A2Gid)gid, force != 0);
    return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
}

int32_t a2_engine_get_global_stat(A2SessionHandle session, A2GlobalStat* out_stat) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !out_stat) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    aria2::GlobalStat stat = aria2::getGlobalStat(s->aria2_session);
    out_stat->struct_size = sizeof(A2GlobalStat);
    out_stat->download_speed = (uint32_t)stat.downloadSpeed;
    out_stat->upload_speed = (uint32_t)stat.uploadSpeed;
    out_stat->num_active = (uint32_t)stat.numActive;
    out_stat->num_waiting = (uint32_t)stat.numWaiting;
    out_stat->num_stopped = (uint32_t)stat.numStopped;
    out_stat->num_stopped_total = (uint32_t)stat.numStopped;
    return A2_STATUS_OK;
}

int32_t a2_engine_poll_events(A2SessionHandle session, A2EngineEvent* out_events, uint32_t max_events, uint32_t* out_count) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !out_events || !out_count) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    if (s->event_overflow) {
        s->event_overflow = false;
        return A2_STATUS_FATAL;
    }

    *out_count = (uint32_t)s->pop_events(out_events, max_events);
    return A2_STATUS_OK;
}

void a2_gid_to_hex(uint64_t gid, char* out_hex_16) {
    if (!out_hex_16) return;
    std::string hex = aria2::gidToHex((aria2::A2Gid)gid);
    std::strncpy(out_hex_16, hex.c_str(), 16);
    out_hex_16[16] = '\0';
}

uint64_t a2_hex_to_gid(const char* hex_16) {
    if (!hex_16) return 0;
    return (uint64_t)aria2::hexToGid(std::string(hex_16));
}

}
