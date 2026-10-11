#include "decoder_context.h"
#include "preview_converter.h"
#include "color_resolution.h"
#include <cstddef>
#include <mutex>
#include <unordered_set>

using aeginext::decode::DecoderContext;
using aeginext::decode::Error;
using aeginext::decode::FrameOwner;
using aeginext::decode::PreviewConverter;

static_assert(sizeof(an_decoder_options) == 16);
static_assert(sizeof(an_decoder_session_info) == 320);
static_assert(offsetof(an_decoder_session_info, fallback_reason) == 64);
static_assert(sizeof(an_resolved_color) == 40);
static_assert(sizeof(an_decode_backend_info) == 64);
static_assert(sizeof(an_frame_info) == 600);
static_assert(sizeof(an_frame_display_timing) == 32);
static_assert(offsetof(an_frame_display_timing, timestamp) == 16);
static_assert(offsetof(an_frame_info, pts) == 128);
static_assert(offsetof(an_frame_info, pixel_format_name) == 152);
static_assert(sizeof(an_frame_plane_info) == 32);
static_assert(offsetof(an_frame_plane_info, tight_byte_count) == 24);
static_assert(sizeof(an_frame_hdr_info) == 192);
static_assert(sizeof(an_decode_ratio) == 16);
static_assert(offsetof(an_frame_hdr_info, red_x) == 16);
static_assert(offsetof(an_frame_hdr_info, max_content_light_level) == 176);
static_assert(sizeof(an_preview_backend_info) == 16);
static_assert(sizeof(an_preview_request) == 48);
static_assert(offsetof(an_preview_request, color_range) == 16);
static_assert(offsetof(an_preview_request, flags) == 40);

namespace
{
std::mutex registryMutex;
std::unordered_set<DecoderContext *> decoders;
std::unordered_set<FrameOwner *> frames;
std::unordered_set<PreviewConverter *> previewConverters;
std::atomic<uint32_t> decoderCount{0};
std::atomic<uint32_t> frameCount{0};
std::atomic<uint32_t> previewCount{0};

template<typename Action>
int32_t Boundary(char *error, uint32_t capacity, Action action) noexcept
{
    aeginext::decode::CopyText(error, capacity, "");
    try
    {
        return action();
    }
    catch (const aeginext::media::CoreError &exception)
    {
        aeginext::decode::CopyText(error, capacity, exception.what());
        return static_cast<int32_t>(exception.Code());
    }
    catch (const Error &exception)
    {
        aeginext::decode::CopyText(error, capacity, exception.what());
        return exception.Result();
    }
    catch (const std::exception &exception)
    {
        aeginext::decode::CopyText(error, capacity, exception.what());
        return AN_DECODE_NATIVE_FAILURE;
    }
    catch (...)
    {
        aeginext::decode::CopyText(error, capacity, "Unexpected native decoding failure.");
        return AN_DECODE_NATIVE_FAILURE;
    }
}

DecoderContext *Decoder(void *handle)
{
    const std::lock_guard lock(registryMutex);
    auto *decoder = static_cast<DecoderContext *>(handle);
    if (!decoder || !decoders.contains(decoder))
    {
        throw Error(AN_DECODE_INVALID_ARGUMENT, "Invalid decoder handle.");
    }

    return decoder;
}

FrameOwner *Frame(void *handle)
{
    const std::lock_guard lock(registryMutex);
    auto *frame = static_cast<FrameOwner *>(handle);
    if (!frame || !frames.contains(frame))
    {
        throw Error(AN_DECODE_INVALID_ARGUMENT, "Invalid frame handle.");
    }

    return frame;
}

PreviewConverter *Preview(void *handle)
{
    const std::lock_guard lock(registryMutex);
    auto *converter = static_cast<PreviewConverter *>(handle);
    if (!converter || !previewConverters.contains(converter))
    {
        throw Error(AN_DECODE_INVALID_ARGUMENT, "Invalid preview converter handle.");
    }
    return converter;
}

template<typename T>
void ValidateInfo(T *info)
{
    if (!info || info->struct_size != sizeof(T) || info->abi_version != AN_DECODE_ABI_VERSION)
    {
        throw Error(AN_DECODE_INVALID_ARGUMENT, "Output structure size or ABI version is incorrect.");
    }
}
}

uint32_t AN_DECODE_CALL an_decode_abi_version(void) { return AN_DECODE_ABI_VERSION; }
uint32_t AN_DECODE_CALL an_decode_features(void) { return AN_DECODE_FEATURE_SEEK | AN_DECODE_FEATURE_SDR_PREVIEW | AN_DECODE_FEATURE_MEDIA_CORE | AN_DECODE_FEATURE_SEEK_SELECTION | AN_DECODE_FEATURE_DISPLAY_TIMING | AN_DECODE_FEATURE_SEEK_SUPERSESSION; }
uint32_t AN_DECODE_CALL an_decode_live_decoders(void) { return decoderCount.load(); }
uint32_t AN_DECODE_CALL an_decode_live_frames(void) { return frameCount.load(); }
uint32_t AN_DECODE_CALL an_preview_live_converters(void) { return previewCount.load(); }

