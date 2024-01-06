using AForge.Video;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using SkiaSharp;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;


namespace AForgeNet.AvaloniaVideo
{
    public partial class VideoPlayer :Control
    {
        private readonly GlyphRun _noSkia;
        private IVideoSource videoSource = null;
        // last received frame from the video source
        private Bitmap currentFrame = null;
        // converted version of the current frame (in the case if current frame is a 16 bpp 
        // per color plane image, then the converted image is its 8 bpp version for rendering)
        private Bitmap convertedFrame = null;
        // last error message provided by video source
        private string lastMessage = null;
        // controls border color
        private System.Drawing.Color borderColor = System.Drawing.Color.Black;

        private bool autosize = false;
        private bool keepRatio = false;
        private bool needSizeUpdate = false;
        private bool firstFrameNotProcessed = true;
        private volatile bool requestedToStop = false;
        private object sync = new object();
        private StyledElement? parent;

        [DefaultValue(false)]
        public bool AutoSizeControl
        {
            get { return autosize; }
            set
            {
                autosize = value;
                UpdatePosition();
            }
        }
        [DefaultValue(false)]
        public bool KeepAspectRatio
        {
            get { return keepRatio; }
            set
            {
                keepRatio = value;

            }
        }

        /// <summary>
        /// Control's border color.
        /// </summary>
        /// 
        /// <remarks><para>Specifies color of the border drawn around video frame.</para></remarks>
        /// 
        [DefaultValue(typeof(System.Drawing.Color), "Black")]
        public System.Drawing.Color BorderColor
        {
            get { return borderColor; }
            set
            {
                borderColor = value;


            }
        }

        /// <summary>
        /// Video source to play.
        /// </summary>
        /// 

        [Browsable(false)]
        public IVideoSource VideoSource
        {
            get { return videoSource; }
            set
            {


                // detach events
                if (videoSource != null)
                {
                    videoSource.NewFrame -= new NewFrameEventHandler(videoSource_NewFrame);
                    videoSource.VideoSourceError -= new VideoSourceErrorEventHandler(videoSource_VideoSourceError);
                    videoSource.PlayingFinished -= new PlayingFinishedEventHandler(videoSource_PlayingFinished);
                }

                lock (sync)
                {
                    if (currentFrame != null)
                    {
                        currentFrame.Dispose();
                        currentFrame = null;
                    }
                }

                videoSource = value;

                // atach events
                if (videoSource != null)
                {
                    videoSource.NewFrame += new NewFrameEventHandler(videoSource_NewFrame);
                    videoSource.VideoSourceError += new VideoSourceErrorEventHandler(videoSource_VideoSourceError);
                    videoSource.PlayingFinished += new PlayingFinishedEventHandler(videoSource_PlayingFinished);
                }
                else
                {

                }

                lastMessage = null;
                needSizeUpdate = true;
                firstFrameNotProcessed = true;

            }
        }

        /// <summary>
        /// State of the current video source.
        /// </summary>
        /// 
        /// <remarks><para>Current state of the current video source object - running or not.</para></remarks>
        /// 
        [Browsable(false)]
        public bool IsRunning
        {
            get
            {


                return (videoSource != null) ? videoSource.IsRunning : false;
            }
        }

        /// <summary>
        /// Delegate to notify about new frame.
        /// </summary>
        /// 
        /// <param name="sender">Event sender.</param>
        /// <param name="image">New frame.</param>
        /// 
        public delegate void NewFrameHandler(object sender, ref Bitmap image);

        /// <summary>
        /// New frame event.
        /// </summary>
        /// 
        /// <remarks><para>The event is fired on each new frame received from video source. The
        /// event is fired right after receiving and before displaying, what gives user a chance to
        /// perform some image processing on the new frame and/or update it.</para>
        /// 
        /// <para><note>Users should not keep references of the passed to the event handler image.
        /// If user needs to keep the image, it should be cloned, since the original image will be disposed
        /// by the control when it is required.</note></para>
        /// </remarks>
        /// 
        public event NewFrameHandler NewFrame;

