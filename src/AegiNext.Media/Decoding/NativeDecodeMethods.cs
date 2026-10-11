using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AegiNext.Media.Decoding;

internal static partial class NativeDecodeMethods
{
    static NativeDecodeMethods()
    {
        NativeMediaRuntime.Initialize();
    }

    internal const uint ABI_VERSION = 1;
    internal const uint SEEK_FEATURE = 1;
    internal const uint SEEK_SELECTION_FEATURE = 8;
    internal const uint DISPLAY_TIMING_FEATURE = 16;
    internal const uint SEEK_SUPERSESSION_FEATURE = 32;
    internal const int EOF = 1;
    internal const int INVALID_ARGUMENT = 2;
    internal const int UNSUPPORTED = 3;
    internal const int CANCELLED = 6;
    internal const int INVALID_STATE = 7;
    internal const int DISPLAY_TIMING_UNAVAILABLE = 9;
    internal const int SEEK_SUPERSEDED = 10;
    internal const int ERROR_CAPACITY = 1024;
    internal const int NAME_CAPACITY = 64;
    private const string LIBRARY = "aeginext_decode";

    internal const uint CORE_VERSION = 1;
    internal const uint CORE_FEATURE = 4;
    internal const uint CORE_CAPABILITIES = 15;

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_core_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CoreVersion();

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_core_capabilities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CoreCapabilities();

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_create_with_options")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int CreateWithOptions(in NativeDecoderOptions options, out nint decoder, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_get_session_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetSessionInfo(VideoDecoderHandle decoder, ref NativeDecoderSessionInfo info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_resolve_color")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int ResolveColor(DecodedFrameHandle frame, ref NativeResolvedColor color, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_features")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Features();

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_live_decoders")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint LiveDecoders();

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_live_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint LiveFrames();

    [LibraryImport(LIBRARY, EntryPoint = "an_decode_get_backend_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetBackendInfo(ref NativeDecodeBackendInfo info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Create(out nint decoder, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_open", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Open(VideoDecoderHandle decoder, string path, int streamIndex, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_read_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int ReadNext(VideoDecoderHandle decoder, out nint frame, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_read_for_seek")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int ReadForSeek(VideoDecoderHandle decoder, long timestamp, out nint frame, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_read_for_seek_epoch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int ReadForSeekEpoch(VideoDecoderHandle decoder, long timestamp, ulong epoch, out nint frame, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_set_seek_epoch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetSeekEpoch(VideoDecoderHandle decoder, ulong epoch);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_get_time_base")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetTimeBase(VideoDecoderHandle decoder, out NativeDecodeRatio timeBase, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_seek")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Seek(VideoDecoderHandle decoder, long timestamp, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_cancel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Cancel(VideoDecoderHandle decoder);

    [LibraryImport(LIBRARY, EntryPoint = "an_decoder_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint decoder);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetFrameInfo(DecodedFrameHandle frame, ref NativeDecodedFrameInfo info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_get_display_timing")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetDisplayTiming(DecodedFrameHandle frame, ref NativeFrameDisplayTiming info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_get_plane_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetPlaneInfo(DecodedFrameHandle frame, uint plane, ref NativeDecodedPlaneInfo info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_get_hdr_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetHdrInfo(DecodedFrameHandle frame, ref NativeDecodedHdrInfo info, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_get_side_data_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetSideDataName(DecodedFrameHandle frame, uint index, byte* name, uint nameCapacity, byte* error, uint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_copy_plane")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int CopyPlane(DecodedFrameHandle frame, uint plane, byte* destination, ulong capacity, byte* error, uint errorCapacity);

    [LibraryImport(LIBRARY, EntryPoint = "an_frame_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyFrame(nint frame);
}
