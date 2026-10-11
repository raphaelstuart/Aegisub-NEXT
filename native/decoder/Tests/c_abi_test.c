#include "aeginext_decode.h"
#include <stddef.h>

_Static_assert(sizeof(an_decode_backend_info) == 64, "backend ABI size");
_Static_assert(sizeof(an_frame_info) == 600, "frame ABI size");
_Static_assert(sizeof(an_frame_display_timing) == 32, "display timing ABI size");
_Static_assert(offsetof(an_frame_display_timing, timestamp) == 16, "display timestamp ABI offset");
_Static_assert(offsetof(an_frame_info, pts) == 128, "PTS ABI offset");
_Static_assert(offsetof(an_frame_info, pixel_format_name) == 152, "format name ABI offset");
_Static_assert(sizeof(an_frame_plane_info) == 32, "plane ABI size");
_Static_assert(offsetof(an_frame_plane_info, tight_byte_count) == 24, "byte count ABI offset");
_Static_assert(sizeof(an_frame_hdr_info) == 192, "HDR ABI size");
_Static_assert(sizeof(an_decode_ratio) == 16, "ratio ABI size");
_Static_assert(sizeof(an_preview_backend_info) == 16, "preview backend ABI size");
_Static_assert(sizeof(an_preview_request) == 48, "preview request ABI size");
_Static_assert(offsetof(an_preview_request, color_range) == 16, "preview color ABI offset");
_Static_assert(offsetof(an_preview_request, flags) == 40, "preview flags ABI offset");

_Static_assert(sizeof(an_decoder_options) == 16, "options ABI size");
_Static_assert(sizeof(an_decoder_session_info) == 320, "session ABI size");
_Static_assert(offsetof(an_decoder_session_info, decode_nanoseconds) == 48, "decode timing offset");
_Static_assert(offsetof(an_decoder_session_info, fallback_reason) == 64, "fallback offset");
_Static_assert(sizeof(an_resolved_color) == 40, "resolved color ABI size");

int an_decode_c_abi_test(void)
{
    return an_decode_abi_version() == AN_DECODE_ABI_VERSION &&
        (an_decode_features() & (AN_DECODE_FEATURE_SEEK | AN_DECODE_FEATURE_SEEK_SUPERSESSION)) ==
            (AN_DECODE_FEATURE_SEEK | AN_DECODE_FEATURE_SEEK_SUPERSESSION);
}
