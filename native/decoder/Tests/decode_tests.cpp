#include "frame_owner.h"
#include <array>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <random>
#include <thread>
#include <vector>

extern "C"
{
#include <libavutil/buffer.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/mem.h>
int an_decode_c_abi_test(void);
}

using namespace aeginext::decode;

namespace
{
void Require(bool value, const char *message)
{
    if (!value) { throw std::runtime_error(message); }
}

template<typename Action>
void ExpectError(int32_t result, Action action)
{
    try
    {
        action();
    }
    catch (const Error &error)
    {
        Require(error.Result() == result, "Unexpected native error result.");
        return;
    }

    throw std::runtime_error("Expected native error was not thrown.");
}

FramePointer MakeFrame(AVPixelFormat format = AV_PIX_FMT_GRAY8, int width = 3, int height = 2)
{
    FramePointer frame(av_frame_alloc());
    Require(frame != nullptr, "Cannot allocate synthetic frame.");
    frame->format = format;
    frame->width = width;
    frame->height = height;
    CheckAv(av_frame_get_buffer(frame.get(), 32), AN_DECODE_NATIVE_FAILURE, "av_frame_get_buffer(test)");
    return frame;
}

void CopyPaddingAndNegativeStride()
{
    auto frame = MakeFrame(AV_PIX_FMT_GRAY8, 3, 3);
    const auto stride = frame->linesize[0];
    Require(stride > 3, "Synthetic fixture should have padding.");
    for (int row = 0; row < 3; ++row)
    {
        std::memset(frame->data[0] + row * stride, 0xee, stride);
        for (int column = 0; column < 3; ++column)
        {
            frame->data[0][row * stride + column] = static_cast<uint8_t>(row * 10 + column);
        }
    }

    FramePointer reversed(av_frame_clone(frame.get()));
    Require(reversed != nullptr, "Cannot clone frame.");
    reversed->data[0] += 2 * stride;
    reversed->linesize[0] = -stride;
    FrameOwner positive(std::move(frame), {1, 1000});
    FrameOwner negative(std::move(reversed), {1, 1000});
    Require(positive.Plane(0).tight_byte_count == 9, "Padding entered tight byte count.");
    Require(negative.Plane(0).native_stride == -stride, "Signed native stride was lost.");
    std::array<uint8_t, 11> bytes{};
    bytes.fill(0xcd);
    positive.CopyPlane(0, bytes.data(), 9);
    Require(bytes == std::array<uint8_t, 11>{0, 1, 2, 10, 11, 12, 20, 21, 22, 0xcd, 0xcd}, "Positive row copy was not tight.");
    negative.CopyPlane(0, bytes.data(), 9);
    Require(bytes == std::array<uint8_t, 11>{20, 21, 22, 10, 11, 12, 0, 1, 2, 0xcd, 0xcd}, "Negative-stride row copy is incorrect.");
    ExpectError(AN_DECODE_INVALID_ARGUMENT, [&]() { negative.CopyPlane(0, bytes.data(), 8); });
    ExpectError(AN_DECODE_INVALID_ARGUMENT, [&]() { negative.CopyPlane(1, bytes.data(), 9); });
    ExpectError(AN_DECODE_INVALID_ARGUMENT, [&]() { negative.CopyPlane(0, nullptr, 9); });
}

void PreservesTenBitOddChromaAndColor()
{
    auto frame = MakeFrame(AV_PIX_FMT_YUV420P10LE, 3, 3);
    frame->color_primaries = AVCOL_PRI_BT2020;
    frame->color_trc = AVCOL_TRC_SMPTE2084;
    frame->colorspace = AVCOL_SPC_BT2020_NCL;
    frame->color_range = AVCOL_RANGE_MPEG;
    frame->chroma_location = AVCHROMA_LOC_LEFT;
    const std::array<uint16_t, 3> row{1, 1023, 731};
    std::memcpy(frame->data[0], row.data(), sizeof(row));
    FrameOwner owner(std::move(frame), {1, 24000});
    Require(owner.Info().plane_count == 3 && owner.Info().component_depth[0] == 10, "10-bit planar format was lost.");
    Require(owner.Plane(0).row_bytes == 6 && owner.Plane(0).rows == 3, "Luma geometry is incorrect.");
    Require(owner.Plane(1).row_bytes == 4 && owner.Plane(1).rows == 2, "Odd chroma geometry was rounded down.");
    Require(std::strcmp(owner.Info().color_transfer_name, "smpte2084") == 0, "PQ frame color was not preserved.");
    std::array<uint8_t, 18> pixels{};
    owner.CopyPlane(0, pixels.data(), pixels.size());
    Require(std::memcmp(pixels.data(), row.data(), sizeof(row)) == 0, "10-bit samples were altered.");
}

void KeepsTimestampEvidenceSeparate()
{
    auto frame = MakeFrame();
    frame->pts = AV_NOPTS_VALUE;
    frame->best_effort_timestamp = 700;
    frame->time_base = {1, 2000};
    frame->duration = 0;
    FrameOwner owner(std::move(frame), {1, 1000});
    const auto &info = owner.Info();
    Require(!(info.flags & AN_FRAME_HAS_PTS) && info.pts == 0, "Best-effort timestamp replaced absent PTS.");
    Require((info.flags & AN_FRAME_HAS_BEST_EFFORT_TIMESTAMP) && info.best_effort_timestamp == 700, "Best-effort evidence was lost.");
    Require(!(info.flags & AN_FRAME_HAS_DURATION), "Unknown duration was fabricated.");
    Require(info.time_base_den == 2000 && info.stream_time_base_den == 1000, "Distinct timestamp units were conflated.");

    auto negative = MakeFrame();
    negative->pts = -25;
    negative->duration = 5;
    negative->time_base = {0, 1};
    FrameOwner negativeOwner(std::move(negative), {1, 48000});
    Require(negativeOwner.Info().pts == -25 && negativeOwner.Info().time_base_den == 48000, "Negative raw PTS or packet units were lost.");
    Require(negativeOwner.Info().duration == 5 && (negativeOwner.Info().flags & AN_FRAME_HAS_DURATION), "Known duration was lost.");
}

void PreservesPartialHdrAndAdditionalSideData()
{
    auto frame = MakeFrame();
    auto *mastering = av_mastering_display_metadata_create_side_data(frame.get());
    Require(mastering != nullptr, "Cannot allocate mastering metadata.");
    mastering->has_luminance = 1;
    mastering->min_luminance = {1, 10000};
    mastering->max_luminance = {1000, 1};
    auto *light = av_content_light_metadata_create_side_data(frame.get());
    Require(light != nullptr, "Cannot allocate CLL metadata.");
    light->MaxCLL = 0;
    light->MaxFALL = 400;
    Require(av_frame_new_side_data(frame.get(), AV_FRAME_DATA_A53_CC, 3) != nullptr, "Cannot allocate extra side data.");
    FrameOwner owner(std::move(frame), {1, 1000});
    const auto &hdr = owner.Hdr();
    Require((hdr.flags & AN_HDR_MASTERING_PRESENT) && (hdr.flags & AN_HDR_MASTERING_HAS_LUMINANCE), "Mastering luminance flags were lost.");
    Require(!(hdr.flags & AN_HDR_MASTERING_HAS_PRIMARIES), "Missing mastering primaries were fabricated.");
    Require(hdr.min_luminance.numerator == 1 && hdr.min_luminance.denominator == 10000, "Mastering rational changed.");
    Require((hdr.flags & AN_HDR_CONTENT_LIGHT_PRESENT) && hdr.max_content_light_level == 0 && hdr.max_frame_average_light_level == 400,
        "Content-light presence was confused with a zero component.");
    Require(owner.Info().side_data_count == 3 && std::strlen(owner.SideDataName(2)) > 0, "Additional side-data identity was discarded.");
}

void PreservesPrimariesWithoutInventingLuminance()
{
    auto frame = MakeFrame();
    auto *metadata = av_mastering_display_metadata_create_side_data(frame.get());
    Require(metadata != nullptr, "Cannot allocate mastering metadata.");
    metadata->has_primaries = 1;
    for (auto &primary : metadata->display_primaries)
    {
        primary[0] = {1, 2};
        primary[1] = {1, 4};
    }
    metadata->white_point[0] = {15635, 50000};
    metadata->white_point[1] = {16450, 50000};
    FrameOwner owner(std::move(frame), {1, 1000});
    Require(owner.Hdr().flags == (AN_HDR_MASTERING_PRESENT | AN_HDR_MASTERING_HAS_PRIMARIES), "HDR presence flags were conflated.");
    Require(owner.Hdr().white_point_x.numerator == 15635 && owner.Hdr().white_point_x.denominator == 50000, "Mastering fraction changed.");
}

void OwnsFrameReferenceIndependently()
{
    int freed = 0;
    {
        FramePointer source(av_frame_alloc());
        Require(source != nullptr, "Cannot allocate source frame.");
        auto *pixels = static_cast<uint8_t *>(av_malloc(64));
        Require(pixels != nullptr, "Cannot allocate source pixels.");
        source->buf[0] = av_buffer_create(pixels, 64, [](void *opaque, uint8_t *data)
        {
            ++*static_cast<int *>(opaque);
            av_free(data);
        }, &freed, 0);
        Require(source->buf[0] != nullptr, "Cannot allocate reference buffer.");
        source->format = AV_PIX_FMT_GRAY8;
        source->width = 3;
        source->height = 2;
        source->data[0] = pixels;
        source->linesize[0] = 32;
        std::memset(pixels, 73, 64);
        FramePointer retained(av_frame_clone(source.get()));
        Require(retained != nullptr, "Cannot retain frame.");
        FrameOwner owner(std::move(retained), {1, 1000});
        source.reset();
        Require(freed == 0, "Pixel buffer was freed while frame owner retained it.");
        std::array<uint8_t, 6> result{};
        owner.CopyPlane(0, result.data(), result.size());
        Require(result == std::array<uint8_t, 6>{73, 73, 73, 73, 73, 73}, "Retained frame pixels changed.");
    }
    Require(freed == 1, "Reference-owned frame pixels were not released exactly once.");
}

void RejectsInvalidPlanesAndMetadata()
{
    auto badStride = MakeFrame();
    badStride->linesize[0] = 1;
    ExpectError(AN_DECODE_DECODE_ERROR, [&]() { FrameOwner owner(std::move(badStride), {1, 1000}); });
    auto truncated = MakeFrame();
    Require(av_frame_new_side_data(truncated.get(), AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, 1) != nullptr, "Cannot allocate malformed side data.");
    ExpectError(AN_DECODE_DECODE_ERROR, [&]() { FrameOwner owner(std::move(truncated), {1, 1000}); });
    auto backwards = MakeFrame();
    backwards->linesize[0] = -backwards->linesize[0];
    ExpectError(AN_DECODE_DECODE_ERROR, [&]() { FrameOwner owner(std::move(backwards), {1, 1000}); });
}

void KeepsUnknownRawColorAndCrop()
{
    auto frame = MakeFrame();
    frame->color_primaries = static_cast<AVColorPrimaries>(9999);
    frame->crop_left = 1;
    frame->flags |= AV_FRAME_FLAG_CORRUPT;
    frame->decode_error_flags = FF_DECODE_ERROR_INVALID_BITSTREAM;
    FrameOwner owner(std::move(frame), {1, 1000});
    Require(owner.Info().color_primaries == 9999 && owner.Info().color_primaries_name[0] == '\0', "Unknown color enum was replaced.");
    Require(owner.Info().width == 3 && owner.Info().crop_left == 1 && owner.Plane(0).row_bytes == 3, "Cropping was silently applied.");
    Require((owner.Info().flags & AN_FRAME_CORRUPT) && owner.Info().decode_error_flags == FF_DECODE_ERROR_INVALID_BITSTREAM,
        "Corruption evidence was discarded.");
}

void RejectsEmptyCropAndAcceptsSingleRemainingPixel()
{
    const std::array<std::array<size_t, 4>, 6> emptyCrops{{
        {3, 0, 0, 0},
        {0, 3, 0, 0},
        {1, 2, 0, 0},
        {0, 0, 2, 0},
        {0, 0, 0, 2},
        {0, 0, 1, 1}
    }};
    for (const auto &crop : emptyCrops)
    {
        auto frame = MakeFrame(AV_PIX_FMT_GRAY8, 3, 2);
        frame->crop_left = crop[0];
        frame->crop_right = crop[1];
        frame->crop_top = crop[2];
        frame->crop_bottom = crop[3];
        ExpectError(AN_DECODE_DECODE_ERROR, [&]() { FrameOwner owner(std::move(frame), {1, 1000}); });
    }

    auto frame = MakeFrame(AV_PIX_FMT_GRAY8, 3, 2);
    frame->crop_left = 1;
    frame->crop_right = 1;
    frame->crop_top = 1;
    FrameOwner owner(std::move(frame), {1, 1000});
    Require(owner.Info().crop_left == 1 && owner.Info().crop_right == 1 && owner.Info().crop_top == 1,
        "A crop leaving a single visible pixel was rejected or changed.");
    Require(owner.Info().width == 3 && owner.Info().height == 2, "Crop metadata was applied to native pixels.");
}

void CopiesSingleRowPaletteWithZeroStride()
{
    auto frame = MakeFrame(AV_PIX_FMT_PAL8, 3, 2);
    Require(frame->data[1] != nullptr, "Palette fixture has no palette data.");
    frame->linesize[1] = 0;
    std::array<uint8_t, 1024> expected{};
    for (size_t index = 0; index < expected.size(); ++index)
    {
        expected[index] = static_cast<uint8_t>(index % 251);
    }
    std::memcpy(frame->data[1], expected.data(), expected.size());

    FrameOwner owner(std::move(frame), {1, 1000});
    const auto plane = owner.Plane(1);
    Require(owner.Info().plane_count == 2 && plane.rows == 1 && plane.native_stride == 0,
        "Single-row palette stride was rejected or changed.");
    Require(plane.row_bytes == 1024 && plane.tight_byte_count == 1024, "Palette byte count is incorrect.");
    std::array<uint8_t, 1024> actual{};
    owner.CopyPlane(1, actual.data(), actual.size());
    Require(actual == expected, "Zero-stride palette bytes were not copied exactly.");
}

class SeekFixture final
{
public:
    explicit SeekFixture(bool singleImage = false)
    {
        std::random_device random;
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            auto candidate = std::filesystem::temp_directory_path() /
                ("aeginext-seek-" + std::to_string(random()) + "-" + std::to_string(random()));
            if (std::filesystem::create_directory(candidate))
            {
                directory_ = std::move(candidate);
                break;
            }
        }
        Require(!directory_.empty(), "Cannot create a unique native seek fixture directory.");
        try
        {
            path_ = directory_ / (singleImage ? "single.ppm" : "frames.y4m");
            std::ofstream output(path_, std::ios::binary);
            output.exceptions(std::ios::badbit | std::ios::failbit);
            if (singleImage)
            {
                output << "P6\n2 2\n255\n";
                const std::array<char, 12> pixels{1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12};
                output.write(pixels.data(), pixels.size());
            }
            else
            {
                output << "YUV4MPEG2 W2 H2 F25:1 Ip A1:1 C420jpeg\n";
                for (char index = 0; index < 4; ++index)
                {
                    output << "FRAME\n";
                    const std::array<char, 6> pixels{index, index, index, index, 64, 64};
                    output.write(pixels.data(), pixels.size());
                }
            }
            output.close();
        }
        catch (...)
        {
            std::error_code error;
            std::filesystem::remove_all(directory_, error);
            throw;
        }
    }

