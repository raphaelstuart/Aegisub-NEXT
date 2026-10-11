#include "media_core.h"
#include <cstdlib>
#include <iostream>
#include <thread>
namespace aeginext::media
{
struct DecoderSessionTestAccess
{
    static bool ExportsFilmGrain(const DecoderSession &session) { return (session.codec_->export_side_data & AV_CODEC_EXPORT_DATA_FILM_GRAIN) != 0; }
    static void RefuseHardwareFormat(DecoderSession &session) { session.hardwareFormat_ = AV_PIX_FMT_NONE; }
    static void InjectCorruptPacket(DecoderSession &session)
    {
        av_packet_unref(session.packet_);
        if (av_new_packet(session.packet_, 16) < 0) { throw std::bad_alloc(); }
        for (int index = 0; index < 16; ++index) { session.packet_->data[index] = 0; }
        session.packet_->stream_index = session.streamIndex_;
        session.packetPending_ = true;
    }
    static bool NegotiationFailed(const DecoderSession &session) { return session.negotiationFailed_; }
    static void InjectSetupAllocationFailure(DecoderSession &session) { session.hardwareSetupError_ = AVERROR(ENOMEM); }
    static void InjectPendingTimestamp(DecoderSession &session, int64_t timestamp)
    {
        session.pendingFrame_.reset(av_frame_alloc());
        if (!session.pendingFrame_) { throw std::bad_alloc(); }
        session.pendingFrame_->pts = timestamp;
        session.pendingDisplayTiming_ = {timestamp, session.timeBase_, timestamp == AV_NOPTS_VALUE ? 0u : DISPLAY_ORIGINAL_PTS};
    }
    static void SupersedeDuringDecoding(DecoderSession &session)
    {
        session.codec_->get_buffer2 = [](AVCodecContext *codec, AVFrame *frame, int flags)
        {
            std::thread supersede([codec] { static_cast<DecoderSession *>(codec->opaque)->SetSeekEpoch(2); });
            supersede.join();
            return avcodec_default_get_buffer2(codec, frame, flags);
        };
    }
    static void RestoreBufferAllocation(DecoderSession &session)
    {
        session.codec_->get_buffer2 = avcodec_default_get_buffer2;
    }
};
}
using namespace aeginext::media;
namespace
{
void Require(bool value, const char *message)
{ if (!value) { throw std::runtime_error(message); } }
void CorruptionNeverTriggersHardwareFallback(const char *path)
{
    DecoderSession session;
    session.Open(path, 0);
    Require(DecoderSessionTestAccess::ExportsFilmGrain(session), "Decoder must expose film-grain parameters without silently synthesizing grain.");
    const auto backend = session.Info().activeBackend;
    const auto reason = session.Info().fallbackReason;
    DecoderSessionTestAccess::InjectCorruptPacket(session);
    try { session.ReadFrame(); }
    catch (const CoreError &error)
    {
        Require(error.Code() == ErrorCode::Decode && !error.IsHardwareFailure(), "Corrupt packet was classified as a hardware failure.");
        Require(session.Info().activeBackend == backend && session.Info().deliveredFrames == 0 && session.Info().fallbackReason == reason,
            "Source corruption changed backend or delivered a frame.");
        Require(!DecoderSessionTestAccess::NegotiationFailed(session), "Corrupt packet claimed a format negotiation failure.");
        return;
    }
    throw std::runtime_error("Invalid source packet was accepted.");
}
void CancellationNeverFallsBack(const char *path)
{
    DecoderSession session;
    session.Open(path, 0);
    Require(DecoderSessionTestAccess::ExportsFilmGrain(session), "Decoder must expose film-grain parameters without silently synthesizing grain.");
    const auto backend = session.Info().activeBackend;
    DecoderSessionTestAccess::RefuseHardwareFormat(session);
    session.Cancel();
    try { session.ReadFrame(); }
    catch (const CoreError &error)
    {
        Require(error.Code() == ErrorCode::Cancelled && !error.IsHardwareFailure(), "Cancellation was classified as a hardware failure.");
        Require(session.Info().activeBackend == backend && session.Info().deliveredFrames == 0, "Cancellation changed backend or delivered a frame.");
        return;
    }
    throw std::runtime_error("Cancelled session returned a frame.");
}
void SetupAllocationFailureNeverFallsBack(const char *path)
{
    DecoderSession session;
    session.Open(path, 0);
    const auto backend = session.Info().activeBackend;
    const auto reason = session.Info().fallbackReason;
    DecoderSessionTestAccess::InjectSetupAllocationFailure(session);
    try { session.ReadFrame(); }
    catch (const std::bad_alloc &)
    {
        Require(session.Info().activeBackend == backend && session.Info().deliveredFrames == 0 && session.Info().fallbackReason == reason,
            "Setup allocation failure changed backend or delivered a frame.");
        return;
    }
    throw std::runtime_error("Hardware setup allocation failure was lost.");
}
void NegotiationFailureFallsBackBeforeDelivery(const char *path)
{
    DecoderSession session;
    session.Open(path, 0);
    if (session.Info().activeBackend == DecoderBackend::Software)
    { std::cout << "Hardware device unavailable; automatic initialization fallback was exercised.\n"; return; }
    DecoderSessionTestAccess::RefuseHardwareFormat(session);
    auto frame = session.ReadFrame();
    Require(frame != nullptr, "Automatic negotiation fallback lost the first valid frame.");
    Require(session.Info().activeBackend == DecoderBackend::Software && !session.Info().hardwareConfirmed &&
        session.Info().deliveredFrames == 1 && !session.Info().fallbackReason.empty(), "Automatic negotiation fallback was not recorded.");
    Require(!DecoderSessionTestAccess::NegotiationFailed(session), "Software reopening retained stale negotiation state.");
}
void HardwareRequiresFailureAndNeverSwitchesAfterDelivery(const char *path)
{
    DecoderSession required({DecodeMode::Hardware, DecodeWorkload::Interactive});
    try { required.Open(path, 0); }
    catch (const CoreError &error)
    {
        Require(error.IsHardwareFailure(), "Required hardware initialization returned a non-hardware failure.");
        std::cout << "Required hardware rejected unavailable device.\n"; return;
    }
    DecoderSessionTestAccess::RefuseHardwareFormat(required);
    bool rejected = false;
    try { required.ReadFrame(); }
    catch (const CoreError &error) { rejected = error.IsHardwareFailure(); }
    Require(rejected && required.Info().deliveredFrames == 0 && required.Info().activeBackend != DecoderBackend::Software,
        "Required hardware silently switched to software.");
    DecoderSession autoSession;
    autoSession.Open(path, 0);
    auto first = autoSession.ReadFrame();
    Require(first != nullptr, "Valid input has no first frame.");
    if (autoSession.Info().activeBackend == DecoderBackend::Software)
    { std::cout << "Hardware first-frame validation refused this fixture; automatic software restart was exercised.\n"; return; }
    const auto backend = autoSession.Info().activeBackend;
    DecoderSessionTestAccess::RefuseHardwareFormat(autoSession);
    rejected = false;
    try { autoSession.ReadFrame(); }
    catch (const CoreError &error) { rejected = error.IsHardwareFailure(); }
    Require(rejected && autoSession.Info().activeBackend == backend && autoSession.Info().deliveredFrames == 1,
        "Hardware failure after delivery silently switched backend or delivered a mixed frame.");
    std::cout << "Executed actual hardware post-delivery refusal: backend=" << static_cast<uint32_t>(backend) << ", hardwareConfirmed=" << autoSession.Info().hardwareConfirmed << '\n';
}

void SeekSelectionPreservesTimestampContracts(const char *path)
{
    DecoderSession baseline({DecodeMode::Software, DecodeWorkload::Interactive});
    baseline.Open(path, 0);
    auto first = baseline.ReadFrame();
    auto second = baseline.ReadFrame();
    Require(first && second && first->pts != AV_NOPTS_VALUE && second->pts > first->pts, "Fixture needs increasing original PTS.");

    DecoderSession duplicate({DecodeMode::Software, DecodeWorkload::Interactive});
    duplicate.Open(path, 0);
    DecoderSessionTestAccess::InjectPendingTimestamp(duplicate, first->pts);
    auto selected = duplicate.ReadFrameForSeek(first->pts);
    Require(selected && selected->pts == first->pts && selected->data[0], "Selection did not retain the last duplicate frame.");
    auto next = duplicate.ReadFrame();
    Require(next && next->pts == second->pts && duplicate.Info().deliveredFrames == 2, "Selection lost or counted the pending next frame twice.");

    for (const auto timestamp : {AV_NOPTS_VALUE, first->pts + 1})
    {
        DecoderSession invalid({DecodeMode::Software, DecodeWorkload::Interactive});
        invalid.Open(path, 0);
        DecoderSessionTestAccess::InjectPendingTimestamp(invalid, timestamp);
        bool rejected = false;
        try { invalid.ReadFrameForSeek(first->pts + 1); }
        catch (const CoreError &error)
        { rejected = error.Code() == (timestamp == AV_NOPTS_VALUE ? ErrorCode::DisplayTimingUnavailable : ErrorCode::Decode) && !error.IsHardwareFailure(); }
        Require(rejected && invalid.Info().deliveredFrames == 0, "Missing or decreasing PTS was accepted during selection.");
        rejected = false;
        try { invalid.ReadFrame(); }
        catch (const CoreError &error) { rejected = error.Code() == ErrorCode::InvalidState; }
        Require(rejected, "Selection error did not make the decoder terminal.");
    }

    DecoderSession sentinel({DecodeMode::Software, DecodeWorkload::Interactive});
    sentinel.Open(path, 0);
    bool rejected = false;
    try { sentinel.ReadFrameForSeek(AV_NOPTS_VALUE); }
    catch (const CoreError &error) { rejected = error.Code() == ErrorCode::InvalidArgument; }
    Require(rejected && sentinel.ReadFrame() != nullptr, "Invalid selection argument consumed or faulted the decoder.");
}

void SupersededNativePrerollRecoversWithoutReopening(const char *path)
{
    DecoderSession baseline({DecodeMode::Software, DecodeWorkload::Interactive});
    baseline.Open(path, 0);
    auto first = baseline.ReadFrame();
    auto second = baseline.ReadFrame();
    Require(first && second && second->pts > first->pts, "Fixture needs two increasing frames.");

    DecoderSession session({DecodeMode::Software, DecodeWorkload::Interactive});
    session.Open(path, 0);
    const auto generation = session.Info().generation;
    session.SetSeekEpoch(1);
    DecoderSessionTestAccess::SupersedeDuringDecoding(session);
    bool superseded = false;
    try { session.ReadFrameForSeek(second->pts, 1); }
    catch (const CoreError &error)
    {
        superseded = error.Code() == ErrorCode::SeekSuperseded && !error.IsHardwareFailure();
    }
    Require(superseded && session.Info().deliveredFrames == 0 && session.Info().downloadNanoseconds == 0,
        "Superseded preroll delivered or downloaded a frame.");
    Require(session.Info().generation == generation, "Supersession reopened the decoder.");
    bool requiresSeek = false;
    try { session.ReadFrame(); }
    catch (const CoreError &error) { requiresSeek = error.Code() == ErrorCode::InvalidState; }
    Require(requiresSeek, "Superseded preroll allowed an ambiguous sequential cursor.");

    DecoderSessionTestAccess::RestoreBufferAllocation(session);
    session.SetSeekEpoch(1);
    session.Seek(first->pts);
    auto recovered = session.ReadFrameForSeek(first->pts, 2);
    Require(recovered && recovered->pts == first->pts && session.Info().generation == generation + 1 &&
        session.Info().deliveredFrames == 1, "Superseded decoder did not recover by seeking or epoch moved backwards.");
    auto next = session.ReadFrame();
    Require(next && next->pts == second->pts, "Recovering a superseded seek lost its lookahead.");
    session.Cancel();
    session.SetSeekEpoch(3);
    bool cancelled = false;
    try { session.Seek(first->pts); }
    catch (const CoreError &error) { cancelled = error.Code() == ErrorCode::Cancelled; }
    Require(cancelled, "Publishing a seek epoch reset terminal cancellation.");
}
}
int main()
{
    const auto *path = std::getenv("AEGINEXT_CORE_TEST_MEDIA_PATH");
    if (!path || !*path)
    { std::cout << "SKIP hardware negotiation fault injection: AEGINEXT_CORE_TEST_MEDIA_PATH is not set.\n"; return 77; }
    std::cout << "Fixture: " << path << '\n';
    try
    {
        CorruptionNeverTriggersHardwareFallback(path);
        CancellationNeverFallsBack(path);
        SetupAllocationFailureNeverFallsBack(path);
        NegotiationFailureFallsBackBeforeDelivery(path);
        HardwareRequiresFailureAndNeverSwitchesAfterDelivery(path);
        SeekSelectionPreservesTimestampContracts(path);
        SupersededNativePrerollRecoversWithoutReopening(path);
        std::cout << "PASS controlled negotiation failure, required hardware refusal, post-delivery failure, source corruption and sticky cancellation\n";
        return 0;
    }
    catch (const std::exception &error) { std::cerr << error.what() << '\n'; return 1; }
}
