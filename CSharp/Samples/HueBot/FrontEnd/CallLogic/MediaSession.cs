﻿/**************************************************
*                                                                                    *
*   © Microsoft Corporation. All rights reserved.  *
*                                                                                    *
**************************************************/

using FrontEnd.Http;
using FrontEnd.Logging;
using FrontEnd.Media;
using Microsoft.Bing.Speech;
using Microsoft.Skype.Bots.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.RealTimeMediaCalling;
using CorrelationId = FrontEnd.Logging.CorrelationId;

namespace FrontEnd.CallLogic
{
    /// <summary>
    /// This class handles media related logic for a call.
    /// </summary>
    internal class MediaSession : IDisposable
    {
        #region Fields
        /// <summary>
        /// The long dictation URL
        /// </summary>
        private static readonly Uri LongDictationUrl = new Uri(@"wss://speech.platform.bing.com/api/service/recognition/continuous");

        /// <summary>
        /// Indicates if the call has been disposed
        /// </summary>
        private int _disposed;

        /// <summary>
        /// Indicates that the MediaPlatform is ready for video to be sent
        /// </summary>
        private bool _sendVideo;

        /// <summary>
        /// Indicates that the MediaPlatform is ready for audio to be sent
        /// </summary>
        private bool _sendAudio;

        /// <summary>
        /// DefaultHueColor for the video looped back
        /// </summary>
        private Color DefaultHueColor = Color.Blue;

        /// <summary>
        /// Speech client to talk to the bing speech services
        /// </summary>
        private SpeechClient _speechClient;

        /// <summary>
        /// Stream used by the speech for recognition
        /// </summary>
        private SpeechRecognitionPcmStream _recognitionStream;

        /// <summary>
        /// TokenSource to cancel the speech recognition
        /// </summary>
        private CancellationTokenSource _recognitionCts;

        /// <summary>
        /// Wait handle to make sure the speech recognition was stopped
        /// </summary>
        private readonly ManualResetEvent _speechRecoginitionFinished;

        private const int _speechRecognitionTaskTimeOut = 2000;

        private IRealTimeMediaSession _mediaSession;

        #endregion

        #region Public Methods