    ~SeekFixture()
    {
        std::error_code error;
        std::filesystem::remove_all(directory_, error);
    }

    std::string Path() const
    {
        const auto utf8 = path_.u8string();
        return std::string(utf8.begin(), utf8.end());
    }

private:
    std::filesystem::path directory_;
    std::filesystem::path path_;
};

using DecoderHandle = std::unique_ptr<void, decltype(&an_decoder_destroy)>;
using FrameHandle = std::unique_ptr<void, decltype(&an_frame_destroy)>;

DecoderHandle OpenSeekFixture(const SeekFixture &fixture)
{
    std::array<char, 512> error{};
    void *handle = nullptr;
    Require(an_decoder_create(&handle, error.data(), error.size()) == AN_DECODE_OK, error.data());
    DecoderHandle decoder(handle, &an_decoder_destroy);
    Require(an_decoder_open(decoder.get(), fixture.Path().c_str(), 0, error.data(), error.size()) == AN_DECODE_OK,
        error.data());
    return decoder;
}

FrameHandle ReadSeekFrame(void *decoder, int64_t expectedPts)
{
    std::array<char, 512> error{};
    void *handle = nullptr;
    Require(an_decoder_read_next(decoder, &handle, error.data(), error.size()) == AN_DECODE_OK, error.data());
    FrameHandle frame(handle, &an_frame_destroy);
    an_frame_info info{};
    info.struct_size = sizeof(info);
    info.abi_version = AN_DECODE_ABI_VERSION;
    Require(an_frame_get_info(frame.get(), &info, error.data(), error.size()) == AN_DECODE_OK, error.data());
    Require((info.flags & AN_FRAME_HAS_PTS) && info.pts == expectedPts, "Seek returned an unexpected absolute frame PTS.");
    return frame;
}

