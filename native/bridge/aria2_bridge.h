#ifndef ARIA2_BRIDGE_H
#define ARIA2_BRIDGE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define A2_ABI_VERSION 1

// Return status codes
#define A2_STATUS_OK 0
#define A2_STATUS_ERROR -1
#define A2_STATUS_INVALID_ARGUMENT -2
#define A2_STATUS_INVALID_STATE -3
#define A2_STATUS_WRONG_THREAD -4
#define A2_STATUS_BUFFER_TOO_SMALL -5
#define A2_STATUS_NOT_FOUND -6
#define A2_STATUS_FATAL -7
#define A2_STATUS_QUEUE_FULL -8

// Event types matching DownloadEvent in aria2.h
#define A2_EVENT_DOWNLOAD_START 1
#define A2_EVENT_DOWNLOAD_PAUSE 2
#define A2_EVENT_DOWNLOAD_STOP 3
#define A2_EVENT_DOWNLOAD_COMPLETE 4
#define A2_EVENT_DOWNLOAD_ERROR 5
#define A2_EVENT_BT_DOWNLOAD_COMPLETE 6

#pragma pack(push, 8)
typedef struct A2EngineEvent {
    uint32_t struct_size;    // sizeof(A2EngineEvent)
    uint32_t event_type;     // A2_EVENT_*
    uint64_t gid;            // 64-bit GID
    int64_t timestamp_ms;    // UTC milliseconds
    uint64_t user_data;
} A2EngineEvent;

typedef struct A2GlobalStat {
    uint32_t struct_size;
    uint32_t download_speed;
    uint32_t upload_speed;
    uint32_t num_active;
    uint32_t num_waiting;
    uint32_t num_stopped;
    uint32_t num_stopped_total;
} A2GlobalStat;

typedef struct A2InitOptions {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t keep_running;
    int32_t use_signal_handler;
    const char* download_dir;    // UTF-8 or NULL
    const char* session_file;    // UTF-8 or NULL
    const char* const* option_keys;
    const char* const* option_values;
    uint32_t option_count;
} A2InitOptions;

typedef struct A2TaskHandleInfo {
    uint32_t struct_size;
    uint32_t status;          // 0: active, 1: waiting, 2: paused, 3: complete, 4: error, 5: removed
    int64_t total_length;
    int64_t completed_length;
    int64_t upload_length;
    uint32_t download_speed;
    uint32_t upload_speed;
    int32_t error_code;
    uint32_t num_files;
} A2TaskHandleInfo;

typedef struct A2FileInfo {
    uint32_t struct_size;
    uint32_t index;
    int64_t length;
    int64_t completed_length;
    uint32_t selected;
    uint32_t reserved;
} A2FileInfo;
#pragma pack(pop)

typedef void* A2SessionHandle;

// ABI metadata
uint32_t a2_bridge_get_abi_version(void);

// Lifecycle
int32_t a2_engine_init(const A2InitOptions* options, A2SessionHandle* out_session);
int32_t a2_engine_shutdown(A2SessionHandle session, int32_t force);
int32_t a2_engine_destroy(A2SessionHandle session);

// Event loop step
int32_t a2_engine_run_once(A2SessionHandle session);

// Commands
int32_t a2_download_add_uri(A2SessionHandle session,
                            const char* uri,
                            const char* const* headers,
                            uint32_t header_count,
                            const char* dir,
                            const char* out_filename,
                            uint64_t* out_gid);

int32_t a2_download_add_uris(A2SessionHandle session,
                             const char* const* uris,
                             uint32_t uri_count,
                             const char* const* option_keys,
                             const char* const* option_values,
                             uint32_t option_count,
                             uint64_t* out_gid);

int32_t a2_download_add_torrent(A2SessionHandle session,
                                const char* torrent_file_path,
                                const char* const* option_keys,
                                const char* const* option_values,
                                uint32_t option_count,
                                uint64_t* out_gid);

int32_t a2_download_pause(A2SessionHandle session, uint64_t gid, int32_t force);
int32_t a2_download_unpause(A2SessionHandle session, uint64_t gid);
int32_t a2_download_remove(A2SessionHandle session, uint64_t gid, int32_t force);
int32_t a2_download_purge_results(A2SessionHandle session, uint32_t* out_purged_count);

// Options
int32_t a2_download_change_option(A2SessionHandle session,
                                  uint64_t gid,
                                  const char* const* option_keys,
                                  const char* const* option_values,
                                  uint32_t option_count);

int32_t a2_engine_change_global_option(A2SessionHandle session,
                                       const char* const* option_keys,
                                       const char* const* option_values,
                                       uint32_t option_count);

int32_t a2_engine_get_global_option_value(A2SessionHandle session,
                                          const char* name,
                                          char* out_val,
                                          uint32_t val_buf_len,
                                          uint32_t* out_needed_len);

int32_t a2_download_get_option_value(A2SessionHandle session,
                                     uint64_t gid,
                                     const char* name,
                                     char* out_val,
                                     uint32_t val_buf_len,
                                     uint32_t* out_needed_len);

// Task inspection
int32_t a2_download_get_handle_info(A2SessionHandle session, uint64_t gid, A2TaskHandleInfo* out_info);

// index=0 returns the download directory; other indices are aria2's 1-based files.
// needed_len is UTF-8 bytes without a NUL. BUFFER_TOO_SMALL has no side effects.
int32_t a2_download_get_file_info(A2SessionHandle session, uint64_t gid, uint32_t index,
    A2FileInfo* out_info, uint8_t* path, uint32_t capacity, uint32_t* needed_len);

// Stats & Events
int32_t a2_engine_get_global_stat(A2SessionHandle session, A2GlobalStat* out_stat);
int32_t a2_engine_poll_events(A2SessionHandle session, A2EngineEvent* out_events, uint32_t max_events, uint32_t* out_count);

// GID utilities
void a2_gid_to_hex(uint64_t gid, char* out_hex_16);
uint64_t a2_hex_to_gid(const char* hex_16);

#ifdef __cplusplus
}
#endif

#endif // ARIA2_BRIDGE_H