        /// <summary>
        /// Create a new instance of the MediaSession.
        /// </summary>
        /// <param name="mediaSession"></param>
        public MediaSession(IRealTimeMediaSession mediaSession)
        {
            _mediaSession = mediaSession;
            _speechRecoginitionFinished = new ManualResetEvent(false);

            Log.Info(new CallerInfo(), LogContext.FrontEnd, $"[{_mediaSession.Id}]: Call created");

            try
            {
                var audioSocket = mediaSession.SetAudioSocket(new AudioSocketSettings
                {
                    StreamDirections = StreamDirection.Sendrecv,
                    SupportedAudioFormat = AudioFormat.Pcm16K, // audio format is currently fixed at PCM 16 KHz.
                });

                Log.Info(new CallerInfo(), LogContext.FrontEnd, $"[{_mediaSession.Id}]: Created AudioSocket");

                var videoSocket = mediaSession.SetVideoSocket(new VideoSocketSettings
                {
                    StreamDirections = StreamDirection.Sendrecv,
                    ReceiveColorFormat = VideoColorFormat.NV12,

                    //We loop back the video in this sample. The MediaPlatform always sends only NV12 frames. So include only NV12 video in supportedSendVideoFormats
                    SupportedSendVideoFormats = new List<VideoFormat>() {
                        VideoFormat.NV12_270x480_15Fps,
                        VideoFormat.NV12_320x180_15Fps,
                        VideoFormat.NV12_360x640_15Fps,
                        VideoFormat.NV12_424x240_15Fps,
                        VideoFormat.NV12_480x270_15Fps,
                        VideoFormat.NV12_480x848_30Fps,
                        VideoFormat.NV12_640x360_15Fps,
                        VideoFormat.NV12_720x1280_30Fps,
                        VideoFormat.NV12_848x480_30Fps,
                        VideoFormat.NV12_960x540_30Fps,
                        VideoFormat.NV12_424x240_15Fps
                    },
                });

                Log.Info(new CallerInfo(), LogContext.FrontEnd, $"[{_mediaSession.Id}]: Created VideoSocket");


                //audio socket events
                audioSocket.AudioMediaReceived += OnAudioMediaReceived;
                audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;

                //Video socket events
                videoSocket.VideoMediaReceived += OnVideoMediaReceived;
                videoSocket.VideoSendStatusChanged += OnVideoSendStatusChanged;

                StartSpeechRecognition();
            }
            catch (Exception ex)
            {
                Log.Error(new CallerInfo(), LogContext.FrontEnd, "Error in MediaSession creation" + ex.ToString());
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Unsubscribes all audio/video send/receive-related events, cancels tasks and disposes sockets
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                {
                    Log.Info(new CallerInfo(), LogContext.FrontEnd, $"[{_mediaSession.Id}]: Disposing Call");
                    _sendAudio = false;

                    if (_mediaSession.AudioSocket != null)
                    {
                        _mediaSession.AudioSocket.AudioMediaReceived -= OnAudioMediaReceived;
                        _mediaSession.AudioSocket.AudioSendStatusChanged -= OnAudioSendStatusChanged;
                    }
                    _sendVideo = false;

                    if (_mediaSession.VideoSocket != null)
                    {
                        _mediaSession.VideoSocket.VideoMediaReceived -= OnVideoMediaReceived;
                        _mediaSession.VideoSocket.VideoSendStatusChanged -= OnVideoSendStatusChanged;
                    }

                    _recognitionCts?.Cancel();
                    if (!_speechRecoginitionFinished.WaitOne(_speechRecognitionTaskTimeOut))
                    {
                        Log.Error(new CallerInfo(), LogContext.FrontEnd, "SpeechRecoginition task did not finish within expected time");
                    }

                    _mediaSession?.Dispose();
                    _recognitionCts?.Dispose();
                    _recognitionStream?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(new CallerInfo(), LogContext.FrontEnd, $"[{_mediaSession.Id}]: Ignoring exception in dispose {ex}");
            }
        }
        #endregion

        private void StartSpeechRecognition()
        {
            _recognitionCts = new CancellationTokenSource();

            string speechSubscription = Service.Instance.Configuration.SpeechSubscription;
            Preferences preferences = new Preferences("en-US", LongDictationUrl, new CognitiveServicesAuthorizationProvider(speechSubscription), enableAudioBuffering: false);

            _recognitionStream = new SpeechRecognitionPcmStream(16000);

            var deviceMetadata = new DeviceMetadata(DeviceType.Far, DeviceFamily.Unknown, NetworkType.Unknown, OsName.Windows, "1607", "Dell", "T3600");
            var applicationMetadata = new ApplicationMetadata("HueBot", "1.0.0");
            var requestMetadata = new RequestMetadata(Guid.NewGuid(), deviceMetadata, applicationMetadata, "HueBot");

            Task.Run(async () =>
            {
                do
                {
                    try
                    {
                        //create a speech client
                        using (_speechClient = new SpeechClient(preferences))
                        {
                            _speechClient.SubscribeToRecognitionResult(this.OnRecognitionResult);

                            await _speechClient.RecognizeAsync(new SpeechInput(_recognitionStream, requestMetadata), _recognitionCts.Token);
                        }
                        Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Speech recognize completed.");
                    }
                    catch (Exception exception)
                    {
                        Log.Error(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Speech recognize threw exception {exception.ToString()}");
                    }

                    if (_recognitionCts.IsCancellationRequested)
                    {
                        Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]:Speech recognition cancelled because it was cancelled or max exception count was hit");
                        break;
                    }

                    Stream oldStream = _recognitionStream;
                    Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Restart speech recognition as the call is still alive. Speech recognition could have been completed because of babble/silence timeout");

                    _recognitionStream = new SpeechRecognitionPcmStream(16000);
                    oldStream.Dispose();
                } while (true);

                _speechRecoginitionFinished.Set();
            }
            ).ForgetAndLogException(string.Format("Failed to start the SpeechRecognition Task for Id: {0}", _mediaSession.Id));
        }

        #region Event Handling Methods
        #region Speech
        public Task OnRecognitionResult(RecognitionResult result)
        {
            CorrelationId.SetCurrentId(_mediaSession.CorrelationId);
            if (result.RecognitionStatus != RecognitionStatus.Success)
            {
                Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Speech recognize result {result.RecognitionStatus}");
                return Task.CompletedTask;
            }
            else
            {
                Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Speech recognize success");
            }

            //since we had a success recognition
            try
            {
                foreach (RecognitionPhrase phrase in result.Phrases)
                {
                    string message = phrase.DisplayText.ToLower();
                    Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Received from speech api {message}");

                    int redIndex = message.LastIndexOf("red");
                    int blueIndex = message.LastIndexOf("blue");
                    int greenIndex = message.LastIndexOf("green");

                    int colorIndex = Math.Max(greenIndex, Math.Max(redIndex, blueIndex));
                    if (colorIndex == -1)
                    {
                        return Task.CompletedTask;
                    }

                    if (colorIndex == redIndex)
                    {
                        DefaultHueColor = Color.Red;
                    }
                    else if (colorIndex == blueIndex)
                    {
                        DefaultHueColor = Color.Blue;
                    }
                    else
                    {
                        DefaultHueColor = Color.Green;
                    }

                    Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Changing hue to {DefaultHueColor.ToString()}");
                }
            }
            catch (Exception ex)
            {
                Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Exception in OnRecognitionResult {ex.ToString()}");
            }
            return Task.CompletedTask;
        }
        #endregion

        #region Audio    
        /// <summary>
        /// Callback for informational updates from the media plaform about audio status changes.
        /// Once the status becomes active, audio can be loopbacked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnAudioSendStatusChanged(object sender, AudioSendStatusChangedEventArgs e)
        {
            CorrelationId.SetCurrentId(_mediaSession.CorrelationId);
            Log.Info(
                new CallerInfo(),
                LogContext.Media,
                $"[{_mediaSession.Id}]: AudioSendStatusChangedEventArgs(MediaSendStatus={e.MediaSendStatus})"
                );

            if (e.MediaSendStatus == MediaSendStatus.Active && _sendAudio == false)
            {
                _sendAudio = true;
            }
        }

        /// <summary>
        /// Callback from the media platform when raw audio received.  This method sends the raw
        /// audio to the transcriber. The audio is also loopbacked to the user.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            if (!_sendAudio)
            {
                e.Buffer.Dispose();
                return;
            }

            CorrelationId.SetCurrentId(_mediaSession.CorrelationId);
            Log.Verbose(
                new CallerInfo(),
                LogContext.Media,
                "[{0}] [AudioMediaReceivedEventArgs(Data=<{1}>, Length={2}, Timestamp={3}, AudioFormat={4})]",
                _mediaSession.Id,
                e.Buffer.Data.ToString(),
                e.Buffer.Length,
                e.Buffer.Timestamp,
                e.Buffer.AudioFormat);

            try
            {
                var audioSendBuffer = new AudioSendBuffer(e.Buffer, AudioFormat.Pcm16K, (UInt64)DateTime.Now.Ticks);
                _mediaSession.AudioSocket.Send(audioSendBuffer);

                byte[] buffer = new byte[e.Buffer.Length];
                Marshal.Copy(e.Buffer.Data, buffer, 0, (int)e.Buffer.Length);

                //If the recognize had completed with error/timeout, the underlying stream might have been swapped out on us and disposed.
                //so ignore the objectDisposedException 
                try
                {
                    _recognitionStream.Write(buffer, 0, buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Write on recognitionStream threw ObjectDisposed");
                }
            }
            catch (Exception ex)
            {
                Log.Error(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Caught exception when attempting to send audio buffer {ex.ToString()}");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        #endregion

        #region Video
        /// <summary>
        /// Callback from the media platform when raw video is received. This is loopbacked to the user after adding the hue of the user's choice
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            try
            {
                CorrelationId.SetCurrentId(_mediaSession.CorrelationId);

                Log.Verbose(
                    new CallerInfo(),
                    LogContext.Media,
                    "[{0}] [VideoMediaReceivedEventArgs(Data=<{1}>, Length={2}, Timestamp={3}, Width={4}, Height={5}, ColorFormat={6}, FrameRate={7})]",
                    _mediaSession.Id,
                    e.Buffer.Data.ToString(),
                    e.Buffer.Length,
                    e.Buffer.Timestamp,
                    e.Buffer.VideoFormat.Width,
                    e.Buffer.VideoFormat.Height,
                    e.Buffer.VideoFormat.VideoColorFormat,
                    e.Buffer.VideoFormat.FrameRate);


                byte[] buffer = new byte[e.Buffer.Length];
                Marshal.Copy(e.Buffer.Data, buffer, 0, (int)e.Buffer.Length);

                VideoMediaBuffer videoRenderMediaBuffer = e.Buffer as VideoMediaBuffer;
                AddHue(DefaultHueColor, buffer, e.Buffer.VideoFormat.Width, e.Buffer.VideoFormat.Height);

                VideoFormat sendVideoFormat = GetSendVideoFormat(e.Buffer.VideoFormat);
                var videoSendBuffer = new VideoSendBuffer(buffer, (uint)buffer.Length, sendVideoFormat);
                _mediaSession.VideoSocket.Send(videoSendBuffer);
            }
            catch (Exception ex)
            {
                Log.Error(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}]: Exception in VideoMediaReceived {ex.ToString()}");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private enum Color
        {
            Red,
            Blue,
            Green
        };

        private VideoFormat GetSendVideoFormat(VideoFormat videoFormat)
        {
            VideoFormat sendVideoFormat;
            switch (videoFormat.Width)
            {
                case 270:
                    sendVideoFormat = VideoFormat.NV12_270x480_15Fps;
                    break;
                case 320:
                    sendVideoFormat = VideoFormat.NV12_320x180_15Fps;
                    break;
                case 360:
                    sendVideoFormat = VideoFormat.NV12_360x640_15Fps;
                    break;
                case 424:
                    sendVideoFormat = VideoFormat.NV12_424x240_15Fps;
                    break;
                case 480:
                    if (videoFormat.Height == 270)
                    {
                        sendVideoFormat = VideoFormat.NV12_480x270_15Fps;
                        break;
                    }
                    sendVideoFormat = VideoFormat.NV12_480x848_30Fps;
                    break;
                case 640:
                    sendVideoFormat = VideoFormat.NV12_640x360_15Fps;
                    break;
                case 720:
                    sendVideoFormat = VideoFormat.NV12_720x1280_30Fps;
                    break;
                case 848:
                    sendVideoFormat = VideoFormat.NV12_848x480_30Fps;
                    break;
                case 960:
                    sendVideoFormat = VideoFormat.NV12_960x540_30Fps;
                    break;
                default:
                    sendVideoFormat = VideoFormat.NV12_424x240_15Fps;
                    break;
            }

            return sendVideoFormat;
        }

        private void AddHue(Color color, byte[] buffer, int width, int height)
        {
            int start = 0;
            int widthXheight = width * height;
            int count = widthXheight / 2, length = buffer.Length;

            while (start < length)
            {
                //skip y
                start += widthXheight;

                //read u,v                
                int max = Math.Min(start + count + 1, length);

                for (int i = start; i < max; i += 2)
                {
                    switch (color)
                    {
                        case Color.Red:
                            SubtractWithoutRollover(buffer, i, 16);
                            AddWithoutRollover(buffer, i + 1, 50);
                            break;

                        case Color.Blue:
                            AddWithoutRollover(buffer, i, 50);
                            SubtractWithoutRollover(buffer, i + 1, 8);
                            break;

                        case Color.Green:
                            SubtractWithoutRollover(buffer, i, 33);
                            SubtractWithoutRollover(buffer, i + 1, 41);
                            break;

                        default:
                            break;
                    }
                }
                start += count;
            }
        }

        private void SubtractWithoutRollover(byte[] buffer, int index, byte value)
        {
            if (buffer[index] >= value)
            {
                buffer[index] -= value;
            }
            else
            {
                buffer[index] = byte.MinValue;
            }
            return;
        }

        private void AddWithoutRollover(byte[] buffer, int index, byte value)
        {
            int val = Convert.ToInt32(buffer[index]) + value;
            buffer[index] = (byte)Math.Min(val, byte.MaxValue);
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about video status changes. 
        /// Once the Status becomes active, then video can be sent.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnVideoSendStatusChanged(object sender, VideoSendStatusChangedEventArgs e)
        {
            CorrelationId.SetCurrentId(_mediaSession.CorrelationId);

            Log.Info(
                new CallerInfo(),
                LogContext.Media,
                "[{0}]: [VideoSendStatusChangedEventArgs(MediaSendStatus=<{1}>;PreferredVideoSourceFormat=<{2}>]",
                _mediaSession.Id,
                e.MediaSendStatus,
                e.PreferredVideoSourceFormat.VideoColorFormat);

            if (e.MediaSendStatus == MediaSendStatus.Active && _sendVideo == false)
            {
                //Start sending video once the Video Status changes to Active
                Log.Info(new CallerInfo(), LogContext.Media, $"[{_mediaSession.Id}] Start sending video");

                _sendVideo = true;
            }
        }
        #endregion

        #endregion
    }
}