        /// <summary>
        /// Playing finished event.
        /// </summary>
        /// 
        /// <remarks><para>The event is fired when/if video playing finishes. The reason of video
        /// stopping is provided as an argument to the event handler.</para></remarks>
        /// 
        public event PlayingFinishedEventHandler PlayingFinished;

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoSourcePlayer"/> class.
        /// </summary>
        public VideoPlayer(double width = 100, double height = 100)
        {
          
            Width = width;
            Height = height;
            Bounds = new Rect(0, 0, width, height);

            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

            Initialized += SkiaCanvas_Initialized;

            renderingLogic = new RenderingLogic();
            renderingLogic.RenderCall += (canvas) => OnRenderSkia(canvas);
            ClipToBounds = true;
            var text = "Current rendering API is not Skia";
            var glyphs = text.Select(ch => Typeface.Default.GlyphTypeface.GetGlyph(ch)).ToArray();
            _noSkia = new GlyphRun(Typeface.Default.GlyphTypeface, 12, text.AsMemory(), glyphs);
        }
        public VideoPlayer()
        {
            
            Width = 300;
            Height = 300;
            Bounds = new Rect(0, 0, 300, 300);

            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

            Initialized += SkiaCanvas_Initialized;

            renderingLogic = new RenderingLogic();
            renderingLogic.RenderCall += (canvas) => OnRenderSkia(canvas);
            ClipToBounds = true;
            var text = "Current rendering API is not Skia";
            var glyphs = text.Select(ch => Typeface.Default.GlyphTypeface.GetGlyph(ch)).ToArray();
            _noSkia = new GlyphRun(Typeface.Default.GlyphTypeface, 12, text.AsMemory(), glyphs);
        }


        /// <summary>
        /// Start video source and displaying its frames.
        /// </summary>
        public void Start()
        {


            requestedToStop = false;

            if (videoSource != null)
            {
                firstFrameNotProcessed = true;

                videoSource.Start();


            }
        }

        /// <summary>
        /// Stop video source.
        /// </summary>
        /// 
        /// <remarks><para>The method stops video source by calling its <see cref="AForge.Video.IVideoSource.Stop"/>
        /// method, which abourts internal video source's thread. Use <see cref="SignalToStop"/> and
        /// <see cref="WaitForStop"/> for more polite video source stopping, which gives a chance for
        /// video source to perform proper shut down and clean up.
        /// </para></remarks>
        /// 
        public void Stop()
        {

            requestedToStop = true;

            if (videoSource != null)
            {
                videoSource.SignalToStop();
                videoSource.Stop();

                if (currentFrame != null)
                {
                    currentFrame.Dispose();
                    currentFrame = null;
                }



            }
        }

        /// <summary>
        /// Signal video source to stop. 
        /// </summary>
        /// 
        /// <remarks><para>Use <see cref="WaitForStop"/> method to wait until video source
        /// stops.</para></remarks>
        /// 
        public void SignalToStop()
        {

            requestedToStop = true;

            if (videoSource != null)
            {
                videoSource.SignalToStop();
            }
        }

        /// <summary>
        /// Wait for video source has stopped. 
        /// </summary>
        /// 
        /// <remarks><para>Waits for video source stopping after it was signaled to stop using
        /// <see cref="SignalToStop"/> method. If <see cref="SignalToStop"/> was not called, then
        /// it will be called automatically.</para></remarks>
        /// 
        public void WaitForStop()
        {


            if (!requestedToStop)
            {
                SignalToStop();
            }

            if (videoSource != null)
            {
                videoSource.WaitForStop();

                if (currentFrame != null)
                {

                    currentFrame = null;
                }



            }
        }

        /// <summary>
        /// Get clone of current video frame displayed by the control.
        /// </summary>
        /// 
        /// <returns>Returns copy of the video frame, which is currently displayed
        /// by the control - the last video frame received from video source. If the
        /// control did not receive any video frames yet, then the method returns
        /// <see langword="null"/>.</returns>
        /// 
        public Bitmap GetCurrentVideoFrame()
        {
            lock (sync)
            {
                return currentFrame;
            }
        }

        private void UpdatePosition()
        {
            this.UpdateLayout();

        }

        // On new frame ready
        private void videoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            if (!requestedToStop)
            {
                Bitmap newFrame = (Bitmap)eventArgs.Frame.Clone();

                // let user process the frame first
                if (NewFrame != null)
                {
                    NewFrame(this, ref newFrame);
                }
                //if(!this.IsVisible)
                //{
                //    return;
                //}
                // now update current frame of the control
                lock (sync)
                {
                    // dispose previous frame
                    //if (currentFrame != null)
                    //{
                    //    if (currentFrame.Size != eventArgs.Frame.Size)
                    //    {
                    //        needSizeUpdate = true;
                    //    }
                    //    currentFrame = null;
                    //}
                    if (convertedFrame != null)
                    {

                        convertedFrame = null;
                    }

                    currentFrame = newFrame;

                    lastMessage = null;

                    // check if conversion is required to lower bpp rate
                    if ((currentFrame.PixelFormat == System.Drawing.Imaging.PixelFormat.Format16bppGrayScale) ||
                         (currentFrame.PixelFormat == System.Drawing.Imaging.PixelFormat.Format48bppRgb) ||
                         (currentFrame.PixelFormat == System.Drawing.Imaging.PixelFormat.Format64bppArgb))
                    {
                        convertedFrame = currentFrame;
                    }
                }



            }
        }