int32_t AN_DECODE_CALL an_preview_get_backend_info(an_preview_backend_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        *info = PreviewConverter::BackendInfo();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_preview_converter_create(void **converter, char *error, uint32_t capacity)
{
    if (converter)
    {
        *converter = nullptr;
    }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!converter)
        {
            throw Error(AN_DECODE_INVALID_ARGUMENT, "Preview converter output pointer is required.");
        }
        auto value = std::make_unique<PreviewConverter>();
        {
            const std::lock_guard lock(registryMutex);
            previewConverters.insert(value.get());
            ++previewCount;
        }
        *converter = value.release();
        return AN_DECODE_OK;
    });
}

void AN_DECODE_CALL an_preview_converter_destroy(void *handle)
{
    try
    {
        auto *converter = static_cast<PreviewConverter *>(handle);
        {
            const std::lock_guard lock(registryMutex);
            if (!converter || previewConverters.erase(converter) == 0)
            {
                return;
            }
            --previewCount;
        }
        delete converter;
    }
    catch (...) {}
}

int32_t AN_DECODE_CALL an_preview_convert(void *converter, void *frame, const an_preview_request *request,
    uint8_t *destination, uint64_t destinationCapacity, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(request);
        Preview(converter)->Convert(*Frame(frame), *request, destination, destinationCapacity);
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decode_get_backend_info(an_decode_backend_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        aeginext::decode::ValidateBackend();
        *info = aeginext::decode::BackendInfo();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_create(void **decoder, char *error, uint32_t capacity)
{
    if (decoder) { *decoder = nullptr; }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!decoder)
        {
            throw Error(AN_DECODE_INVALID_ARGUMENT, "Decoder output pointer is required.");
        }

        auto context = std::make_unique<DecoderContext>();
        {
            const std::lock_guard lock(registryMutex);
            decoders.insert(context.get());
            ++decoderCount;
        }

        *decoder = context.release();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_open(void *decoder, const char *path, int32_t streamIndex, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        Decoder(decoder)->Open(path, streamIndex);
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_read_next(void *decoder, void **frame, char *error, uint32_t capacity)
{
    if (frame) { *frame = nullptr; }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!frame)
        {
            throw Error(AN_DECODE_INVALID_ARGUMENT, "Frame output pointer is required.");
        }

        auto value = Decoder(decoder)->ReadNext();
        if (!value)
        {
            return AN_DECODE_EOF;
        }

        {
            const std::lock_guard lock(registryMutex);
            frames.insert(value.get());
            ++frameCount;
        }

        *frame = value.release();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_read_for_seek(void *decoder, int64_t timestamp, void **frame, char *error, uint32_t capacity)
{
    if (frame) { *frame = nullptr; }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!frame) { throw Error(AN_DECODE_INVALID_ARGUMENT, "Frame output pointer is required."); }
        auto value = Decoder(decoder)->ReadForSeek(timestamp);
        if (!value) { return AN_DECODE_EOF; }
        {
            const std::lock_guard lock(registryMutex);
            frames.insert(value.get());
            ++frameCount;
        }
        *frame = value.release();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_read_for_seek_epoch(void *decoder, int64_t timestamp, uint64_t epoch,
    void **frame, char *error, uint32_t capacity)
{
    if (frame) { *frame = nullptr; }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!frame) { throw Error(AN_DECODE_INVALID_ARGUMENT, "Frame output pointer is required."); }
        auto value = Decoder(decoder)->ReadForSeek(timestamp, epoch);
        if (!value) { return AN_DECODE_EOF; }
        {
            const std::lock_guard lock(registryMutex);
            frames.insert(value.get());
            ++frameCount;
        }
        *frame = value.release();
        return AN_DECODE_OK;
    });
}

void AN_DECODE_CALL an_decoder_set_seek_epoch(void *handle, uint64_t epoch)
{
    try
    {
        const std::lock_guard lock(registryMutex);
        auto *decoder = static_cast<DecoderContext *>(handle);
        if (decoder && decoders.contains(decoder)) { decoder->SetSeekEpoch(epoch); }
    }
    catch (...) {}
}

int32_t AN_DECODE_CALL an_decoder_get_time_base(void *decoder, an_decode_ratio *timeBase, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!timeBase)
        {
            throw Error(AN_DECODE_INVALID_ARGUMENT, "Time-base output pointer is required.");
        }

        *timeBase = Decoder(decoder)->StreamTimeBase();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_decoder_seek(void *decoder, int64_t timestamp, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        Decoder(decoder)->Seek(timestamp);
        return AN_DECODE_OK;
    });
}

