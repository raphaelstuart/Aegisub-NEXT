using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Material.Icons;
using Material.Icons.Avalonia;
using SkiaSharp;

namespace AegiNext.Desktop.Styling;

internal static class WorkbenchIcon
{
    internal static MaterialIcon Create(string key, double size = 16)
    {
        return new() { Kind = ResolveKind(key), Width = size, Height = size };
    }

    internal static StackPanel Content(string text, string key)
    {
        return new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                Create(key),
                new TextBlock
                    { Text = text, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
            }
        };
    }

    internal static Bitmap CreateNative(string key)
    {
        using var bitmap = new SKBitmap(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var path = SKPath.ParseSvgPathData(MaterialIconDataProvider.GetData(ResolveKind(key)));
        path.FillType = SKPathFillType.EvenOdd;
        using var paint = new SKPaint { Color = new(128, 128, 128), IsAntialias = true };
        canvas.DrawPath(path, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new(stream);
    }

    internal static MaterialIconKind ResolveKind(string key)
    {
        return key switch
        {
            "New" or "NEW_PROJECT" or "File" => MaterialIconKind.FileDocumentOutline,
            "Open" or "OpenProject" or "OPEN_PROJECT" or "OPEN_MEDIA" => MaterialIconKind.FolderOpen,
            "Save" or "SavePreset" or "SAVE_PROJECT" or "LAYOUT_SAVE" => MaterialIconKind.ContentSave,
            "SaveAs" or "SaveEffectAs" or "SAVE_PROJECT_AS" or "LAYOUT_SAVE_AS" => MaterialIconKind.ContentSaveEditOutline,
            "ManageLayouts" or "LAYOUT_MANAGE" => MaterialIconKind.ViewDashboard,
            "RestoreLayout" or "Restore" or "Reset" or "LAYOUT_RESTORE_DEFAULT" => MaterialIconKind.BackupRestore,
            "Add" or "AddLayer" or "ADD_SUBTITLE" => MaterialIconKind.Plus,
            "Delete" or "DeleteKeyframe" or "DELETE_SUBTITLE" => MaterialIconKind.Delete,
            "Undo" or "UNDO" or "Edit" => MaterialIconKind.Undo,
            "Redo" or "REDO" => MaterialIconKind.Redo,
            "Import" or "ImportFont" or "IMPORT_SUBTITLES" or "IMPORT_ASS" => MaterialIconKind.Import,
            "Export" or "ExportText" or "EXPORT_SUBTITLES" or "EXPORT_ASS" or "EXPORT_VIDEO" or "VIEW_EXPORT" => MaterialIconKind.Export,
            "Settings" or "OPEN_SETTINGS" or "ManageStyles" => MaterialIconKind.Cog,
            "Help" => MaterialIconKind.HelpCircleOutline,
            "OPEN_ABOUT" => MaterialIconKind.InformationOutline,
            "CHECK_UPDATES" => MaterialIconKind.Update,
            "Play" or "PLAY_PAUSE" or "Playback" => MaterialIconKind.Play,
            "Pause" => MaterialIconKind.Pause,
            "Loop" => MaterialIconKind.Repeat,
            "EnableHighlight" => MaterialIconKind.FormatColorHighlight,
            "HighlightStyle" => MaterialIconKind.FormatText,
            "AppearanceNormal" => MaterialIconKind.CircleOutline,
            "AppearanceInactive" => MaterialIconKind.ClockOutline,
            "AppearanceActive" => MaterialIconKind.CheckCircle,
            "Backward" or "SEEK_BACKWARD" => MaterialIconKind.Rewind,
            "Forward" or "SEEK_FORWARD" => MaterialIconKind.FastForward,
            "Enter" or "SetStart" or "TIMING_ENTER" => MaterialIconKind.SkipPrevious,
            "ExitTiming" or "SetEnd" or "TIMING_EXIT" => MaterialIconKind.SkipNext,
            "Split" or "SPLIT_SUBTITLE" => MaterialIconKind.CallSplit,
            "Merge" or "MERGE_SUBTITLE" or "MERGE_PROJECT" => MaterialIconKind.CallMerge,
            "Style" or "VIEW_STYLES" => MaterialIconKind.FormatText,
            "ApplyStyle" or "ApplySelectionStyle" => MaterialIconKind.FormatPaint,
            "Effects" or "VIEW_EFFECTS" or "ApplyPreset" or "Fade" or "Pop" or "Slide" => MaterialIconKind.Creation,
            "Subtitles" or "VIEW_SUBTITLES" or "Karaoke" or "ClearKaraoke" => MaterialIconKind.SubtitlesOutline,
            "SubtitleEditor" or "OPEN_SUBTITLE_DETAILS" => MaterialIconKind.TextBoxEditOutline,
            "View" => MaterialIconKind.Eye,
            "VIEW_PREVIEW" => MaterialIconKind.Monitor,
            "Timeline" or "VIEW_TIMELINE" => MaterialIconKind.Timeline,
            "Magnet" => MaterialIconKind.Magnet,
            "Clock" => MaterialIconKind.ClockOutline,
            "LinkedTiming" => MaterialIconKind.LinkVariant,
            "Spectrum" => MaterialIconKind.ChartBar,
            "Waveform" => MaterialIconKind.Waveform,
            "Close" or "Clear" or "EXIT" or "CLOSE_PROJECT" or "Cancel" => MaterialIconKind.Close,
            "Up" => MaterialIconKind.ChevronUp,
            "Down" => MaterialIconKind.ChevronDown,
            "Rectangle" => MaterialIconKind.VectorRectangle,
            "Ellipse" => MaterialIconKind.VectorCircle,
            "Image" => MaterialIconKind.Image,
            "Keyframe" => MaterialIconKind.Diamond,
            "Path" or "ClearPath" or "OrientPath" => MaterialIconKind.VectorPolyline,
            "Mask" or "VIEW_MASKS" or "ClearMask" or "InvertMask" => MaterialIconKind.DramaMasks,
            "Bold" => MaterialIconKind.FormatBold,
            "Italic" => MaterialIconKind.FormatItalic,
            "AlignLeft" => MaterialIconKind.FormatAlignLeft,
            "AlignCenter" => MaterialIconKind.FormatAlignCenter,
            "AlignRight" => MaterialIconKind.FormatAlignRight,
            "AlignTop" => MaterialIconKind.FormatVerticalAlignTop,
            "AlignMiddle" => MaterialIconKind.FormatVerticalAlignCenter,
            "AlignBottom" => MaterialIconKind.FormatVerticalAlignBottom,
            "ApplySelectionPreset" => MaterialIconKind.TextBoxPlusOutline,
            "ClearSelectionStyle" => MaterialIconKind.FormatClear,
            "Volume" => MaterialIconKind.VolumeHigh,
            "Mute" => MaterialIconKind.VolumeOff,
            "Keyboard" or "Record" or "Shortcuts" => MaterialIconKind.Keyboard,
            "Pin" => MaterialIconKind.PinOutline,
            "Unpin" => MaterialIconKind.PinOffOutline,
            "Duplicate" => MaterialIconKind.ContentDuplicate,
            "ValidateScript" => MaterialIconKind.FileCheckOutline,
            "ResetPosition" or "ResetAutomaticPosition" => MaterialIconKind.CrosshairsGps,
            "ResetColors" => MaterialIconKind.FormatColorReset,
            "ManageEffectScripts" => MaterialIconKind.FileCodeOutline,
            "AddPathPoint" => MaterialIconKind.VectorPointPlus,
            "RemovePathPoint" => MaterialIconKind.VectorPointMinus,
            "VIEW_LOG" => MaterialIconKind.TextBoxOutline,
            "END_TEXT_INPUT" => MaterialIconKind.KeyboardReturn,
            _ => MaterialIconKind.Cog
        };
    }
}
