namespace JustDownload.Core.Media;

/// <summary>
/// A progress snapshot parsed from ffmpeg's machine-readable <c>-progress</c> output (TASK-040): how far
/// through the media ffmpeg has processed (<see cref="OutTime"/>), the current <see cref="Speed"/>
/// multiple, the output size so far, and whether this is the final block.
/// </summary>
/// <param name="OutTime">Processed media timestamp (e.g. 12.3s of the output produced so far).</param>
/// <param name="Speed">Processing speed as a real-time multiple (e.g. 17.7 for "17.7x"), if reported.</param>
/// <param name="TotalSize">Bytes written so far, if reported.</param>
/// <param name="IsComplete">Whether ffmpeg reported <c>progress=end</c> (the last block).</param>
public readonly record struct FfmpegProgress(TimeSpan OutTime, double? Speed, long? TotalSize, bool IsComplete);