        // Error occured in video source
        private void videoSource_VideoSourceError(object sender, VideoSourceErrorEventArgs eventArgs)
        {
            lastMessage = eventArgs.Description;


        }

        // Video source has finished playing video
        private void videoSource_PlayingFinished(object sender, ReasonToFinishPlaying reason)
        {
            switch (reason)
            {
                case ReasonToFinishPlaying.EndOfStreamReached:
                    lastMessage = "Video has finished";
                    break;

                case ReasonToFinishPlaying.StoppedByUser:
                    lastMessage = "Video was stopped";
                    break;

                case ReasonToFinishPlaying.DeviceLost:
                    lastMessage = "Video device was unplugged";
                    break;

                case ReasonToFinishPlaying.VideoSourceError:
                    lastMessage = "Video has finished because of error in video source";
                    break;

                default:
                    lastMessage = "Video has finished for unknown reason";
                    break;
            }


            // notify users
            if (PlayingFinished != null)
            {
                PlayingFinished(this, reason);
            }
        }



        // Parent Changed event handler
        private void VideoSourcePlayer_ParentChanged(object sender, EventArgs e)
        {
            if (this.Parent != null)
            {
                this.Parent.PropertyChanged -= Parent_PropertyChanged;
            }

            parent = this.Parent;

            // set handler for Size Changed parent's event
            if (parent != null)
            {
                parent.PropertyChanged += Parent_PropertyChanged;
            }
        }

        private void Parent_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            UpdatePosition();
        }

        // Parent control has changed its size
        private void parent_SizeChanged(object sender, EventArgs e)
        {
            UpdatePosition();
        }
        class RenderingLogic : ICustomDrawOperation
        {
            public Action<SKCanvas> RenderCall;
            public Rect Bounds { get; set; }


            public void Dispose() { }

            public bool Equals(ICustomDrawOperation? other) => other == this;

            // not sure what goes here....
            public bool HitTest(Avalonia.Point p) { return false; }



            public void Render(ImmediateDrawingContext context)
            {

                var skia = context.TryGetFeature<Avalonia.Skia.ISkiaSharpApiLeaseFeature>();
                using (var lease = skia.Lease())
                {
                    SKCanvas canvas = lease.SkCanvas;

                    if (canvas == null)
                    {
                        string str = "Connecting ..";
                        FormattedText text = new FormattedText(str, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, null);
                        context.TryGetFeature<DrawingContext>().DrawText(text,
                           new Avalonia.Point(5, 5));

                    }
                    else
                    {
                        RenderCall?.Invoke(canvas);
                    }
                }
            }
        }

        RenderingLogic renderingLogic;

        //public event Action<SKCanvas> RenderSkia;
        private void SkiaCanvas_Initialized(object? sender, EventArgs e)
        {
            // Remove this if you don't need to do anything when this event is raised.
        }

        public override void Render(DrawingContext context)
        {
            if (renderingLogic == null || renderingLogic.Bounds != this.Bounds)
            {
                // (re)create drawing operation matching actual bounds
                if (renderingLogic != null) renderingLogic.Dispose();
                renderingLogic = new RenderingLogic();
                renderingLogic.RenderCall += (canvas) => OnRenderSkia(canvas);
                renderingLogic.Bounds = new Rect(0, 0, this.Bounds.Width, this.Bounds.Height);
            }
            renderingLogic.Bounds = new Rect(0, 0, this.Bounds.Width, this.Bounds.Height);

            context.Custom((ICustomDrawOperation)renderingLogic);
            Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Background);
        }

        private void OnRenderSkia(SKCanvas canvas)
        {
            SKBitmap sKBitmap = AvaloniaBitmapToSKBitmap(GetCurrentVideoFrame());
            if(sKBitmap == null)
            {
                return;
            }
            canvas.DrawBitmap(sKBitmap, new SKPoint(0, 0));
        }

        public Avalonia.Media.Imaging.Bitmap SKBitmapToAvaloniaBitmap(SKBitmap skBitmap)
        {
            SKData data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            using (Stream stream = data.AsStream())
            {
                return new Avalonia.Media.Imaging.Bitmap(stream);
            }
        }
        public SKBitmap AvaloniaBitmapToSKBitmap(Bitmap bitmap)
        {
            SKBitmap sKBitmap = null;
            if(bitmap==null)
            {
                return null;
            }
            using (MemoryStream memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                sKBitmap = SKBitmap.Decode(memory);

            }
            return sKBitmap;

        }
    }
}
