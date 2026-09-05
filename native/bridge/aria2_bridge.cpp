#include "aria2_bridge.h"
#include <aria2/aria2.h>

#include <vector>
#include <string>
#include <cstring>
#include <mutex>
#include <chrono>
#include <atomic>
#include <unistd.h>
#include <sys/syscall.h>

static pid_t get_current_tid() {
    return (pid_t)syscall(SYS_gettid);
}

static std::atomic<int> g_active_session_count{0};
static std::mutex g_init_mutex;
static bool g_library_initialized = false;

struct BridgeSession {
    aria2::Session* aria2_session = nullptr;
    pid_t owner_tid = 0;
    bool is_initialized = false;
    bool is_stopping = false;
    bool is_faulted = false;

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
    if (options->struct_size != sizeof(A2InitOptions) && options->struct_size != 32) {
        return A2_STATUS_INVALID_ARGUMENT;
    }

    // D04: Only one session per process at a time
    int prev_count = g_active_session_count.fetch_add(1);
    if (prev_count > 0) {
        g_active_session_count.fetch_sub(1);
        return A2_STATUS_INVALID_STATE; // Already have active session in process
    }

    try {
        std::lock_guard<std::mutex> lock(g_init_mutex);
        if (!g_library_initialized) {
            int r = aria2::libraryInit();
            if (r != 0) {
                g_active_session_count.fetch_sub(1);
                return A2_STATUS_FATAL;
            }
            g_library_initialized = true;
        }

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
            if (access(options->session_file, F_OK) == 0) {
                keyVals.emplace_back("input-file", options->session_file);
            }
            keyVals.emplace_back("save-session", options->session_file);
        }
        if (options->struct_size == sizeof(A2InitOptions) && options->option_keys && options->option_values && options->option_count > 0) {
            for (uint32_t i = 0; i < options->option_count; ++i) {
                if (options->option_keys[i] && options->option_values[i]) {
                    keyVals.emplace_back(options->option_keys[i], options->option_values[i]);
                }
            }
        }

        s->aria2_session = aria2::sessionNew(keyVals, config);
        if (!s->aria2_session) {
            delete s;
            g_active_session_count.fetch_sub(1);
            return A2_STATUS_ERROR;
        }

