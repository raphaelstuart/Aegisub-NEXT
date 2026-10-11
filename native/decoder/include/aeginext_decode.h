#ifndef AEGINEXT_DECODE_H
#define AEGINEXT_DECODE_H

#include <stdint.h>

#if defined(_WIN32)
#define AN_DECODE_CALL __cdecl
#if defined(AEGINEXT_DECODE_BUILD)
#define AN_DECODE_API __declspec(dllexport)
#else
#define AN_DECODE_API __declspec(dllimport)
#endif
#else
#define AN_DECODE_CALL
#define AN_DECODE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C"
{
#endif

enum an_decode_result
{
    AN_DECODE_OK = 0,
    AN_DECODE_EOF = 1,
    AN_DECODE_INVALID_ARGUMENT = 2,
    AN_DECODE_UNSUPPORTED = 3,
    AN_DECODE_IO_ERROR = 4,
    AN_DECODE_DECODE_ERROR = 5,
    AN_DECODE_CANCELLED = 6,
    AN_DECODE_INVALID_STATE = 7,
    AN_DECODE_NATIVE_FAILURE = 8,
    AN_DECODE_DISPLAY_TIMING_UNAVAILABLE = 9,
    AN_DECODE_SEEK_SUPERSEDED = 10
};

enum { AN_DECODE_ABI_VERSION = 1, AN_DECODE_NAME_CAPACITY = 64 };

enum an_decode_feature_flags
{
    AN_DECODE_FEATURE_SEEK = 1,
    AN_DECODE_FEATURE_SDR_PREVIEW = 2,
    AN_DECODE_FEATURE_SEEK_SELECTION = 8,
    AN_DECODE_FEATURE_DISPLAY_TIMING = 16,
    AN_DECODE_FEATURE_SEEK_SUPERSESSION = 32
};

enum an_frame_flags
{
    AN_FRAME_HAS_PTS = 1,
    AN_FRAME_HAS_BEST_EFFORT_TIMESTAMP = 2,
    AN_FRAME_HAS_DURATION = 4,
    AN_FRAME_KEY = 8,
    AN_FRAME_CORRUPT = 16,
    AN_FRAME_INTERLACED = 32,
    AN_FRAME_TOP_FIELD_FIRST = 64
};

enum an_frame_hdr_flags
{
    AN_HDR_MASTERING_PRESENT = 1,
    AN_HDR_MASTERING_HAS_PRIMARIES = 2,
    AN_HDR_MASTERING_HAS_LUMINANCE = 4,
    AN_HDR_CONTENT_LIGHT_PRESENT = 8
};

typedef struct an_decode_backend_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t compile_avformat;
    uint32_t runtime_avformat;
    uint32_t compile_avcodec;
    uint32_t runtime_avcodec;
    uint32_t compile_avutil;
    uint32_t runtime_avutil;
    char release_version[32];
} an_decode_backend_info;

typedef struct an_frame_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t width;
    uint32_t height;
    uint32_t plane_count;
    uint32_t component_count;
    uint32_t flags;
    uint32_t decode_error_flags;
    int32_t pixel_format;
    int32_t time_base_num;
    int32_t time_base_den;
    int32_t raw_frame_time_base_num;
    int32_t raw_frame_time_base_den;
    int32_t stream_time_base_num;
    int32_t stream_time_base_den;
    int32_t sample_aspect_ratio_num;
    int32_t sample_aspect_ratio_den;
    uint32_t crop_left;
    uint32_t crop_top;
    uint32_t crop_right;
    uint32_t crop_bottom;
    uint32_t component_depth[4];
    int32_t color_range;
    int32_t color_matrix;
    int32_t color_primaries;
    int32_t color_transfer;
    int32_t chroma_location;
    int32_t alpha_mode;
    uint32_t side_data_count;
    int64_t pts;
    int64_t best_effort_timestamp;
    int64_t duration;
    char pixel_format_name[AN_DECODE_NAME_CAPACITY];
    char color_range_name[AN_DECODE_NAME_CAPACITY];
    char color_matrix_name[AN_DECODE_NAME_CAPACITY];
    char color_primaries_name[AN_DECODE_NAME_CAPACITY];
    char color_transfer_name[AN_DECODE_NAME_CAPACITY];
    char chroma_location_name[AN_DECODE_NAME_CAPACITY];
    char alpha_mode_name[AN_DECODE_NAME_CAPACITY];
} an_frame_info;

enum an_display_timing_evidence
{
    AN_DISPLAY_ORIGINAL_PTS = 1,
    AN_DISPLAY_BEST_EFFORT = 2,
    AN_DISPLAY_PREVIOUS_DURATION = 4,
    AN_DISPLAY_DECLARED_FRAME_RATE = 8,
    AN_DISPLAY_STREAM_START = 16
};

typedef struct an_frame_display_timing
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t evidence;
    uint32_t reserved;
    int64_t timestamp;
    int32_t time_base_num;
    int32_t time_base_den;
} an_frame_display_timing;

