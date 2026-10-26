using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MihonWindows;

/// <summary>
/// The Mihon window. The emulator draws Mihon's display with the graphics card and leaves every new frame in shared
/// memory, where this window picks it up; mouse, wheel and keyboard input go back through the emulator's gRPC API.
/// Unlike a scrcpy window, nothing is video-encoded on Android's CPU, so scrolling costs about half the CPU and
/// keeps up with 60 fps.
/// </summary>
sealed class EmulatorView : Form
{
    const string Controller = "/android.emulation.control.EmulatorController/";

    /// <summary>The emulator's id for the secondary display the launcher creates for Mihon.</summary>
    public const int Display = 1;

    readonly GrpcConnection grpc;
    readonly GrpcCall input;
    readonly object frameGate = new();
    readonly byte[] bitmapInfo = new byte[40];
    readonly TaskCompletionSource<bool> firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly System.Windows.Forms.Timer resizeTimer = new() { Interval = 150 };
    readonly System.Windows.Forms.Timer wheelTimer = new() { Interval = 120 };
    readonly System.Windows.Forms.Timer moveTimer = new() { Interval = 10 };
    readonly System.Windows.Forms.Timer settleTimer = new() { Interval = SettleDelay };
    readonly Stopwatch moveClock = Stopwatch.StartNew();
    long lastMove = -MoveInterval;
    (int Finger, Point At)? pendingMove;
    readonly HashSet<int> keysDown = new();
    readonly int wheelStep, touchSlop;
    Point wheelFinger;
    bool wheeling;
    MemoryMappedFile frameMap;
    MemoryMappedViewAccessor frameView;
    IntPtr frames;
    string sharedMemory;
    int frameStreams, streamFrames, failedStreams, framesArrived;
    int mappedBytes, frameWidth, frameHeight, paintPending;
    MemoryMappedFile pictureMap;
    MemoryMappedViewAccessor pictureView;
    IntPtr picture;
    string pictureMemory;
    int pictureBytes, pictureMaps, failedPictures;
    uint[] pixels = new uint[0];
    int pixelsWidth, pixelsHeight;
    bool streaming, closed, touching, fullScreen;
    volatile bool minimized;
    string clipboard;
    Rectangle windowedBounds;

    /// <summary>The window's drawing area changed size; Mihon's display should follow.</summary>
    public event Action<Size> SizeWanted;

    /// <summary>Files dragged onto the window.</summary>
    public event Action<string[]> FilesDropped;

    /// <summary>Back was pressed (right click, the mouse's back button or Esc).</summary>
    public event Action BackPressed;

    /// <summary>Completes once the emulator has delivered a frame.</summary>
    public Task FirstFrame => firstFrame.Task;

    /// <summary>Where the window's drawing area was when it last had a normal size and place.</summary>
    public WindowBounds NormalBounds { get; private set; }

    /// <param name="dpi">Mihon's display density, which sets how far a wheel notch scrolls.</param>
    public EmulatorView(GrpcConnection grpc, WindowBounds bounds, int dpi)
    {
        this.grpc = grpc;
        // Android's own distances: 64 dp per wheel notch, 8 dp of movement before a finger starts scrolling.
        wheelStep = 64 * dpi / 160;
        touchSlop = 8 * dpi / 160;
        wheelTimer.Tick += (_, _) => EndWheel();
        moveTimer.Tick += (_, _) => SendPendingMove();
        settleTimer.Tick += (_, _) => Settle();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Mihon";
        Icon = new Icon(Resources.Open("Mihon.ico"));
        BackColor = Color.Black;
        AllowDrop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        ClientSize = new Size(bounds.Width, bounds.Height);
        StartPosition = FormStartPosition.CenterScreen;
        if (bounds.HasPosition)
        {
            StartPosition = FormStartPosition.Manual;
            var border = SizeFromClientSize(ClientSize) - ClientSize;
            Location = new Point(bounds.X - border.Width / 2, bounds.Y - (border.Height - border.Width / 2));
        }
        NormalBounds = bounds;
        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            if (WindowState != FormWindowState.Minimized && ClientSize.Width > 0 && ClientSize.Height > 0) SizeWanted?.Invoke(ClientSize);
        };