        s->is_initialized = true;
        *out_session = s;
        return A2_STATUS_OK;
    } catch (...) {
        g_active_session_count.fetch_sub(1);
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_shutdown(A2SessionHandle session, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    try {
        s->is_stopping = true;
        int r = aria2::shutdown(s->aria2_session, force != 0);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_destroy(A2SessionHandle session) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;

    try {
        if (s->aria2_session) {
            aria2::sessionFinal(s->aria2_session);
            s->aria2_session = nullptr;
        }
        delete s;
        g_active_session_count.fetch_sub(1);
        return A2_STATUS_OK;
    } catch (...) {
        delete s;
        g_active_session_count.fetch_sub(1);
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_run_once(A2SessionHandle session) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        int r = aria2::run(s->aria2_session, aria2::RUN_ONCE);
        return r;
    } catch (...) {
        s->is_faulted = true;
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
    if (!s || !s->aria2_session || !uri || !out_gid) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
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
            *out_gid = (uint64_t)gid;
            return A2_STATUS_OK;
        }
        return A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_add_uris(A2SessionHandle session,
                             const char* const* uris,
                             uint32_t uri_count,
                             const char* const* option_keys,
                             const char* const* option_values,
                             uint32_t option_count,
                             uint64_t* out_gid) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !uris || uri_count == 0 || !out_gid) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        std::vector<std::string> uris_vec;
        uris_vec.reserve(uri_count);
        for (uint32_t i = 0; i < uri_count; ++i) {
            if (uris[i]) uris_vec.emplace_back(uris[i]);
        }
        if (uris_vec.empty()) return A2_STATUS_INVALID_ARGUMENT;

        aria2::KeyVals options;
        if (option_keys && option_values && option_count > 0) {
            for (uint32_t i = 0; i < option_count; ++i) {
                if (option_keys[i] && option_values[i]) {
                    options.emplace_back(option_keys[i], option_values[i]);
                }
            }
        }

        aria2::A2Gid gid = 0;
        int r = aria2::addUri(s->aria2_session, &gid, uris_vec, options);
        if (r == 0) {
            *out_gid = (uint64_t)gid;
            return A2_STATUS_OK;
        }
        return A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_add_torrent(A2SessionHandle session,
                                const char* torrent_file_path,
                                const char* const* option_keys,
                                const char* const* option_values,
                                uint32_t option_count,
                                uint64_t* out_gid) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !torrent_file_path || !out_gid) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::KeyVals options;
        if (option_keys && option_values && option_count > 0) {
            for (uint32_t i = 0; i < option_count; ++i) {
                if (option_keys[i] && option_values[i]) {
                    options.emplace_back(option_keys[i], option_values[i]);
                }
            }
        }

        aria2::A2Gid gid = 0;
        int r = aria2::addTorrent(s->aria2_session, &gid, std::string(torrent_file_path), options);
        if (r == 0) {
            *out_gid = (uint64_t)gid;
            return A2_STATUS_OK;
        }
        return A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_pause(A2SessionHandle session, uint64_t gid, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        int r = aria2::pauseDownload(s->aria2_session, (aria2::A2Gid)gid, force != 0);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_unpause(A2SessionHandle session, uint64_t gid) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        int r = aria2::unpauseDownload(s->aria2_session, (aria2::A2Gid)gid);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_remove(A2SessionHandle session, uint64_t gid, int32_t force) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        int r = aria2::removeDownload(s->aria2_session, (aria2::A2Gid)gid, force != 0);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_purge_results(A2SessionHandle session, uint32_t* out_purged_count) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session) return A2_STATUS_INVALID_STATE;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    if (out_purged_count) *out_purged_count = 0;
    return A2_STATUS_OK;
}

