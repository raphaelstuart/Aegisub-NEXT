#pragma once
#include "frame_owner.h"
#include "media_core.h"
namespace aeginext::decode
{
class DecoderContext final
{
public:
    explicit DecoderContext(aeginext::media::DecodeOptions options = {}) : session_(options) {}
    void Open(const char *path, int32_t streamIndex) { session_.Open(path, streamIndex); }
    std::unique_ptr<FrameOwner> ReadNext();
    std::unique_ptr<FrameOwner> ReadForSeek(int64_t timestamp);
    std::unique_ptr<FrameOwner> ReadForSeek(int64_t timestamp, uint64_t epoch);
    void SetSeekEpoch(uint64_t epoch) noexcept { session_.SetSeekEpoch(epoch); }
    an_decode_ratio StreamTimeBase() const;
    void Seek(int64_t timestamp) { session_.Seek(timestamp); }
    void Cancel() noexcept { session_.Cancel(); }
    const aeginext::media::DecoderSessionInfo &Info() const noexcept { return session_.Info(); }
private:
    aeginext::media::DecoderSession session_;
};
}