        // A top-down BGRA DIB: GDI's fast path.
        BitConverter.GetBytes(40).CopyTo(bitmapInfo, 0);
        BitConverter.GetBytes((short)1).CopyTo(bitmapInfo, 12);
        BitConverter.GetBytes((short)32).CopyTo(bitmapInfo, 14);

        input = grpc.Start(Controller + "streamInputEvent");
        input.End += error =>
        {
            if (!closed) Log.Warn("Input to Android stopped: " + (error ?? "the emulator ended it"));
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Frames are handed to the window as they arrive, so it needs its handle first.
        if (streaming) return;
        streaming = true;
        StartFrames();

        var clipboardStream = grpc.Start(Controller + "streamClipboard");
        var clips = 0;
        clipboardStream.Message += message =>
        {
            // The first message is what Android's clipboard already held, not something just copied in Mihon.
            if (++clips == 1) return;
            foreach (var field in Pb.Read(new ArraySegment<byte>(message)))
            {
                if (field.Field != 1) continue;
                var text = System.Text.Encoding.UTF8.GetString(field.Bytes.Array, field.Bytes.Offset, field.Bytes.Count);
                Ui(() => CopyToWindows(text));
            }
        };
        clipboardStream.Send(Array.Empty<byte>(), last: true);
    }

    void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    // Frames ---------------------------------------------------------------------------------------------------------

    /// <summary>Asks the emulator for every new frame of Mihon's display, delivered in a new piece of shared memory.</summary>
    void StartFrames()
    {
        lock (frameGate)
        {
            Unmap();
            sharedMemory = $"MihonWindows-frames-{Process.GetCurrentProcess().Id}-{++frameStreams}";
            streamFrames = 0;
        }
        var screenshots = grpc.Start(Controller + "streamScreenshot");
        screenshots.Message += OnImage;
        screenshots.End += error => Ui(() => OnFramesEnded(error));
        screenshots.Send(new Pb()
            .Int(1, 1)                                                // RGBA8888
            .Int(5, Display)
            .Message(6, new Pb().Int(1, 1).Text(2, sharedMemory))     // transport: MMAP
            .ToArray(), last: true);
    }

    void OnFramesEnded(string error)
    {
        if (closed) return;
        // The emulator sizes the shared memory for the stream's first frame, and ends the stream when a bigger frame
        // comes (the window grew). So a stream that delivered frames is simply asked for again, in new memory.
        failedStreams = streamFrames > 0 ? 0 : failedStreams + 1;
        if (!grpc.Closed && failedStreams < 3)
        {
            try
            {
                StartFrames();
                return;
            }
            catch (IOException)
            {
            }
        }
        // The emulator closed or crashed: nothing more will show up, so close the window like scrcpy does.
        Log.Warn("Frames from Android stopped: " + (error ?? "the emulator ended them"));
        Close();
    }

    /// <summary>The width and height an Image message's format gives.</summary>
    static (int Width, int Height) ImageSize(byte[] message)
    {
        int width = 0, height = 0;
        foreach (var field in Pb.Read(new ArraySegment<byte>(message)))
        {
            if (field.Field != 1) continue;
            foreach (var format in Pb.Read(field.Bytes))
            {
                if (format.Field == 3) width = (int)format.Number;
                else if (format.Field == 4) height = (int)format.Number;
            }
        }
        return (width, height);
    }

    void OnImage(byte[] message)
    {
        var (width, height) = ImageSize(message);
        if (width <= 0 || height <= 0) return;
        lock (frameGate)
        {
            if (closed) return;
            if (frames == IntPtr.Zero || mappedBytes != width * height * 4) Map(width * height * 4);
            if (frames == IntPtr.Zero) return;
            frameWidth = width;
            frameHeight = height;
            streamFrames++;
            framesArrived++;
        }
        firstFrame.TrySetResult(true);
        if (!minimized && Interlocked.Exchange(ref paintPending, 1) == 0) Ui(PaintFrame);
    }