void SeekClearsEofAndPreservesOwnedFrames()
{
    const SeekFixture fixture;
    auto decoder = OpenSeekFixture(fixture);
    std::array<char, 512> error{};
    an_decode_ratio timeBase{};
    Require(an_decoder_get_time_base(decoder.get(), &timeBase, error.data(), error.size()) == AN_DECODE_OK, error.data());
    Require(timeBase.numerator == 1 && timeBase.denominator == 25, "Selected stream time base changed.");
    auto retained = ReadSeekFrame(decoder.get(), 0);
    Require(an_decoder_seek(decoder.get(), INT64_MIN, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT,
        "Missing timestamp sentinel was accepted for seeking.");
    Require(an_decoder_get_time_base(decoder.get(), nullptr, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT,
        "Null time-base output pointer was accepted.");
    for (int64_t pts = 1; pts < 4; ++pts)
    {
        ReadSeekFrame(decoder.get(), pts);
    }
    void *absent = reinterpret_cast<void *>(1);
    for (int iteration = 0; iteration < 2; ++iteration)
    {
        Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_EOF && !absent,
            "Sequential EOF was not stable.");
    }
    Require(an_decoder_seek(decoder.get(), INT64_MIN, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT,
        "Missing timestamp sentinel was accepted after EOF.");
    Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_EOF,
        "Invalid seek changed the EOF state.");
    Require(an_decoder_get_time_base(decoder.get(), &timeBase, error.data(), error.size()) == AN_DECODE_OK,
        "Normal EOF prevented querying the stream time base.");
    Require(an_decoder_seek(decoder.get(), 2, error.data(), error.size()) == AN_DECODE_OK, error.data());
    for (int64_t pts = 1; pts < 4; ++pts)
    {
        ReadSeekFrame(decoder.get(), pts);
    }
    Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_EOF,
        "Decoder did not drain after seeking from EOF.");
    Require(an_decoder_seek(decoder.get(), -1, error.data(), error.size()) == AN_DECODE_OK, error.data());
    ReadSeekFrame(decoder.get(), 0);
    Require(an_decoder_seek(decoder.get(), 3, error.data(), error.size()) == AN_DECODE_OK, error.data());
    ReadSeekFrame(decoder.get(), 2);
    std::thread cancellation([&]() { an_decoder_cancel(decoder.get()); });
    cancellation.join();
    Require(an_decoder_seek(decoder.get(), 0, error.data(), error.size()) == AN_DECODE_CANCELLED,
        "Seek reset sticky cancellation.");
    Require(an_decoder_get_time_base(decoder.get(), &timeBase, error.data(), error.size()) == AN_DECODE_CANCELLED,
        "Time-base query ignored sticky cancellation.");
    Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_CANCELLED,
        "Read resumed after a cancelled seek.");
    decoder.reset();
    std::array<uint8_t, 4> pixels{255, 255, 255, 255};
    Require(an_frame_copy_plane(retained.get(), 0, pixels.data(), pixels.size(), error.data(), error.size()) == AN_DECODE_OK,
        error.data());
    Require(pixels == std::array<uint8_t, 4>{0, 0, 0, 0}, "Seeking or destroying the decoder invalidated an owned frame.");
}