typedef struct an_frame_plane_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t plane_index;
    uint32_t rows;
    int32_t native_stride;
    uint32_t row_bytes;
    uint64_t tight_byte_count;
} an_frame_plane_info;

typedef struct an_decode_ratio
{
    int64_t numerator;
    int64_t denominator;
} an_decode_ratio;

typedef struct an_frame_hdr_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t flags;
    uint32_t reserved;
    an_decode_ratio red_x;
    an_decode_ratio red_y;
    an_decode_ratio green_x;
    an_decode_ratio green_y;
    an_decode_ratio blue_x;
    an_decode_ratio blue_y;
    an_decode_ratio white_point_x;
    an_decode_ratio white_point_y;
    an_decode_ratio min_luminance;
    an_decode_ratio max_luminance;
    uint32_t max_content_light_level;
    uint32_t max_frame_average_light_level;
    uint64_t reserved2;
} an_frame_hdr_info;

typedef struct an_preview_backend_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t compile_swscale;
    uint32_t runtime_swscale;
} an_preview_backend_info;

typedef struct an_preview_request
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t width;
    uint32_t height;
    int32_t color_range;
    int32_t color_matrix;
    int32_t color_primaries;
    int32_t color_transfer;
    int32_t chroma_location;
    int32_t alpha_mode;
    uint32_t flags;
    uint32_t reserved;
} an_preview_request;