    void Map(int bytes)
    {
        Unmap();
        // The emulator prefixes its Windows shared memory names with "SHM_".
        foreach (var name in new[] { "SHM_" + sharedMemory, sharedMemory })
        {
            try
            {
                frameMap = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                frameView = frameMap.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.Read);
                unsafe
                {
                    byte* pointer = null;
                    frameView.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    frames = (IntPtr)pointer;
                }
                mappedBytes = bytes;
                return;
            }
            catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Unmap();
            }
        }
        Log.Warn("Couldn't open the shared memory with Android's frames");
    }

    void Unmap()
    {
        if (frames != IntPtr.Zero) frameView.SafeMemoryMappedViewHandle.ReleasePointer();
        frames = IntPtr.Zero;
        frameView?.Dispose();
        frameMap?.Dispose();
        frameView = null;
        frameMap = null;
        mappedBytes = 0;
    }

    void PaintFrame()
    {
        paintPending = 0;
        lock (frameGate)
        {
            if (frames == IntPtr.Zero) return;
            CopyPixels(frames, frameWidth, frameHeight);
        }
        Draw();
        settleTimer.Stop();
        settleTimer.Start();
    }

    /// <summary>Takes a frame (RGBA, from shared memory) for drawing.</summary>
    unsafe void CopyPixels(IntPtr source, int width, int height)
    {
        var count = width * height;
        if (pixels.Length < count) pixels = new uint[count];
        // The emulator's bytes are RGBA; a DIB wants BGRA. Swapping red and blue here costs about 1 ms a frame,
        // much less than letting GDI convert each pixel.
        var from = (uint*)source;
        fixed (uint* to = pixels)
        {
            for (var i = 0; i < count; i++)
            {
                var v = from[i];
                to[i] = (v & 0xFF00FF00) | ((v & 0xFF) << 16) | ((v >> 16) & 0xFF);
            }
        }
        pixelsWidth = width;
        pixelsHeight = height;
    }

    // The emulator's stream sometimes leaves out the last frame of a change: Android drew it and the emulator holds it,
    // but the stream never sends it, so the window went on showing the screen before (a tab switch didn't show until
    // the next click, which Mihon then took as a second tap on that tab). The stream missed 3 to 17 of 30 tab switches
    // in testing, with OpenGL as with Vulkan. So once frames stop coming, the window asks for the emulator's current
    // picture (about 4 ms of the emulator's time), which was right every time, even 15 ms after the last frame.
    const int SettleDelay = 50;

    void Settle()
    {
        settleTimer.Stop();
        if (closed || minimized) return;
        RequestPicture();
    }

    /// <summary>Asks the emulator for its current picture of Mihon's display, which arrives in shared memory.</summary>
    bool RequestPicture()
    {
        if (failedPictures >= 3) return false;
        int width, height, arrived;
        lock (frameGate)
        {
            width = frameWidth;
            height = frameHeight;
            arrived = framesArrived;
        }
        if (width == 0 || !MapPicture(width * height * 4)) return false;
        // The emulator's pixels are opaque, so a picture it wrote doesn't start with a transparent one.
        unsafe { *(uint*)picture = 0; }
        var memory = pictureMemory;
        try
        {
            var call = grpc.Start(Controller + "getScreenshot");
            call.Message += reply => Ui(() => ShowPicture(reply, memory, arrived));
            call.Send(new Pb()
                .Int(1, 1)                                         // RGBA8888
                .Int(5, Display)
                .Message(6, new Pb().Int(1, 1).Text(2, memory))    // transport: MMAP
                .ToArray(), last: true);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    void ShowPicture(byte[] reply, string memory, int arrived)
    {
        if (closed || memory != pictureMemory) return;
        var (width, height) = ImageSize(reply);
        bool written, copied = false;
        lock (frameGate)
        {
            // A frame that came meanwhile is newer than this picture, and the window settles again after it.
            if (arrived != framesArrived) return;
            unsafe { written = *(uint*)picture >> 24 != 0; }
            if (written && width * height * 4 == pictureBytes)
            {
                CopyPixels(picture, width, height);
                copied = true;
            }
        }
        if (copied) Draw();
        if (written) failedPictures = 0;
        else if (++failedPictures == 3) Log.Warn("The emulator doesn't hand over its current picture, so only its stream of frames shows");
    }

    /// <summary>
    /// Makes the shared memory for the emulator's current picture. Unlike the stream's memory, the window makes it:
    /// memory the emulator makes for one picture is gone again when the call ends.
    /// </summary>
    bool MapPicture(int bytes)
    {
        if (picture != IntPtr.Zero && pictureBytes == bytes) return true;
        UnmapPicture();
        // The emulator puts "SHM_" in front of the name it's given.
        var name = $"MihonWindows-picture-{Process.GetCurrentProcess().Id}-{++pictureMaps}";
        try
        {
            pictureMap = MemoryMappedFile.CreateNew("SHM_" + name, bytes);
            pictureView = pictureMap.CreateViewAccessor(0, bytes);
            unsafe
            {
                byte* pointer = null;
                pictureView.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                picture = (IntPtr)pointer;
            }
            pictureMemory = name;
            pictureBytes = bytes;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't make shared memory for the emulator's current picture: " + e.Message);
            UnmapPicture();
            failedPictures = 3;
            return false;
        }
    }

    void UnmapPicture()
    {
        if (picture != IntPtr.Zero) pictureView.SafeMemoryMappedViewHandle.ReleasePointer();
        picture = IntPtr.Zero;
        pictureView?.Dispose();
        pictureMap?.Dispose();
        pictureView = null;
        pictureMap = null;
        pictureMemory = null;
        pictureBytes = 0;
    }

    void Draw()
    {
        if (pixelsWidth == 0) return;
        BitConverter.GetBytes(pixelsWidth).CopyTo(bitmapInfo, 4);
        BitConverter.GetBytes(-pixelsHeight).CopyTo(bitmapInfo, 8);
        var dc = Native.GetDC(Handle);
        // While Android catches up with a resize, the last frame is stretched over the window.
        Native.SetStretchBltMode(dc, 3 /* COLORONCOLOR */);
        Native.StretchDIBits(dc, 0, 0, ClientSize.Width, ClientSize.Height, 0, 0, pixelsWidth, pixelsHeight, pixels, bitmapInfo, 0, 0x00CC0020 /* SRCCOPY */);
        Native.ReleaseDC(Handle, dc);
    }

    protected override void OnPaint(PaintEventArgs e) => Draw();

    // Size and place -------------------------------------------------------------------------------------------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RememberBounds();
        // Nobody sees the frames of a minimized window, so they aren't copied or drawn until it's back; then the
        // newest one, still in shared memory, is drawn straight away.
        var wasMinimized = minimized;
        minimized = WindowState == FormWindowState.Minimized;
        if (minimized) return;
        if (wasMinimized) PaintFrame();
        resizeTimer.Stop();
        resizeTimer.Start();
        Draw();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        RememberBounds();
    }

    void RememberBounds()
    {
        if (fullScreen || WindowState != FormWindowState.Normal || !IsHandleCreated) return;
        var origin = PointToScreen(Point.Empty);
        NormalBounds = new WindowBounds { X = origin.X, Y = origin.Y, Width = ClientSize.Width, Height = ClientSize.Height, HasPosition = true };
    }

    void ToggleFullScreen()
    {
        fullScreen = !fullScreen;
        if (fullScreen)
        {
            windowedBounds = DesktopBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            DesktopBounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            DesktopBounds = windowedBounds;
        }
    }

    // Input ----------------------------------------------------------------------------------------------------------

    void SendInput(int field, Pb message)
    {
        try { input.Send(new Pb().Message(field, message).ToArray()); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// A window position as a point on Mihon's display, in the display's pixels: the same point, unless Android is
    /// still catching up with a resize.
    /// </summary>
    Point ToDisplay(Point point)
    {
        var width = Math.Max(1, frameWidth == 0 ? ClientSize.Width : frameWidth);
        var height = Math.Max(1, frameHeight == 0 ? ClientSize.Height : frameHeight);
        return new Point(
            Math.Max(0, Math.Min(width - 1, point.X * width / Math.Max(1, ClientSize.Width))),
            Math.Max(0, Math.Min(height - 1, point.Y * height / Math.Max(1, ClientSize.Height))));
    }

    /// <summary>A finger on Mihon's display: pressure while touching, 0 to lift it.</summary>
    void Finger(int id, Point at, bool down) =>
        SendInput(2, new Pb().Message(1, new Pb().Int(1, at.X).Int(2, at.Y).Int(3, id).Int(4, down ? 1024 : 0)).Int(2, Display));

    // The left button is a finger, so dragging scrolls like on a phone.
    void Touch(Point point, bool down) => Finger(0, ToDisplay(point), down);

    // Android draws at most 60 frames a second and smooths touch input itself, so a moving finger is sent at most every
    // 8 ms. A gaming mouse or a precision touchpad reports up to 1000 moves a second, and each would cross gRPC, the
    // emulator and Android's input pipeline: at that rate, drag-scrolling cost about half a core more.
    const int MoveInterval = 8;

    /// <summary>Moves a finger that is down. The newest place wins when moves come faster than they're sent.</summary>
    void MoveFinger(int id, Point at)
    {
        pendingMove = (id, at);
        if (moveClock.ElapsedMilliseconds - lastMove >= MoveInterval) SendPendingMove();
        else moveTimer.Start();
    }

    void SendPendingMove()
    {
        moveTimer.Stop();
        if (pendingMove is not { } move) return;
        pendingMove = null;
        lastMove = moveClock.ElapsedMilliseconds;
        Finger(move.Finger, move.At, true);
    }

    /// <summary>Forgets a move not sent yet; the finger is about to come down or lift somewhere else.</summary>
    void DropPendingMove()
    {
        moveTimer.Stop();
        pendingMove = null;
    }

    void Back()
    {
        SendInput(1, new Pb().Int(2, 2 /* keypress */).Text(4, "GoBack"));
        BackPressed?.Invoke();
    }

    /// <summary>
    /// Scrolls by a wheel's movement, given in pixels. Android here has no mouse, so the wheel scrolls the way a
    /// finger would: a drag from the pointer, lifted once the wheel rests. Standing still when lifted, the finger
    /// doesn't fling the list onwards.
    /// </summary>
    void Wheel(Point pointer, int dx, int dy)
    {
        if (touching || frameWidth == 0) return;
        var moveX = dx;
        var moveY = dy;
        if (!wheeling) StartWheel(pointer, ref moveX, ref moveY);
        var next = new Point(wheelFinger.X + moveX, wheelFinger.Y + moveY);
        if (next.X < 0 || next.Y < 0 || next.X >= frameWidth || next.Y >= frameHeight)
        {
            // No room left to drag: lift the finger and put it down again at the pointer.
            EndWheel();
            moveX = dx;
            moveY = dy;
            StartWheel(pointer, ref moveX, ref moveY);
            next = new Point(Math.Max(0, Math.Min(frameWidth - 1, wheelFinger.X + moveX)),
                Math.Max(0, Math.Min(frameHeight - 1, wheelFinger.Y + moveY)));
        }
        wheelFinger = next;
        MoveFinger(1, next);
        wheelTimer.Stop();
        wheelTimer.Start();
    }

    void StartWheel(Point pointer, ref int moveX, ref int moveY)
    {
        wheeling = true;
        wheelFinger = ToDisplay(pointer);
        DropPendingMove();
        Finger(1, wheelFinger, true);
        // Android starts scrolling once a finger has moved past its touch slop, so the first move covers that too.
        moveX += Math.Sign(moveX) * touchSlop;
        moveY += Math.Sign(moveY) * touchSlop;
    }

    void EndWheel()
    {
        wheelTimer.Stop();
        if (!wheeling) return;
        wheeling = false;
        DropPendingMove();
        Finger(1, wheelFinger, false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        EndWheel();
        switch (e.Button)
        {
            case MouseButtons.Left:
                touching = true;
                Capture = true;
                DropPendingMove();
                Touch(e.Location, true);
                break;
            case MouseButtons.Right:
            case MouseButtons.XButton1:
                Back();
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (touching) MoveFinger(0, ToDisplay(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !touching) return;
        touching = false;
        Capture = false;
        DropPendingMove();
        Touch(e.Location, false);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!touching) return;
        touching = false;
        DropPendingMove();
        Touch(PointToClient(Cursor.Position), false);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // Turning the wheel away from you shows what's above, as a finger dragging down would.
        Wheel(e.Location, 0, e.Delta * wheelStep / 120);
    }

    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105,
        WM_SYSCHAR = 0x106, WM_MOUSEHWHEEL = 0x20E;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => false;

    protected override bool ProcessDialogKey(Keys keyData) => false;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYUP:
            {
                var down = m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                var key = (Keys)(int)m.WParam;
                if (key == Keys.F11)
                {
                    if (down) ToggleFullScreen();
                    return;
                }
                if (key == Keys.F4 && m.Msg == WM_SYSKEYDOWN)
                {
                    base.WndProc(ref m);   // Alt+F4 closes the window
                    return;
                }
                if (down && key == Keys.V && (ModifierKeys & Keys.Control) != 0) CopyToAndroid();
                SendKey(m.LParam, down);
                return;   // not to Windows: a lone Alt would open the window menu
            }
            case WM_CHAR:
            case WM_SYSCHAR:
                return;   // Android makes its own characters from the keys
            case WM_MOUSEHWHEEL:
                // Tilting right shows what's to the right, as a finger dragging left would.
                Wheel(PointToClient(Cursor.Position), -(short)((long)m.WParam >> 16) * wheelStep / 120, 0);
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Sends a key as its keyboard scan code, so Android sees a real (US layout) keyboard: shortcuts, arrows and
    /// keys held down all work, whatever keyboard layout Windows uses.
    /// </summary>
    void SendKey(IntPtr lParam, bool down)
    {
        var bits = (long)lParam;
        var scanCode = (int)((bits >> 16) & 0xFF);
        if (scanCode == 0) return;
        if (((bits >> 24) & 1) != 0) scanCode |= 0xE000;   // extended keys: arrows, right Ctrl, Insert, ...
        if (down) keysDown.Add(scanCode);
        else keysDown.Remove(scanCode);
        SendInput(1, new Pb().Int(1, 3 /* Win scan codes */).Int(2, down ? 0 : 1).Int(3, scanCode));
        if (scanCode == 0x01 && !down) BackPressed?.Invoke();   // Esc works as Back in Android
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        EndWheel();
        // Release keys still held, e.g. Alt after Alt+Tab, so Android doesn't think they stay pressed.
        foreach (var scanCode in keysDown) SendInput(1, new Pb().Int(1, 3).Int(2, 1).Int(3, scanCode));
        keysDown.Clear();
    }

    // Clipboard and files ------------------------------------------------------------------------------------------

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        CopyToAndroid();
    }

    /// <summary>Gives Android the text on the Windows clipboard, so pasting in Mihon pastes it.</summary>
    void CopyToAndroid()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        if (string.IsNullOrEmpty(text) || text == clipboard) return;
        clipboard = text;
        try { grpc.Start(Controller + "setClipboard").Send(new Pb().Text(1, text).ToArray(), last: true); }
        catch (IOException) { }
    }

    /// <summary>Puts text copied in Mihon on the Windows clipboard.</summary>
    void CopyToWindows(string text)
    {
        if (string.IsNullOrEmpty(text) || text == clipboard) return;
        clipboard = text;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        // Exceptions here would vanish into Windows' drag and drop, so they're logged.
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                Log.Info($"{files.Length} file(s) dropped on the window");
                FilesDropped?.Invoke(files);
            }
            else
            {
                Log.Warn("Something without files was dropped on the window: " + string.Join(", ", e.Data.GetFormats()));
            }
        }
        catch (Exception error)
        {
            Log.Warn("Couldn't take what was dropped on the window: " + error);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        resizeTimer.Dispose();
        wheelTimer.Dispose();
        moveTimer.Dispose();
        settleTimer.Dispose();
        lock (frameGate)
        {
            closed = true;
            Unmap();
            UnmapPicture();
        }
        base.OnFormClosed(e);
    }
}
