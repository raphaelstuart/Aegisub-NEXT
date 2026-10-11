#include "media_core.h"
#include "hardware_decode_policy.h"
#include "color_resolution.h"
#include "media_core_versions.h"
#include <algorithm>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <mutex>
extern "C"
{
#include <libavutil/cpu.h>
#include <libavutil/mem.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
#ifdef __APPLE__
#include <libavcodec/videotoolbox.h>
#include <libavutil/hwcontext_videotoolbox.h>
#endif
}
namespace aeginext::media
{
namespace
{
using Clock = std::chrono::steady_clock;
uint64_t Elapsed(Clock::time_point start)
{ return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - start).count()); }
void CheckAv(int result, ErrorCode code, const char *operation, bool hardwareFailure = false)
{
    if (result < 0)
    {
        if (result == AVERROR(ENOMEM)) { throw std::bad_alloc(); }
        char message[AV_ERROR_MAX_STRING_SIZE]{};
        av_strerror(result, message, sizeof(message));
        throw CoreError(code, (hardwareFailure ? "Hardware decoder failure: " : "") + std::string(operation) + ": " + message, hardwareFailure);
    }
}
#ifdef __APPLE__
void RegisterVideoToolboxDecoders()
{
    static std::once_flag registration;
    std::call_once(registration, []
    {
        VTRegisterProfessionalVideoWorkflowVideoDecoders();
        for (const auto codec : {kCMVideoCodecType_H264, kCMVideoCodecType_HEVC, kCMVideoCodecType_AV1, kCMVideoCodecType_VP9,
            kCMVideoCodecType_AppleProRes422Proxy, kCMVideoCodecType_AppleProRes422LT, kCMVideoCodecType_AppleProRes422,
            kCMVideoCodecType_AppleProRes422HQ, kCMVideoCodecType_AppleProRes4444, kCMVideoCodecType_AppleProRes4444XQ})
        { VTRegisterSupplementalVideoDecoderIfAvailable(codec); }
    });
}
#endif
}
void ValidateBackend()
{
    static_assert(LIBAVFORMAT_VERSION_INT == CORE_EXPECTED_AVFORMAT);
    static_assert(LIBAVCODEC_VERSION_INT == CORE_EXPECTED_AVCODEC);
    static_assert(LIBAVUTIL_VERSION_INT == CORE_EXPECTED_AVUTIL);
    static_assert(LIBSWSCALE_VERSION_INT == CORE_EXPECTED_SWSCALE);
    if (avformat_version() != CORE_EXPECTED_AVFORMAT || avcodec_version() != CORE_EXPECTED_AVCODEC ||
        avutil_version() != CORE_EXPECTED_AVUTIL || swscale_version() != CORE_EXPECTED_SWSCALE)
    { throw CoreError(ErrorCode::Unsupported, "Media core runtime libraries do not match the pinned SDK."); }
    for (const auto *release : CORE_RELEASES)
    {
        if (std::strcmp(av_version_info(), release) == 0) { return; }
    }
    throw CoreError(ErrorCode::Unsupported, "Media core FFmpeg release is not accepted by the toolchain manifest.");
}
DecoderSession::DecoderSession(DecodeOptions options) : options_(options)
{
    ValidateBackend();
    if (options.mode > DecodeMode::Hardware || options.workload > DecodeWorkload::Offline)
    { throw CoreError(ErrorCode::InvalidArgument, "Invalid decode mode or workload."); }
    info_.requestedMode = options.mode;
}
DecoderSession::~DecoderSession() { CloseAttempt(); }
void DecoderSession::CloseAttempt() noexcept
{
    pendingFrame_.reset();
    latestDisplayTiming_ = pendingDisplayTiming_ = outputDisplayTiming_ = {};
    scratch_.reset();
    av_packet_free(&packet_);
    avcodec_free_context(&codec_);
#ifdef __APPLE__
    auto *context = static_cast<AVVideotoolboxContext *>(videoToolbox_);
    if (context)
    {
        if (context->session) { VTDecompressionSessionInvalidate(context->session); CFRelease(context->session); }
        if (context->cm_fmt_desc) { CFRelease(context->cm_fmt_desc); }
        av_free(context);
    }
#endif
    videoToolbox_ = nullptr;
    av_buffer_unref(&device_);
    avformat_close_input(&format_);
    ready_ = false;
    packetPending_ = demuxEof_ = drainSent_ = decoderEof_ = false;
}
void DecoderSession::Cancel() noexcept { cancelled_.store(true, std::memory_order_release); }
void DecoderSession::SetSeekEpoch(uint64_t epoch) noexcept
{
    auto previous = seekEpoch_.load(std::memory_order_acquire);
    while (previous < epoch && !seekEpoch_.compare_exchange_weak(previous, epoch, std::memory_order_acq_rel, std::memory_order_acquire))
    {
    }
}
void DecoderSession::CheckSeekEpoch(std::optional<uint64_t> epoch) const
{
    CheckCancelled();
    if (epoch && *epoch != seekEpoch_.load(std::memory_order_acquire))
    {
        throw CoreError(ErrorCode::SeekSuperseded, "Seek was superseded; seek again before reading from this session.");
    }
}
int DecoderSession::Interrupt(void *opaque) noexcept
{ return static_cast<DecoderSession *>(opaque)->cancelled_.load(std::memory_order_acquire) ? 1 : 0; }
void DecoderSession::CheckCancelled() const
{
    if (cancelled_.load(std::memory_order_acquire))
    { throw CoreError(ErrorCode::Cancelled, "Decoding was cancelled; this session cannot resume."); }
}
void DecoderSession::CheckReady() const
{
    CheckCancelled();
    if (!ready_ || failed_) { throw CoreError(ErrorCode::InvalidState, "Decoder is not open or a previous operation failed."); }
}
AVPixelFormat DecoderSession::SelectFormat(AVCodecContext *context, const AVPixelFormat *formats) noexcept
{
    auto *session = static_cast<DecoderSession *>(context->opaque);
    for (const auto *format = formats; *format != AV_PIX_FMT_NONE; ++format)
    {
        if (session->hardwareAttempt_)
        {
            if (*format == session->hardwareFormat_)
            {
#ifdef __APPLE__
                AVBufferRef *parameters = nullptr;
                const auto result = avcodec_get_hw_frames_parameters(context, session->device_, *format, &parameters);
                session->hardwareSetupError_ = result;
                if (result < 0)
                {
                    av_buffer_unref(&parameters);
                    session->hardwareSetupFailure_ = "VideoToolbox hardware frame parameter setup failed";
                    session->negotiationFailed_ = result != AVERROR(ENOMEM);
                    return AV_PIX_FMT_NONE;
                }
                const auto *frames = reinterpret_cast<const AVHWFramesContext *>(parameters->data);
                const auto cvFormat = av_map_videotoolbox_format_from_pixfmt2(frames->sw_format, context->color_range == AVCOL_RANGE_JPEG);
                const auto *failure = HardwareReadbackFailure(context->sw_pix_fmt, frames->sw_format);
                av_buffer_unref(&parameters);
                if (!cvFormat || failure)
                {
                    session->hardwareSetupFailure_ = failure ? failure : "VideoToolbox cannot map the negotiated readback format";
                    session->hardwareSetupError_ = AVERROR(ENOTSUP);
                    session->negotiationFailed_ = true;
                    return AV_PIX_FMT_NONE;
                }
                static_cast<AVVideotoolboxContext *>(session->videoToolbox_)->cv_pix_fmt_type = cvFormat;
#endif
                return *format;
            }
        }
        else
        {
            const auto *descriptor = av_pix_fmt_desc_get(*format);
            if (descriptor && !(descriptor->flags & AV_PIX_FMT_FLAG_HWACCEL)) { return *format; }
        }
    }
    if (session->hardwareAttempt_) { session->negotiationFailed_ = true; }
    return AV_PIX_FMT_NONE;
}
void DecoderSession::Open(const char *path, int32_t streamIndex)
{
    CheckCancelled();
    if (openAttempted_) { throw CoreError(ErrorCode::InvalidState, "A decoder can be opened only once."); }
    openAttempted_ = true;
    if (!path || !*path || streamIndex < 0) { throw CoreError(ErrorCode::InvalidArgument, "An absolute local path and nonnegative video stream index are required."); }
    const std::filesystem::path local(std::u8string(path, path + std::strlen(path)));
    if (!local.is_absolute()) { throw CoreError(ErrorCode::InvalidArgument, "Only absolute local media paths are accepted."); }
    std::error_code error;
    if (!std::filesystem::is_regular_file(local, error)) { throw CoreError(ErrorCode::Io, "Input is not a regular local media file."); }
    path_ = path;
    streamIndex_ = streamIndex;
    try
    {
        try { OpenAttempt(options_.mode != DecodeMode::Software); }
        catch (const CoreError &failure)
        {
            if (options_.mode == DecodeMode::Auto && hardwareAttempt_ &&
                failure.IsHardwareFailure())
            { Fallback(failure.what()); }
            else { throw; }
        }
    }
    catch (...) { failed_ = true; CloseAttempt(); throw; }
}
void DecoderSession::OpenAttempt(bool hardware)
{
    CheckCancelled();
    hardwareAttempt_ = hardware;
    negotiationFailed_ = false;
    hardwareSetupError_ = 0;
    hardwareSetupFailure_ = nullptr;
    format_ = avformat_alloc_context();
    if (!format_) { throw std::bad_alloc(); }
    format_->interrupt_callback = {Interrupt, this};
    AVDictionary *options = nullptr;
    CheckAv(av_dict_set(&options, "protocol_whitelist", "file", 0), ErrorCode::NativeFailure, "protocol_whitelist");
    const auto opened = avformat_open_input(&format_, path_.c_str(), nullptr, &options);
    av_dict_free(&options);
    CheckCancelled();
    CheckAv(opened, ErrorCode::Io, "avformat_open_input");
    const auto discovered = avformat_find_stream_info(format_, nullptr);
    CheckCancelled();
    CheckAv(discovered, ErrorCode::Io, "avformat_find_stream_info");
    if (static_cast<uint32_t>(streamIndex_) >= format_->nb_streams ||
        format_->streams[streamIndex_]->index != streamIndex_ || format_->streams[streamIndex_]->codecpar->codec_type != AVMEDIA_TYPE_VIDEO)
    { throw CoreError(ErrorCode::InvalidArgument, "Selected index is not a video stream."); }
    auto *stream = format_->streams[streamIndex_];
    if (stream->time_base.num <= 0 || stream->time_base.den <= 0)
    { throw CoreError(ErrorCode::Unsupported, "The video stream has no valid time base."); }
    timeBase_ = stream->time_base;
    displayTimingTracker_.Reset(timeBase_, stream->start_time, stream->avg_frame_rate, stream->r_frame_rate);
    colorContext_.hdrEvidence |= HasHdrEvidence(stream->codecpar);
    colorContext_.unsupportedColorMetadata |= HasUnsupportedColorMetadata(stream->codecpar);
    colorContext_.sourcePixelFormat = static_cast<AVPixelFormat>(stream->codecpar->format);
    const AVCodec *decoder = nullptr;
    if (hardware)
    {
#ifdef __APPLE__
        constexpr auto deviceType = AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
        hardwareFormat_ = AV_PIX_FMT_VIDEOTOOLBOX;
        info_.activeBackend = DecoderBackend::VideoToolbox;
        RegisterVideoToolboxDecoders();
#elif defined(_WIN32)
        auto deviceType = AV_HWDEVICE_TYPE_D3D11VA;
        hardwareFormat_ = AV_PIX_FMT_D3D11;
        info_.activeBackend = DecoderBackend::D3D11VA;
#else
        constexpr auto deviceType = AV_HWDEVICE_TYPE_NONE;
        throw CoreError(ErrorCode::Unsupported, "Hardware decoding is unavailable on this platform.", true);
#endif
        decoder = FindHardwareDecoder(stream->codecpar->codec_id, deviceType, hardwareFormat_);
#ifdef _WIN32
        const auto *sourceDescriptor = av_pix_fmt_desc_get(colorContext_.sourcePixelFormat);
        const auto extendedHevc = stream->codecpar->codec_id == AV_CODEC_ID_HEVC && sourceDescriptor &&
            (sourceDescriptor->log2_chroma_w != 1 || sourceDescriptor->log2_chroma_h != 1 || sourceDescriptor->comp[0].depth > 10);
        // D3D11 readback supports only NV12/P010 here. Vulkan can preserve HEVC
        // 4:2:2/4:4:4 samples when the actual GPU and driver support that profile.
        if (extendedHevc) { decoder = nullptr; }
        // The pinned Vulkan ProRes 422 implementation does not meet source-sample
        // parity; enable only the validated 12-bit 4444 profiles (including Alpha).
        const auto proRes4444 = stream->codecpar->codec_id == AV_CODEC_ID_PRORES &&
            (stream->codecpar->profile == AV_PROFILE_PRORES_4444 || stream->codecpar->profile == AV_PROFILE_PRORES_XQ);
        if (!decoder && (extendedHevc || proRes4444))
        {
            deviceType = AV_HWDEVICE_TYPE_VULKAN;
            hardwareFormat_ = AV_PIX_FMT_VULKAN;
            info_.activeBackend = DecoderBackend::Vulkan;
            decoder = FindHardwareDecoder(stream->codecpar->codec_id, deviceType, hardwareFormat_);
        }
#endif
        if (!decoder)
        { throw CoreError(ErrorCode::Unsupported, std::string("No configured hardware decoder for codec '") +
            avcodec_get_name(stream->codecpar->codec_id) + "' in the pinned FFmpeg SDK. Choose Auto or CPU decoding.", true); }
#ifdef _WIN32
        if (deviceType == AV_HWDEVICE_TYPE_VULKAN)
        { CheckAv(CreateVulkanHardwareDevice(&device_), ErrorCode::Unsupported, "Vulkan hardware adapter creation", true); }
        else
#endif
        { CheckAv(av_hwdevice_ctx_create(&device_, deviceType, nullptr, nullptr, 0), ErrorCode::Unsupported, "av_hwdevice_ctx_create", true); }
    }
    else
    {
        decoder = avcodec_find_decoder(stream->codecpar->codec_id);
        info_.activeBackend = DecoderBackend::Software;
    }
    if (!decoder) { throw CoreError(ErrorCode::Unsupported, "No decoder is available for this stream."); }
    codec_ = avcodec_alloc_context3(decoder);
    if (!codec_) { throw std::bad_alloc(); }
    CheckAv(avcodec_parameters_to_context(codec_, stream->codecpar), ErrorCode::NativeFailure, "avcodec_parameters_to_context");
    codec_->opaque = this;
    codec_->pkt_timebase = timeBase_;
    codec_->get_format = SelectFormat;
    codec_->apply_cropping = 0;
    if (std::strcmp(decoder->name, "libdav1d") != 0 &&
        !(hardware && info_.activeBackend == DecoderBackend::VideoToolbox && stream->codecpar->codec_id == AV_CODEC_ID_AV1))
    {
        codec_->export_side_data |= AV_CODEC_EXPORT_DATA_FILM_GRAIN;
    }
    codec_->thread_count = hardware || options_.workload == DecodeWorkload::Interactive ? 1 : std::clamp(av_cpu_count(), 1, 4);
    if (hardware)
    {
#ifdef __APPLE__
        auto *context = static_cast<AVVideotoolboxContext *>(av_mallocz(sizeof(AVVideotoolboxContext)));
        if (!context) { throw std::bad_alloc(); }
        videoToolbox_ = context;
        codec_->hwaccel_context = context;
#else
        codec_->hw_device_ctx = av_buffer_ref(device_);
        if (!codec_->hw_device_ctx) { throw std::bad_alloc(); }
#endif
    }
    const auto initialized = avcodec_open2(codec_, decoder, nullptr);
    if (hardwareSetupError_ == AVERROR(ENOMEM)) { throw std::bad_alloc(); }
    CheckAv(initialized, ErrorCode::Decode, hardwareSetupFailure_ ? hardwareSetupFailure_ : "avcodec_open2",
        hardware && (negotiationFailed_ || IsHardwareSetupFailure(initialized)));
    CheckCancelled();
    packet_ = av_packet_alloc();
    scratch_.reset(av_frame_alloc());
    if (!packet_ || !scratch_) { throw std::bad_alloc(); }
    ready_ = true;
    ++info_.generation;
    if (seekTarget_ != AV_NOPTS_VALUE) { Seek(seekTarget_); }
}
void DecoderSession::Fallback(const std::string &reason)
{
    CheckCancelled();
    if (info_.deliveredFrames != 0 || options_.mode != DecodeMode::Auto)
    { throw CoreError(ErrorCode::Decode, "Hardware failed after frame delivery: " + reason); }
    CloseAttempt();
    info_.fallbackReason = reason;
    info_.hardwareConfirmed = false;
    OpenAttempt(false);
    av_log(codec_, AV_LOG_WARNING, "AegiNext actual decoder=software; GPU fallback: %s\n", reason.c_str());
}
AVRational DecoderSession::StreamTimeBase() const { CheckReady(); return timeBase_; }
AVStream *DecoderSession::SourceStream() const { CheckReady(); return format_->streams[streamIndex_]; }
void DecoderSession::Seek(int64_t timestamp)
{
    CheckReady();
    if (timestamp == AV_NOPTS_VALUE) { throw CoreError(ErrorCode::InvalidArgument, "Missing timestamp sentinel is not a seek target."); }
    try
    {
        const auto result = av_seek_frame(format_, streamIndex_, timestamp, AVSEEK_FLAG_BACKWARD);
        CheckCancelled();
        CheckAv(result, ErrorCode::Io, "av_seek_frame");
        avcodec_flush_buffers(codec_);
        av_packet_unref(packet_);
        av_frame_unref(scratch_.get());
        pendingFrame_.reset();
        latestDisplayTiming_ = pendingDisplayTiming_ = outputDisplayTiming_ = {};
        const auto *stream = format_->streams[streamIndex_];
        displayTimingTracker_.Reset(timeBase_, AV_NOPTS_VALUE, stream->avg_frame_rate, stream->r_frame_rate);
        packetPending_ = demuxEof_ = drainSent_ = decoderEof_ = false;
        seekTarget_ = timestamp;
        requiresSeek_ = false;
        ++info_.generation;
    }
    catch (...) { failed_ = true; throw; }
}
FramePointer DecoderSession::ReadFrame()
{
    return ReadOutput(AV_NOPTS_VALUE);
}
FramePointer DecoderSession::ReadFrameForSeek(int64_t timestamp)
{
    if (timestamp == AV_NOPTS_VALUE)
    { throw CoreError(ErrorCode::InvalidArgument, "Missing timestamp sentinel is not a seek target."); }
    return ReadOutput(timestamp);
}
FramePointer DecoderSession::ReadFrameForSeek(int64_t timestamp, uint64_t epoch)
{
    if (timestamp == AV_NOPTS_VALUE)
    {
        throw CoreError(ErrorCode::InvalidArgument, "Missing timestamp sentinel is not a seek target.");
    }
    return ReadOutput(timestamp, epoch);
}
FramePointer DecoderSession::ReadSelected(int64_t timestamp, std::optional<uint64_t> epoch)
{
    CheckSeekEpoch(epoch);
    const auto hadPending = pendingFrame_ != nullptr;
    auto frame = hadPending ? std::move(pendingFrame_) : ReadInternal();
    CheckSeekEpoch(epoch);
    auto timing = hadPending ? pendingDisplayTiming_ : latestDisplayTiming_;
    pendingDisplayTiming_ = {};
    outputDisplayTiming_ = timing;
    if (timestamp == AV_NOPTS_VALUE || !frame) { return frame; }
    if (timing.value == AV_NOPTS_VALUE)
    { throw CoreError(ErrorCode::DisplayTimingUnavailable, "Seek frame has no usable display timing evidence."); }
    if (av_compare_ts(timing.value, timing.timeBase, timestamp, timeBase_) > 0) { return frame; }
    colorContext_.hdrEvidence |= HasHdrEvidence(frame.get());
    while (true)
    {
        CheckSeekEpoch(epoch);
        auto next = ReadInternal();
        CheckSeekEpoch(epoch);
        if (!next) { outputDisplayTiming_ = timing; return frame; }
        const auto nextTiming = latestDisplayTiming_;
        if (nextTiming.value == AV_NOPTS_VALUE)
        { throw CoreError(ErrorCode::DisplayTimingUnavailable, "Seek frame has no usable display timing evidence."); }
        if (av_compare_ts(nextTiming.value, nextTiming.timeBase, timing.value, timing.timeBase) < 0)
        { throw CoreError(ErrorCode::Decode, "Seek frames have decreasing display times."); }
        if (av_compare_ts(nextTiming.value, nextTiming.timeBase, timestamp, timeBase_) > 0)
        {
            pendingFrame_ = std::move(next);
            pendingDisplayTiming_ = nextTiming;
            outputDisplayTiming_ = timing;
            return frame;
        }
        colorContext_.hdrEvidence |= HasHdrEvidence(next.get());
        frame = std::move(next);
        timing = nextTiming;
    }
}
FramePointer DecoderSession::ReadOutput(int64_t timestamp, std::optional<uint64_t> epoch)
{
    CheckReady();
    if (requiresSeek_)
    {
        throw CoreError(ErrorCode::InvalidState, "A superseded selection must be followed by a seek before reading.");
    }
    try
    {
        CheckSeekEpoch(epoch);
        if (decoderEof_ && !pendingFrame_) { return nullptr; }
        FramePointer frame;
        try
        {
            frame = ReadSelected(timestamp, epoch);
            CheckSeekEpoch(epoch);
            if (frame && hardwareAttempt_) { frame = Download(std::move(frame)); }
        }
        catch (const CoreError &failure)
        {
            if (hardwareAttempt_ && options_.mode == DecodeMode::Auto && info_.deliveredFrames == 0 &&
                failure.IsHardwareFailure())
            { Fallback(failure.what()); frame = ReadSelected(timestamp, epoch); }
            else { throw; }
        }
        CheckSeekEpoch(epoch);
        if (frame)
        {
            colorContext_.hdrEvidence |= HasHdrEvidence(frame.get());
            ++info_.deliveredFrames;
            if (info_.deliveredFrames == 1)
            { av_log(codec_, AV_LOG_INFO, "AegiNext actual decoder=%s; hardwareConfirmed=%d\n", info_.activeBackend == DecoderBackend::Software ? "software" : info_.activeBackend == DecoderBackend::VideoToolbox ? "videotoolbox" : info_.activeBackend == DecoderBackend::Vulkan ? "vulkan" : "d3d11va", info_.hardwareConfirmed); }
        }
        return frame;
    }
    catch (const CoreError &error)
    {
        if (error.Code() == ErrorCode::SeekSuperseded)
        {
            pendingFrame_.reset();
            pendingDisplayTiming_ = outputDisplayTiming_ = {};
            requiresSeek_ = true;
        }
        else
        {
            failed_ = true;
        }
        throw;
    }
    catch (...) { failed_ = true; throw; }
}
FramePointer DecoderSession::Download(FramePointer frame)
{
    const auto start = Clock::now();
    if ((frame->flags & AV_FRAME_FLAG_CORRUPT) || frame->decode_error_flags)
    { throw CoreError(ErrorCode::Decode, "Hardware output contains source corruption."); }
    if (frame->format != hardwareFormat_)
    { throw CoreError(ErrorCode::Decode, "Hardware silently changed to software.", true); }
#ifdef __APPLE__
    auto *context = static_cast<AVVideotoolboxContext *>(videoToolbox_);
    if (!context || !context->session) { throw CoreError(ErrorCode::Decode, "VideoToolbox session is unavailable for hardware verification.", true); }
    CFTypeRef property = nullptr;
    const auto result = VTSessionCopyProperty(context->session, kVTDecompressionPropertyKey_UsingHardwareAcceleratedVideoDecoder, kCFAllocatorDefault, &property);
    if (result == kVTAllocationFailedErr || result == memFullErr)
    {
        if (property) { CFRelease(property); }
        throw std::bad_alloc();
    }
    const auto confirmed = result == noErr && property && CFGetTypeID(property) == CFBooleanGetTypeID() && CFBooleanGetValue(static_cast<CFBooleanRef>(property));
    if (property) { CFRelease(property); }
    if (!confirmed) { throw CoreError(ErrorCode::Unsupported, "VideoToolbox did not confirm hardware acceleration (property status=" + std::to_string(result) + ").", true); }
    if (!frame->hw_frames_ctx)
    {
        const auto pixelBuffer = reinterpret_cast<CVPixelBufferRef>(frame->data[3]);
        if (!pixelBuffer) { throw CoreError(ErrorCode::Decode, "VideoToolbox output has no pixel buffer.", true); }
        const auto format = av_map_videotoolbox_format_to_pixfmt(CVPixelBufferGetPixelFormatType(pixelBuffer));
        auto *buffer = av_hwframe_ctx_alloc(device_);
        if (!buffer) { throw std::bad_alloc(); }
        frame->hw_frames_ctx = buffer;
        auto *frames = reinterpret_cast<AVHWFramesContext *>(buffer->data);
        frames->format = AV_PIX_FMT_VIDEOTOOLBOX;
        frames->sw_format = format;
        frames->width = static_cast<int>(CVPixelBufferGetWidth(pixelBuffer));
        frames->height = static_cast<int>(CVPixelBufferGetHeight(pixelBuffer));
        static_cast<AVVTFramesContext *>(frames->hwctx)->color_range = frame->color_range;
        CheckAv(av_hwframe_ctx_init(buffer), ErrorCode::Unsupported, "av_hwframe_ctx_init(VideoToolbox readback)", true);
    }
#else
    if (!frame->hw_frames_ctx) { throw CoreError(ErrorCode::Decode, "Hardware frame has no transfer context.", true); }
#endif
    const auto *frames = reinterpret_cast<const AVHWFramesContext *>(frame->hw_frames_ctx->data);
#ifdef _WIN32
    if (info_.activeBackend == DecoderBackend::D3D11VA && frames->sw_format != AV_PIX_FMT_NV12 && frames->sw_format != AV_PIX_FMT_P010)
    { throw CoreError(ErrorCode::Unsupported, "Hardware readback requires NV12 or P010LE.", true); }
#endif
    const auto sourceFormat = codec_->sw_pix_fmt != AV_PIX_FMT_NONE ? codec_->sw_pix_fmt : colorContext_.sourcePixelFormat;
    if (const auto *failure = HardwareReadbackFailure(sourceFormat, frames->sw_format))
    { throw CoreError(ErrorCode::Unsupported, failure, true); }
    FramePointer cpu(av_frame_alloc());
    if (!cpu) { throw std::bad_alloc(); }
    cpu->format = frames->sw_format;
    CheckAv(av_hwframe_transfer_data(cpu.get(), frame.get(), 0), ErrorCode::Decode, "av_hwframe_transfer_data", true);
    CheckAv(av_frame_copy_props(cpu.get(), frame.get()), ErrorCode::Decode, "av_frame_copy_props(hardware download)", true);
    if (cpu->format != frames->sw_format || cpu->width != frame->width || cpu->height != frame->height || cpu->hw_frames_ctx)
    { throw CoreError(ErrorCode::Decode, "Hardware download changed geometry or produced a non-CPU frame.", true); }
    const auto *sourceDescriptor = av_pix_fmt_desc_get(sourceFormat);
    const auto *outputDescriptor = av_pix_fmt_desc_get(static_cast<AVPixelFormat>(cpu->format));
    if (!(sourceDescriptor->flags & AV_PIX_FMT_FLAG_ALPHA) && (outputDescriptor->flags & AV_PIX_FMT_FLAG_ALPHA) &&
        !HardwareReadbackAlphaIsOpaque(cpu.get()))
    { throw CoreError(ErrorCode::Unsupported, "Hardware readback added nonopaque alpha to an opaque source.", true); }
    info_.hardwareConfirmed = true;
    info_.downloadNanoseconds += Elapsed(start);
    return cpu;
}
FramePointer DecoderSession::ReadInternal()
{
    while (true)
    {
        CheckCancelled();
        auto start = Clock::now();
        const auto received = avcodec_receive_frame(codec_, scratch_.get());
        info_.decodeNanoseconds += Elapsed(start);
        CheckCancelled();
        if (hardwareSetupError_ == AVERROR(ENOMEM)) { throw std::bad_alloc(); }
        if (received == 0)
        {
            FramePointer frame(av_frame_alloc());
            if (!frame) { throw std::bad_alloc(); }
            av_frame_move_ref(frame.get(), scratch_.get());
            if (hardwareAttempt_)
            {
                if ((frame->flags & AV_FRAME_FLAG_CORRUPT) || frame->decode_error_flags)
                { throw CoreError(ErrorCode::Decode, "Hardware output contains source corruption."); }
                if (frame->format != hardwareFormat_)
                { throw CoreError(ErrorCode::Decode, "Hardware silently changed to software.", true); }
            }
            latestDisplayTiming_ = displayTimingTracker_.Read(*frame);
            return frame;
        }
        if (received == AVERROR_EOF)
        {
            if (!drainSent_) { throw CoreError(ErrorCode::Decode, "Decoder reached EOF before drain."); }
            decoderEof_ = true;
            return nullptr;
        }
        if (received != AVERROR(EAGAIN))
        { CheckAv(received, ErrorCode::Decode, negotiationFailed_ ?
            (hardwareSetupFailure_ ? hardwareSetupFailure_ : "Hardware decoder format negotiation failed (avcodec_receive_frame)") : "avcodec_receive_frame",
            negotiationFailed_ || (hardwareAttempt_ && IsHardwareSetupFailure(received))); }
        if (packetPending_)
        {
            start = Clock::now();
            const auto sent = avcodec_send_packet(codec_, packet_);
            info_.decodeNanoseconds += Elapsed(start);
            CheckCancelled();
            if (hardwareSetupError_ == AVERROR(ENOMEM)) { throw std::bad_alloc(); }
            if (sent == AVERROR(EAGAIN)) { throw CoreError(ErrorCode::Decode, "Decoder made no receive/send progress."); }
            CheckAv(sent, ErrorCode::Decode, negotiationFailed_ ?
                (hardwareSetupFailure_ ? hardwareSetupFailure_ : "Hardware decoder format negotiation failed (avcodec_send_packet)") : "avcodec_send_packet",
                negotiationFailed_ || (hardwareAttempt_ && IsHardwareSetupFailure(sent)));
            av_packet_unref(packet_);
            packetPending_ = false;
            continue;
        }
        if (demuxEof_)
        {
            if (drainSent_) { throw CoreError(ErrorCode::Decode, "Decoder requested packets after drain."); }
            start = Clock::now();
            const auto drained = avcodec_send_packet(codec_, nullptr);
            info_.decodeNanoseconds += Elapsed(start);
            CheckCancelled();
            CheckAv(drained, ErrorCode::Decode, "avcodec_send_packet(drain)");
            drainSent_ = true;
            continue;
        }
        while (!packetPending_ && !demuxEof_)
        {
            CheckCancelled();
            const auto read = av_read_frame(format_, packet_);
            CheckCancelled();
            if (read == AVERROR_EOF) { demuxEof_ = true; }
            else
            {
                CheckAv(read, ErrorCode::Io, "av_read_frame");
                if (packet_->stream_index == streamIndex_) { packetPending_ = true; }
                else { av_packet_unref(packet_); }
            }
        }
    }
}
}
