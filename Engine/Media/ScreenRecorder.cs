using Raylib_cs;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace VoxelEngine.Media;

/// <summary>
/// Records the window into an MP4 next to the player's other files on the desktop. The frame is
/// read back from the framebuffer at a steady rate and handed to ffmpeg through a pipe, which
/// scales and encodes it in a process of its own; the game thread only pays for the read-back.
///
/// Frames are queued rather than written on the spot, and the queue is short: when the encoder
/// falls behind, the frames that could not be handed over are added to the next one as repeats,
/// so the video keeps running at wall-clock speed instead of turning into a fast-forward.
///
/// ffmpeg has to be on the PATH; without it the recorder says so and stays off.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    /// <summary>Frames the encoder may lag behind before the game starts dropping read-backs</summary>
    private const int QueueCapacity = 4;

    /// <summary>
    /// Upper bound on how often one frame stands in for the ones that were not read back. A
    /// stutter belongs in the video, so a held frame is repeated rather than skipped; past half
    /// a second the picture was frozen anyway and the video is allowed to be that much shorter.
    /// </summary>
    private const int MaxRepeat = 30;

    private readonly record struct Frame(byte[] Pixels, int Repeat);

    private Process? _ffmpeg;
    private Thread? _writer;
    private BlockingCollection<Frame>? _queue;
    private volatile string? _writeError;

    private int _width;
    private int _height;
    private int _fps;
    private double _pending;
    private int _held;
    private int _lost;

    public bool IsRecording => _ffmpeg != null;

    /// <summary>Length of the running recording in seconds</summary>
    public float ElapsedSeconds { get; private set; }

    /// <summary>Where the running (or last finished) recording is written</summary>
    public string? OutputPath { get; private set; }

    /// <summary>Starts or stops the recording; the returned line is meant for the status message</summary>
    public string Toggle(int targetHeight, int fps) => IsRecording ? Stop() : Start(targetHeight, fps);

    /// <summary>
    /// Opens the file and the encoder. <paramref name="targetHeight"/> is the height of the video
    /// in pixels; 0 keeps the size of the window.
    /// </summary>
    public string Start(int targetHeight, int fps)
    {
        if (IsRecording) return "Already recording";

        _width = Raylib.GetRenderWidth();
        _height = Raylib.GetRenderHeight();
        _fps = Math.Clamp(fps, 10, 120);
        _pending = 0;
        _held = 0;
        _lost = 0;
        _writeError = null;
        ElapsedSeconds = 0f;

        if (_width <= 0 || _height <= 0) return "Recording failed: no window";

        string directory = DesktopDirectory();
        OutputPath = Path.Combine(directory, $"Terraformer-{DateTime.Now:yyyy-MM-dd-HHmmss}.mp4");

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in Arguments(targetHeight, OutputPath))
            startInfo.ArgumentList.Add(argument);

        try
        {
            Directory.CreateDirectory(directory);
            _ffmpeg = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _ffmpeg = null;
            return "Recording needs ffmpeg on the PATH";
        }

        if (_ffmpeg == null) return "Recording needs ffmpeg on the PATH";

        // ffmpeg writes to stderr the whole time; an unread pipe would fill up and stall it
        _ffmpeg.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data)) _writeError = line.Data;
        };
        _ffmpeg.BeginErrorReadLine();

        _queue = new BlockingCollection<Frame>(QueueCapacity);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ScreenRecorder" };
        _writer.Start();

        return $"Recording to {Path.GetFileName(OutputPath)}";
    }

    /// <summary>Closes the encoder and finishes the file</summary>
    public string Stop()
    {
        if (_ffmpeg == null) return "Not recording";

        Process ffmpeg = _ffmpeg;
        _ffmpeg = null;

        _queue!.CompleteAdding();
        _writer!.Join();

        try
        {
            ffmpeg.StandardInput.BaseStream.Close();
            ffmpeg.WaitForExit(10_000);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The encoder is gone already; whatever it managed to write is on disk
        }

        int exitCode = ffmpeg.HasExited ? ffmpeg.ExitCode : -1;
        ffmpeg.Dispose();

        _queue.Dispose();
        _queue = null;
        _writer = null;

        if (exitCode != 0) return $"Recording failed: {_writeError ?? $"ffmpeg exited with {exitCode}"}";

        string name = Path.GetFileName(OutputPath) ?? "video";
        return _lost > 0 ? $"Saved {name} ({_lost} frames lost)" : $"Saved {name}";
    }

    /// <summary>
    /// One frame of the recording, called at the end of the draw so that everything drawn after
    /// it (the recording badge, the status line) stays out of the video. Reads the framebuffer
    /// only when the video is due a frame, not on every game frame.
    ///
    /// <paramref name="frameTime"/> is how long the frame really took, not the step the
    /// simulation was given: a stall the engine clamps away still happened on screen, and a video
    /// clock that skipped it would run ahead of the wall clock.
    /// </summary>
    public void CaptureFrame(float frameTime)
    {
        if (_ffmpeg == null) return;

        if (_ffmpeg.HasExited)
        {
            Stop();
            return;
        }

        ElapsedSeconds += frameTime;
        _pending += frameTime * _fps;

        int due = (int)_pending;
        if (due <= 0) return;
        _pending -= due;

        // A resized window would change the size of every following frame, which the encoder was
        // opened for; finishing the file is better than writing a torn one
        if (Raylib.GetRenderWidth() != _width || Raylib.GetRenderHeight() != _height)
        {
            Stop();
            return;
        }

        int wanted = due + _held;
        int repeat = Math.Min(MaxRepeat, wanted);
        _lost += wanted - repeat;

        byte[] pixels = ReadFramebuffer();

        if (_queue!.TryAdd(new Frame(pixels, repeat)))
        {
            _held = 0;
        }
        else
        {
            // The encoder is behind: this read-back is thrown away and the frames it stood for
            // are carried over, so the next one covers the gap and the video keeps real time
            ArrayPool<byte>.Shared.Return(pixels);
            _held = repeat;
        }
    }

    private unsafe byte[] ReadFramebuffer()
    {
        int length = _width * _height * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(length);

        // raylib hands back RGBA with the rows already the right way up, which is what the pipe
        // expects; the image itself is unmanaged and has to go straight back
        Image image = Raylib.LoadImageFromScreen();
        new ReadOnlySpan<byte>(image.Data, length).CopyTo(pixels);
        Raylib.UnloadImage(image);

        return pixels;
    }

    private void WriterLoop()
    {
        Stream pipe = _ffmpeg!.StandardInput.BaseStream;
        int length = _width * _height * 4;

        try
        {
            foreach (Frame frame in _queue!.GetConsumingEnumerable())
            {
                for (int i = 0; i < frame.Repeat; i++)
                    pipe.Write(frame.Pixels, 0, length);

                ArrayPool<byte>.Shared.Return(frame.Pixels);
            }

            pipe.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _writeError ??= "the encoder closed the pipe";
        }
    }

    private IEnumerable<string> Arguments(int targetHeight, string outputPath)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-y";

        yield return "-f";
        yield return "rawvideo";
        yield return "-pixel_format";
        yield return "rgba";
        yield return "-video_size";
        yield return $"{_width}x{_height}";
        yield return "-framerate";
        yield return _fps.ToString();
        yield return "-i";
        yield return "-";

        yield return "-an";

        // Scaling a window this size is the one heavy step in the pipe; spread it over a few cores
        yield return "-filter_threads";
        yield return "4";

        // yuv420p needs even side lengths, so even the unscaled case goes through the filter
        yield return "-vf";
        yield return targetHeight > 0 && targetHeight < _height
            ? $"scale=-2:{targetHeight}:flags=bilinear"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2";

        yield return "-c:v";
        yield return OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";

        if (!OperatingSystem.IsMacOS())
        {
            yield return "-preset";
            yield return "veryfast";
        }

        yield return "-b:v";
        yield return Bitrate(targetHeight > 0 ? targetHeight : _height);
        yield return "-pix_fmt";
        yield return "yuv420p";
        yield return "-movflags";
        yield return "+faststart";

        yield return outputPath;
    }

    private static string Bitrate(int height) => height switch
    {
        <= 720 => "8M",
        <= 1080 => "16M",
        <= 1440 => "28M",
        _ => "50M",
    };

    private static string DesktopDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop)) return desktop;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }
}