void AN_DECODE_CALL an_decoder_cancel(void *handle)
{
    try
    {
        const std::lock_guard lock(registryMutex);
        auto *decoder = static_cast<DecoderContext *>(handle);
        if (decoder && decoders.contains(decoder)) { decoder->Cancel(); }
    }
    catch (...) {}
}

void AN_DECODE_CALL an_decoder_destroy(void *handle)
{
    try
    {
        auto *decoder = static_cast<DecoderContext *>(handle);
        {
            const std::lock_guard lock(registryMutex);
            if (!decoder || decoders.erase(decoder) == 0) { return; }
            --decoderCount;
        }

        delete decoder;
    }
    catch (...) {}
}

int32_t AN_DECODE_CALL an_frame_get_info(void *frame, an_frame_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        *info = Frame(frame)->Info();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_frame_get_plane_info(void *frame, uint32_t plane, an_frame_plane_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        *info = Frame(frame)->Plane(plane);
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_frame_get_display_timing(void *frame, an_frame_display_timing *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        *info = Frame(frame)->DisplayTiming();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_frame_get_hdr_info(void *frame, an_frame_hdr_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        *info = Frame(frame)->Hdr();
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_frame_get_side_data_name(void *frame, uint32_t index, char *name, uint32_t nameCapacity, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!name || nameCapacity == 0)
        {
            throw Error(AN_DECODE_INVALID_ARGUMENT, "A side-data name destination is required.");
        }

        aeginext::decode::CopyName(name, nameCapacity, Frame(frame)->SideDataName(index));
        return AN_DECODE_OK;
    });
}

int32_t AN_DECODE_CALL an_frame_copy_plane(void *frame, uint32_t plane, uint8_t *destination, uint64_t destinationCapacity, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        Frame(frame)->CopyPlane(plane, destination, destinationCapacity);
        return AN_DECODE_OK;
    });
}

void AN_DECODE_CALL an_frame_destroy(void *handle)
{
    try
    {
        auto *frame = static_cast<FrameOwner *>(handle);
        {
            const std::lock_guard lock(registryMutex);
            if (!frame || frames.erase(frame) == 0) { return; }
            --frameCount;
        }

        delete frame;
    }
    catch (...) {}
}

uint32_t AN_DECODE_CALL an_decode_core_version(void) { return aeginext::media::CORE_VERSION; }
uint32_t AN_DECODE_CALL an_decode_core_capabilities(void) { return aeginext::media::CAPABILITIES; }
int32_t AN_DECODE_CALL an_decoder_create_with_options(const an_decoder_options *options, void **decoder, char *error, uint32_t capacity)
{
    if (decoder) { *decoder = nullptr; }
    return Boundary(error, capacity, [&]() -> int32_t
    {
        if (!decoder) { throw Error(AN_DECODE_INVALID_ARGUMENT, "Decoder output is required."); }
        ValidateInfo(options);
        auto value = std::make_unique<DecoderContext>(aeginext::media::DecodeOptions{
            static_cast<aeginext::media::DecodeMode>(options->mode), static_cast<aeginext::media::DecodeWorkload>(options->workload)});
        const std::lock_guard lock(registryMutex);
        decoders.insert(value.get());
        *decoder = value.release();
        ++decoderCount;
        return AN_DECODE_OK;
    });
}
int32_t AN_DECODE_CALL an_decoder_get_session_info(void *decoder, an_decoder_session_info *info, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(info);
        const auto &value = Decoder(decoder)->Info();
        *info = {};
        info->struct_size = sizeof(*info); info->abi_version = AN_DECODE_ABI_VERSION;
        info->core_version = aeginext::media::CORE_VERSION; info->capabilities = aeginext::media::CAPABILITIES;
        info->requested_mode = static_cast<uint32_t>(value.requestedMode);
        info->active_backend = static_cast<uint32_t>(value.activeBackend);
        info->hardware_confirmed = value.hardwareConfirmed;
        info->generation = value.generation; info->delivered_frames = value.deliveredFrames;
        info->decode_nanoseconds = value.decodeNanoseconds; info->download_nanoseconds = value.downloadNanoseconds;
        aeginext::decode::CopyText(info->fallback_reason, sizeof(info->fallback_reason), value.fallbackReason.c_str());
        return AN_DECODE_OK;
    });
}
int32_t AN_DECODE_CALL an_frame_resolve_color(void *frame, an_resolved_color *color, char *error, uint32_t capacity)
{
    return Boundary(error, capacity, [&]() -> int32_t
    {
        ValidateInfo(color);
        const auto *owner = Frame(frame);
        const auto value = aeginext::media::ResolveColor(owner->NativeFrame(), owner->ColorContext());
        *color = {sizeof(*color), AN_DECODE_ABI_VERSION, aeginext::media::CORE_VERSION, value.inferredFields,
            value.range, value.matrix, value.primaries, value.transfer, value.chromaLocation, value.alphaMode};
        return AN_DECODE_OK;
    });
}
