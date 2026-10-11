#include "decoder_context.h"
namespace aeginext::decode
{
std::unique_ptr<FrameOwner> DecoderContext::ReadNext()
{
    auto frame = session_.ReadFrame();
    if (!frame) { return nullptr; }
    return std::make_unique<FrameOwner>(std::move(frame), session_.StreamTimeBase(), session_.ColorContext(), session_.OutputDisplayTiming());
}
an_decode_ratio DecoderContext::StreamTimeBase() const
{
    const auto value = session_.StreamTimeBase();
    return {value.num, value.den};
}
std::unique_ptr<FrameOwner> DecoderContext::ReadForSeek(int64_t timestamp)
{
    auto frame = session_.ReadFrameForSeek(timestamp);
    if (!frame) { return nullptr; }
    return std::make_unique<FrameOwner>(std::move(frame), session_.StreamTimeBase(), session_.ColorContext(), session_.OutputDisplayTiming());
}
std::unique_ptr<FrameOwner> DecoderContext::ReadForSeek(int64_t timestamp, uint64_t epoch)
{
    auto frame = session_.ReadFrameForSeek(timestamp, epoch);
    if (!frame) { return nullptr; }
    return std::make_unique<FrameOwner>(std::move(frame), session_.StreamTimeBase(), session_.ColorContext(), session_.OutputDisplayTiming());
}
}