int32_t a2_download_change_option(A2SessionHandle session,
                                  uint64_t gid,
                                  const char* const* option_keys,
                                  const char* const* option_values,
                                  uint32_t option_count) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !option_keys || !option_values) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::KeyVals options;
        for (uint32_t i = 0; i < option_count; ++i) {
            if (option_keys[i] && option_values[i]) {
                options.emplace_back(option_keys[i], option_values[i]);
            }
        }
        int r = aria2::changeOption(s->aria2_session, (aria2::A2Gid)gid, options);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_change_global_option(A2SessionHandle session,
                                       const char* const* option_keys,
                                       const char* const* option_values,
                                       uint32_t option_count) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !option_keys || !option_values) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::KeyVals options;
        for (uint32_t i = 0; i < option_count; ++i) {
            if (option_keys[i] && option_values[i]) {
                options.emplace_back(option_keys[i], option_values[i]);
            }
        }
        int r = aria2::changeGlobalOption(s->aria2_session, options);
        return r == 0 ? A2_STATUS_OK : A2_STATUS_ERROR;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_get_global_option_value(A2SessionHandle session,
                                          const char* name,
                                          char* out_val,
                                          uint32_t val_buf_len,
                                          uint32_t* out_needed_len) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !name || !out_needed_len) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        const std::string& val = aria2::getGlobalOption(s->aria2_session, name);
        *out_needed_len = static_cast<uint32_t>(val.length() + 1);
        if (!out_val || val_buf_len < *out_needed_len) {
            return A2_STATUS_BUFFER_TOO_SMALL;
        }
        std::memcpy(out_val, val.c_str(), *out_needed_len);
        return A2_STATUS_OK;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_get_option_value(A2SessionHandle session,
                                     uint64_t gid,
                                     const char* name,
                                     char* out_val,
                                     uint32_t val_buf_len,
                                     uint32_t* out_needed_len) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !name || !out_needed_len) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::DownloadHandle* dh = aria2::getDownloadHandle(s->aria2_session, (aria2::A2Gid)gid);
        if (!dh) return A2_STATUS_NOT_FOUND;
        const std::string& val = dh->getOption(name);
        *out_needed_len = static_cast<uint32_t>(val.length() + 1);
        if (!out_val || val_buf_len < *out_needed_len) {
            aria2::deleteDownloadHandle(dh);
            return A2_STATUS_BUFFER_TOO_SMALL;
        }
        std::memcpy(out_val, val.c_str(), *out_needed_len);
        aria2::deleteDownloadHandle(dh);
        return A2_STATUS_OK;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_download_get_handle_info(A2SessionHandle session, uint64_t gid, A2TaskHandleInfo* out_info) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !out_info) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::DownloadHandle* dh = aria2::getDownloadHandle(s->aria2_session, (aria2::A2Gid)gid);
        if (!dh) return A2_STATUS_NOT_FOUND;
        out_info->struct_size = sizeof(A2TaskHandleInfo);
        out_info->status = static_cast<uint32_t>(dh->getStatus());
        out_info->total_length = dh->getTotalLength();
        out_info->completed_length = dh->getCompletedLength();
        out_info->upload_length = dh->getUploadLength();
        out_info->download_speed = static_cast<uint32_t>(dh->getDownloadSpeed());
        out_info->upload_speed = static_cast<uint32_t>(dh->getUploadSpeed());
        out_info->error_code = dh->getErrorCode();
        out_info->num_files = static_cast<uint32_t>(dh->getNumFiles());
        aria2::deleteDownloadHandle(dh);
        return A2_STATUS_OK;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_get_global_stat(A2SessionHandle session, A2GlobalStat* out_stat) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !s->aria2_session || !out_stat) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    try {
        aria2::GlobalStat stat = aria2::getGlobalStat(s->aria2_session);
        out_stat->struct_size = sizeof(A2GlobalStat);
        out_stat->download_speed = (uint32_t)stat.downloadSpeed;
        out_stat->upload_speed = (uint32_t)stat.uploadSpeed;
        out_stat->num_active = (uint32_t)stat.numActive;
        out_stat->num_waiting = (uint32_t)stat.numWaiting;
        out_stat->num_stopped = (uint32_t)stat.numStopped;
        out_stat->num_stopped_total = (uint32_t)stat.numStopped;
        return A2_STATUS_OK;
    } catch (...) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }
}

int32_t a2_engine_poll_events(A2SessionHandle session, A2EngineEvent* out_events, uint32_t max_events, uint32_t* out_count) {
    auto* s = reinterpret_cast<BridgeSession*>(session);
    if (!s || !out_events || !out_count) return A2_STATUS_INVALID_ARGUMENT;
    if (get_current_tid() != s->owner_tid) return A2_STATUS_WRONG_THREAD;
    if (s->is_faulted) return A2_STATUS_FATAL;

    if (s->event_overflow) {
        s->is_faulted = true;
        return A2_STATUS_FATAL;
    }

    *out_count = (uint32_t)s->pop_events(out_events, max_events);
    return A2_STATUS_OK;
}

void a2_gid_to_hex(uint64_t gid, char* out_hex_16) {
    if (!out_hex_16) return;
    try {
        std::string hex = aria2::gidToHex((aria2::A2Gid)gid);
        std::strncpy(out_hex_16, hex.c_str(), 16);
        out_hex_16[16] = '\0';
    } catch (...) {
        std::memset(out_hex_16, 0, 17);
    }
}

uint64_t a2_hex_to_gid(const char* hex_16) {
    if (!hex_16) return 0;
    try {
        return (uint64_t)aria2::hexToGid(std::string(hex_16));
    } catch (...) {
        return 0;
    }
}

}