void SeekFailureIsTerminal()
{
    const SeekFixture fixture(true);
    auto decoder = OpenSeekFixture(fixture);
    auto retained = ReadSeekFrame(decoder.get(), 0);
    std::array<char, 512> error{};
    Require(an_decoder_seek(decoder.get(), -1, error.data(), error.size()) == AN_DECODE_IO_ERROR,
        "A demuxer without a keyframe before this target should reject the seek.");
    Require(error[0] != '\0', "A real seek failure returned no diagnostic.");
    Require(an_decoder_seek(decoder.get(), 0, error.data(), error.size()) == AN_DECODE_INVALID_STATE,
        "Seek was allowed after a real demuxer failure.");
    void *absent = reinterpret_cast<void *>(1);
    Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_INVALID_STATE && !absent,
        "Read was allowed after a real demuxer seek failure.");
    an_decode_ratio timeBase{};
    Require(an_decoder_get_time_base(decoder.get(), &timeBase, error.data(), error.size()) == AN_DECODE_INVALID_STATE,
        "Time-base query was allowed on a failed decoder.");
}

void SeekEpochIsNonterminalAndPreservesOwnedFrames()
{
    const SeekFixture fixture;
    auto decoder = OpenSeekFixture(fixture);
    auto retained = ReadSeekFrame(decoder.get(), 0);
    std::array<char, 512> error{};
    an_decoder_set_seek_epoch(decoder.get(), 2);
    void *absent = reinterpret_cast<void *>(1);
    Require(an_decoder_read_for_seek_epoch(decoder.get(), 3, 1, &absent, error.data(), error.size()) == AN_DECODE_SEEK_SUPERSEDED && !absent,
        "Superseded seek did not return the nonterminal status with an empty frame.");
    Require(an_decoder_read_next(decoder.get(), &absent, error.data(), error.size()) == AN_DECODE_INVALID_STATE && !absent,
        "Superseded seek allowed a read before resetting its cursor.");
    an_decoder_set_seek_epoch(decoder.get(), 1);
    Require(an_decoder_seek(decoder.get(), 2, error.data(), error.size()) == AN_DECODE_OK, error.data());
    Require(an_decoder_read_for_seek_epoch(decoder.get(), 2, 2, &absent, error.data(), error.size()) == AN_DECODE_OK,
        "Superseded decoder could not recover or accepted a decreasing epoch.");
    FrameHandle selected(absent, &an_frame_destroy);
    an_frame_info info{};
    info.struct_size = sizeof(info);
    info.abi_version = AN_DECODE_ABI_VERSION;
    Require(an_frame_get_info(selected.get(), &info, error.data(), error.size()) == AN_DECODE_OK && info.pts == 2,
        "Recovered epoch selected the wrong frame.");
    ReadSeekFrame(decoder.get(), 3);
    Require(an_decoder_read_for_seek_epoch(decoder.get(), 3, 2, &absent, error.data(), error.size()) == AN_DECODE_EOF && !absent,
        "Current epoch did not preserve EOF.");
    an_decoder_set_seek_epoch(decoder.get(), 3);
    Require(an_decoder_read_for_seek_epoch(decoder.get(), 3, 2, &absent, error.data(), error.size()) == AN_DECODE_SEEK_SUPERSEDED && !absent,
        "EOF ignored an obsolete request epoch.");
    an_decoder_cancel(decoder.get());
    an_decoder_set_seek_epoch(decoder.get(), 4);
    Require(an_decoder_seek(decoder.get(), 0, error.data(), error.size()) == AN_DECODE_CANCELLED,
        "A new epoch reset terminal cancellation.");
    decoder.reset();
    std::array<uint8_t, 4> pixels{255, 255, 255, 255};
    Require(an_frame_copy_plane(retained.get(), 0, pixels.data(), pixels.size(), error.data(), error.size()) == AN_DECODE_OK &&
        pixels == std::array<uint8_t, 4>{0, 0, 0, 0}, "Seek supersession invalidated an owned frame.");
}

