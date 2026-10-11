#pragma once
#include "display_timing_tracker.h"
#include <atomic>
#include <cstdint>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
}
namespace aeginext::media
{
inline constexpr uint32_t CORE_VERSION = 1;
inline constexpr uint32_t CAP_COLOR_RESOLUTION = 1;
inline constexpr uint32_t CAP_DECODE_OPTIONS = 2;
inline constexpr uint32_t CAP_SESSION_INFO = 4;
inline constexpr uint32_t CAP_HARDWARE_DECODE = 8;
inline constexpr uint32_t CAPABILITIES = 15;
enum class ErrorCode : int32_t { InvalidArgument = 2, Unsupported = 3, Io = 4, Decode = 5, Cancelled = 6, InvalidState = 7, NativeFailure = 8, DisplayTimingUnavailable = 9, SeekSuperseded = 10 };
class CoreError final : public std::runtime_error
{
public:
    CoreError(ErrorCode code, const std::string &message, bool hardwareFailure = false) : std::runtime_error(message), code_(code), hardwareFailure_(hardwareFailure) {}
    bool IsHardwareFailure() const noexcept { return hardwareFailure_; }
    ErrorCode Code() const noexcept { return code_; }
private:
    ErrorCode code_;
    bool hardwareFailure_;
};
enum class DecodeMode : uint32_t { Auto = 0, Software = 1, Hardware = 2 };
enum class DecodeWorkload : uint32_t { Interactive = 0, Offline = 1 };
enum class DecoderBackend : uint32_t { Software = 0, VideoToolbox = 1, D3D11VA = 2, Vulkan = 3 };
struct DecodeOptions { DecodeMode mode = DecodeMode::Auto; DecodeWorkload workload = DecodeWorkload::Interactive; };
struct DecoderSessionInfo
{
    DecodeMode requestedMode = DecodeMode::Auto;
    DecoderBackend activeBackend = DecoderBackend::Software;
    bool hardwareConfirmed = false;
    std::string fallbackReason;
    uint64_t generation = 0;
    uint64_t deliveredFrames = 0;
    uint64_t decodeNanoseconds = 0;
    uint64_t downloadNanoseconds = 0;
};
struct SourceColorContext { bool hdrEvidence = false; AVPixelFormat sourcePixelFormat = AV_PIX_FMT_NONE; bool unsupportedColorMetadata = false; };
struct FrameDeleter { void operator()(AVFrame *frame) const noexcept { av_frame_free(&frame); } };
using FramePointer = std::unique_ptr<AVFrame, FrameDeleter>;
void ValidateBackend();
class DecoderSession final
{
public:
    explicit DecoderSession(DecodeOptions options = {});
    ~DecoderSession();
    DecoderSession(const DecoderSession &) = delete;
    DecoderSession &operator=(const DecoderSession &) = delete;
    void Open(const char *path, int32_t streamIndex);
    FramePointer ReadFrame();
    FramePointer ReadFrameForSeek(int64_t timestamp);
    FramePointer ReadFrameForSeek(int64_t timestamp, uint64_t epoch);
    void SetSeekEpoch(uint64_t epoch) noexcept;
    void Seek(int64_t timestamp);
    void Cancel() noexcept;
    AVRational StreamTimeBase() const;
    AVStream *SourceStream() const;
    const DecoderSessionInfo &Info() const noexcept { return info_; }
    SourceColorContext ColorContext() const noexcept { return colorContext_; }
    DisplayTiming OutputDisplayTiming() const noexcept { return outputDisplayTiming_; }
private:
    friend struct DecoderSessionTestAccess;
    static int Interrupt(void *opaque) noexcept;
    static AVPixelFormat SelectFormat(AVCodecContext *context, const AVPixelFormat *formats) noexcept;
    void CheckCancelled() const;
    void CheckSeekEpoch(std::optional<uint64_t> epoch) const;
    void CheckReady() const;
    void OpenAttempt(bool hardware);
    void CloseAttempt() noexcept;
    FramePointer ReadInternal();
    FramePointer ReadOutput(int64_t timestamp, std::optional<uint64_t> epoch = std::nullopt);
    FramePointer ReadSelected(int64_t timestamp, std::optional<uint64_t> epoch);
    FramePointer Download(FramePointer frame);
    void Fallback(const std::string &reason);
    DecodeOptions options_;
    DecoderSessionInfo info_;
    SourceColorContext colorContext_;
    std::atomic<bool> cancelled_{false};
    std::atomic<uint64_t> seekEpoch_{0};
    bool requiresSeek_ = false;
    bool openAttempted_ = false, ready_ = false, failed_ = false;
    bool packetPending_ = false, demuxEof_ = false, drainSent_ = false, decoderEof_ = false;
    bool hardwareAttempt_ = false;
    bool negotiationFailed_ = false;
    int hardwareSetupError_ = 0;
    const char *hardwareSetupFailure_ = nullptr;
    int32_t streamIndex_ = -1;
    int64_t seekTarget_ = AV_NOPTS_VALUE;
    std::string path_;
    AVRational timeBase_{};
    AVPixelFormat hardwareFormat_ = AV_PIX_FMT_NONE;
    AVFormatContext *format_ = nullptr;
    AVCodecContext *codec_ = nullptr;
    AVPacket *packet_ = nullptr;
    AVBufferRef *device_ = nullptr;
    void *videoToolbox_ = nullptr;
    FramePointer scratch_;
    FramePointer pendingFrame_;
    DisplayTimingTracker displayTimingTracker_;
    DisplayTiming latestDisplayTiming_{};
    DisplayTiming pendingDisplayTiming_{};
    DisplayTiming outputDisplayTiming_{};
};
}