/* Shared core extensions leave all original frame structure layouts unchanged. */
enum { AN_DECODE_FEATURE_MEDIA_CORE = 4 };
typedef struct an_decoder_options
{
    uint32_t struct_size, abi_version, mode, workload;
} an_decoder_options;
typedef struct an_decoder_session_info
{
    uint32_t struct_size, abi_version, core_version, capabilities;
    uint32_t requested_mode, active_backend, hardware_confirmed, reserved;
    uint64_t generation, delivered_frames, decode_nanoseconds, download_nanoseconds;
    char fallback_reason[256];
} an_decoder_session_info;
typedef struct an_resolved_color
{
    uint32_t struct_size, abi_version, core_version, inferred_fields;
    int32_t color_range, color_matrix, color_primaries, color_transfer, chroma_location, alpha_mode;
} an_resolved_color;
AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_core_version(void);
AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_core_capabilities(void);
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_create_with_options(const an_decoder_options *options, void **decoder, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_get_session_info(void *decoder, an_decoder_session_info *info, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_resolve_color(void *frame, an_resolved_color *color, char *error, uint32_t capacity);

AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_abi_version(void);
/* SEEK guarantees both an_decoder_seek and an_decoder_get_time_base exports.
 * Capability additions do not change the layout of ABI 1 structures. */
AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_features(void);
AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_live_decoders(void);
AN_DECODE_API uint32_t AN_DECODE_CALL an_decode_live_frames(void);
AN_DECODE_API int32_t AN_DECODE_CALL an_decode_get_backend_info(an_decode_backend_info *info, char *error, uint32_t capacity);

/* Create is nonblocking. Open is one-shot and accepts an absolute local UTF-8
 * path and an explicit video stream index. Open/read/seek/query/release are serial
 * per handle and may run off the UI thread. Cancel and set_seek_epoch may overlap open/read/seek.
 * Cancellation is sticky and cooperative; destroy must wait for operations.
 * Errors are UTF-8 and NUL-terminated when capacity > 0. No C++ exception escapes. */
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_create(void **decoder, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_open(void *decoder, const char *path_utf8, int32_t stream_index, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_read_next(void *decoder, void **frame, char *error, uint32_t capacity);
/* Optional SEEK_SELECTION capability. From the current cursor, select the last
 * display time <= timestamp (or the first frame if already after the target).
 * Intermediate hardware frames stay on the GPU. The first frame after the
 * target is retained for read_next; duplicates select the last frame. Missing
 * or decreasing display time during selection is a terminal error. No implicit seek.
 * Display time prefers original PTS, then FFmpeg best-effort; derived timing
 * is separate from the original frame fields and is described by its evidence.
 * DISPLAY_TIMING_UNAVAILABLE reports insufficient anchors after preroll; this
 * session is terminal. A caller may retry once with a new session from the start.
 * Decreasing timing and corrupted media remain distinct decode errors. */
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_read_for_seek(void *decoder, int64_t timestamp, void **frame, char *error, uint32_t capacity);
/* Optional SEEK_SUPERSESSION capability. Epochs start at zero and only increase;
 * publishing a lower/equal epoch is a no-op and does not wait for decoding.
 * read_for_seek_epoch checks its expected epoch at complete frame boundaries and
 * before hardware download. A mismatch returns SEEK_SUPERSEDED without a frame;
 * this is nonterminal, but the next read must follow a successful decoder_seek.
 * A single demux/decode operation is not interrupted. Cancel remains terminal. */
AN_DECODE_API void AN_DECODE_CALL an_decoder_set_seek_epoch(void *decoder, uint64_t epoch);
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_read_for_seek_epoch(void *decoder, int64_t timestamp, uint64_t epoch,
    void **frame, char *error, uint32_t capacity);
/* Returns the selected stream's positive time base after a successful open.
 * This query neither reads packets nor changes the current decoder position. */
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_get_time_base(void *decoder, an_decode_ratio *time_base, char *error, uint32_t capacity);
/* Timestamp is absolute in the selected stream's time base, without subtracting
 * the stream/container start. Backward keyframe seeking requires caller preroll.
 * INT64_MIN is reserved for missing PTS and is rejected without changing state.
 * Success flushes pending decoder data and permits reading again after EOF.
 * A demuxer seek failure is terminal; cancellation remains sticky. Frames already
 * returned remain valid across seeking. This does not guarantee an exact frame. */
AN_DECODE_API int32_t AN_DECODE_CALL an_decoder_seek(void *decoder, int64_t timestamp, char *error, uint32_t capacity);
AN_DECODE_API void AN_DECODE_CALL an_decoder_cancel(void *decoder);
AN_DECODE_API void AN_DECODE_CALL an_decoder_destroy(void *decoder);

/* Each returned frame owns an independent AVFrame reference and survives the
 * next read and decoder destruction. All struct queries require exact size and
 * ABI on entry. PTS/duration use time_base; best_effort uses stream_time_base.
 * Missing timestamps are described by flags; their numeric fields are zero.
 * Actual decoded/downloaded pixels, signed strides, cropping and raw color enums
 * are kept. Hardware output may use NV12/P010 and already omit coded padding:
 * CPU/GPU coded dimensions and crop values need not match, but their visible
 * regions match. Download copies hardware frame properties and never fabricates
 * padding or reinstates cropping that the hardware decoder already removed.
 * Cropping must leave nonempty width and height. A single-row plane (including
 * a palette) may have native_stride == 0; multirow strides cover an active row.
 * Unknown color names are empty; unknown raw enum values are not rewritten. */
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_get_info(void *frame, an_frame_info *info, char *error, uint32_t capacity);
/* Independent extension; an_frame_info remains 600 bytes. Evidence is zero
 * when display time is unavailable. Derived evidence retains its anchor and
 * records previous frame duration or matching declared average/nominal rate.
 * Stream start is an initial anchor only; it is never reused after seeking.
 * Original PTS, best-effort timestamp, duration and time bases are unchanged. */
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_get_display_timing(void *frame, an_frame_display_timing *info, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_get_plane_info(void *frame, uint32_t plane, an_frame_plane_info *info, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_get_hdr_info(void *frame, an_frame_hdr_info *info, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_get_side_data_name(void *frame, uint32_t index, char *name, uint32_t name_capacity, char *error, uint32_t capacity);

/* Copies only active row bytes in top-down order, tightly packed. Padding is
 * never copied. Destination size must cover tight_byte_count. No color or
 * pixel-format conversion, crop application, or tone mapping occurs. */
AN_DECODE_API int32_t AN_DECODE_CALL an_frame_copy_plane(void *frame, uint32_t plane, uint8_t *destination, uint64_t destination_capacity, char *error, uint32_t capacity);
AN_DECODE_API void AN_DECODE_CALL an_frame_destroy(void *frame);

/* SDR-only preview, independent of decoder state. All operations and destruction
 * are serial per converter and per borrowed frame. The source remains unchanged.
 * Output is tight top-down opaque BGRA8, sRGB / BT.709 / full range. Exact source
 * crop is applied in BGRA before bilinear display resampling to width/height.
 * The caller resolves SAR into the requested square-pixel output dimensions.
 * Request color values must exactly match shared core effective color; overrides are unsupported.
 * flags/reserved must be zero. The fixed CPU CMS uses perceptual mapping and the
 * FFmpeg SDR reference of 203 nits, not measured physical display luminance.
 * PQ static mastering metadata participates in mapping; HLG uses its fixed
 * 1000-nit reference display. CLL is not an exposure.
 * Returned SDR pixels must never be used as a source for HDR export. */
AN_DECODE_API uint32_t AN_DECODE_CALL an_preview_live_converters(void);
AN_DECODE_API int32_t AN_DECODE_CALL an_preview_get_backend_info(an_preview_backend_info *info, char *error, uint32_t capacity);
AN_DECODE_API int32_t AN_DECODE_CALL an_preview_converter_create(void **converter, char *error, uint32_t capacity);
AN_DECODE_API void AN_DECODE_CALL an_preview_converter_destroy(void *converter);
AN_DECODE_API int32_t AN_DECODE_CALL an_preview_convert(void *converter, void *frame, const an_preview_request *request,
    uint8_t *destination, uint64_t destination_capacity, char *error, uint32_t capacity);

#ifdef __cplusplus
}
#endif
#endif