void BackendAbiAndLifecycle()
{
    Require(an_decode_c_abi_test() == 1, "C ABI smoke test failed.");
    Require((an_decode_features() & AN_DECODE_FEATURE_SEEK) != 0, "Seek capability was not advertised.");
    an_decode_backend_info info{};
    info.struct_size = sizeof(info);
    info.abi_version = AN_DECODE_ABI_VERSION;
    std::array<char, 512> error{};
    Require(an_decode_get_backend_info(&info, error.data(), error.size()) == AN_DECODE_OK, error.data());
    Require(info.compile_avformat == info.runtime_avformat && info.compile_avcodec == info.runtime_avcodec &&
        info.compile_avutil == info.runtime_avutil, "Runtime/compile versions differ.");
    info.struct_size = 1;
    Require(an_decode_get_backend_info(&info, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT, "Invalid struct size was accepted.");
    void *decoder = nullptr;
    Require(an_decoder_create(&decoder, error.data(), error.size()) == AN_DECODE_OK, error.data());
    Require(an_decode_live_decoders() == 1, "Decoder lifecycle counter is incorrect.");
    an_decode_ratio timeBase{13, 17};
    Require(an_decoder_get_time_base(decoder, &timeBase, error.data(), error.size()) == AN_DECODE_INVALID_STATE &&
        timeBase.numerator == 13 && timeBase.denominator == 17, "Pre-open query altered the output or succeeded.");
    Require(an_decoder_seek(decoder, 0, error.data(), error.size()) == AN_DECODE_INVALID_STATE, "Pre-open seek succeeded.");
    void *frame = reinterpret_cast<void *>(1);
    Require(an_decoder_read_next(decoder, &frame, error.data(), error.size()) == AN_DECODE_INVALID_STATE && frame == nullptr,
        "Reading before open did not fail cleanly.");
    std::thread cancellation([&]() { an_decoder_cancel(decoder); });
    cancellation.join();
    Require(an_decoder_open(decoder, "/not-used", 0, error.data(), error.size()) == AN_DECODE_CANCELLED, "Pre-open cancellation was ignored.");
    Require(an_decoder_read_next(decoder, &frame, error.data(), error.size()) == AN_DECODE_CANCELLED, "Cancellation was not sticky.");
    an_decoder_destroy(decoder);
    an_decoder_destroy(decoder);
    an_decoder_destroy(nullptr);
    an_frame_destroy(nullptr);
    Require(an_decode_live_decoders() == 0 && an_decode_live_frames() == 0, "Native handles leaked.");
    Require(an_decoder_read_next(decoder, &frame, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT, "Released decoder handle was accepted.");
    Require(an_decoder_seek(decoder, 0, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT, "Seek accepted a released decoder.");
    Require(an_decoder_get_time_base(decoder, &timeBase, error.data(), error.size()) == AN_DECODE_INVALID_ARGUMENT,
        "Time-base query accepted a released decoder.");
    Require(an_decoder_create(nullptr, error.data(), 1) == AN_DECODE_INVALID_ARGUMENT && error[0] == '\0', "Bounded error buffer handling failed.");
}

void DisplayTimingKeepsOriginalFactsAndVariableDurations()
{
    using namespace aeginext::media;
    DisplayTimingTracker tracker;
    tracker.Reset({1, 1000}, AV_NOPTS_VALUE, {}, {});
    auto frame = MakeFrame();
    frame->time_base = {1, 90000};
    frame->pts = 180000;
    frame->best_effort_timestamp = 9000;
    frame->duration = 9000;
    auto timing = tracker.Read(*frame);
    Require(timing.value == 180000 && timing.timeBase.den == 90000 && timing.evidence == DISPLAY_ORIGINAL_PTS,
        "Original PTS did not retain priority or its own time base.");
    frame->pts = AV_NOPTS_VALUE;
    frame->best_effort_timestamp = 2200;
    frame->duration = 18000;
    timing = tracker.Read(*frame);
    Require(timing.value == 2200 && timing.timeBase.den == 1000 && timing.evidence == DISPLAY_BEST_EFFORT,
        "Best-effort did not use the stream time base independently of raw PTS.");
    frame->best_effort_timestamp = AV_NOPTS_VALUE;
    frame->duration = 4500;
    timing = tracker.Read(*frame);
    Require(timing.value == 2400 && timing.timeBase.den == 1000 &&
        timing.evidence == (DISPLAY_BEST_EFFORT | DISPLAY_PREVIOUS_DURATION),
        "Missing tail timing did not use the previous frame duration in its actual time base.");
    Require(frame->pts == AV_NOPTS_VALUE && frame->best_effort_timestamp == AV_NOPTS_VALUE && frame->duration == 4500,
        "Derived display timing replaced original frame facts.");
    timing = tracker.Read(*frame);
    Require(timing.value == 2450, "Variable frame duration was replaced by fixed frame rate.");
    FrameOwner owner(std::move(frame), {1, 1000}, {}, timing);
    Require(!(owner.Info().flags & (AN_FRAME_HAS_PTS | AN_FRAME_HAS_BEST_EFFORT_TIMESTAMP)) &&
        owner.DisplayTiming().timestamp == 2450 && owner.DisplayTiming().evidence ==
        (AN_DISPLAY_BEST_EFFORT | AN_DISPLAY_PREVIOUS_DURATION), "Frame ABI conflated derived and raw timing.");
}

void DisplayTimingRequiresAnchorsAndCompatibleDeclaredRates()
{
    using namespace aeginext::media;
    DisplayTimingTracker tracker;
    auto frame = MakeFrame();
    tracker.Reset({1, 24000}, 240000, {24000, 1001}, {24000, 1001});
    auto timing = tracker.Read(*frame);
    Require(timing.value == 240000 && timing.evidence == DISPLAY_STREAM_START, "Known stream start was discarded.");
    for (int index = 1; index <= 240; ++index)
    {
        timing = tracker.Read(*frame);
        Require(timing.value == 240000 + index * 1001 &&
            timing.evidence == (DISPLAY_STREAM_START | DISPLAY_DECLARED_FRAME_RATE),
            "Declared fractional rate drifted or lost its provenance.");
    }
    tracker.Reset({1, 1000}, AV_NOPTS_VALUE, {25, 1}, {30, 1});
    frame->pts = 4000;
    tracker.Read(*frame);
    frame->pts = AV_NOPTS_VALUE;
    Require(tracker.Read(*frame).value == AV_NOPTS_VALUE, "Variable/contradictory rate was used as a duration.");
    tracker.Reset({1, 1000}, AV_NOPTS_VALUE, {25, 1}, {25, 1});
    Require(tracker.Read(*frame).value == AV_NOPTS_VALUE, "Missing seek anchor was replaced by an invented origin.");
    tracker.Reset({1, 1000}, 5000, {}, {});
    frame->pts = 7000;
    tracker.Read(*frame);
    frame->pts = AV_NOPTS_VALUE;
    Require(tracker.Read(*frame).value == AV_NOPTS_VALUE, "Stream start was reused for a later missing timestamp.");
    tracker.Reset({1, 1000}, AV_NOPTS_VALUE, {}, {});
    frame->pts = 4000;
    frame->duration = 80;
    tracker.Read(*frame);
    frame->pts = 4000;
    auto duplicate = tracker.Read(*frame);
    frame->pts = 3000;
    auto regression = tracker.Read(*frame);
    Require(duplicate.value == 4000 && regression.value == 3000, "Duplicate/decreasing source PTS was rewritten.");
    tracker.Reset({1, 1000}, AV_NOPTS_VALUE, {}, {});
    frame->pts = INT64_MAX - 10;
    frame->duration = 40;
    tracker.Read(*frame);
    frame->pts = AV_NOPTS_VALUE;
    Require(tracker.Read(*frame).value == AV_NOPTS_VALUE, "Derived timestamp arithmetic overflowed.");
}
}

int main()
{
    const std::vector<std::pair<const char *, std::function<void()>>> tests{
        {"padding and negative stride", CopyPaddingAndNegativeStride},
        {"ten-bit odd chroma and color", PreservesTenBitOddChromaAndColor},
        {"timestamp evidence", KeepsTimestampEvidenceSeparate},
        {"independent derived display timing and VFR duration", DisplayTimingKeepsOriginalFactsAndVariableDurations},
        {"display timing anchors and fractional rate evidence", DisplayTimingRequiresAnchorsAndCompatibleDeclaredRates},
        {"partial HDR and side data", PreservesPartialHdrAndAdditionalSideData},
        {"primaries without luminance", PreservesPrimariesWithoutInventingLuminance},
        {"independent frame ownership", OwnsFrameReferenceIndependently},
        {"invalid planes and metadata", RejectsInvalidPlanesAndMetadata},
        {"unknown color and crop", KeepsUnknownRawColorAndCrop},
        {"nonempty crop boundary", RejectsEmptyCropAndAcceptsSingleRemainingPixel},
        {"single-row zero-stride palette", CopiesSingleRowPaletteWithZeroStride},
        {"seek EOF recovery and independent ownership", SeekClearsEofAndPreservesOwnedFrames},
        {"seek failure is terminal", SeekFailureIsTerminal},
        {"nonterminal seek epochs and owned frames", SeekEpochIsNonterminalAndPreservesOwnedFrames},
        {"backend ABI and lifecycle", BackendAbiAndLifecycle}
    };
    int failures = 0;
    for (const auto &[name, test] : tests)
    {
        try
        {
            test();
            std::cout << "PASS " << name << '\n';
        }
        catch (const std::exception &error)
        {
            ++failures;
            std::cerr << "FAIL " << name << ": " << error.what() << '\n';
        }
    }
    return failures == 0 ? 0 : 1;
}
