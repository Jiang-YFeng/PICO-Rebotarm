using Samples.Common;
using Orbbec;

namespace Samples.Color
{
    class Program
    {
        private static volatile bool _isRunning = true;

        static void Main(string[] args)
        {
            Console.Clear();
            Console.WriteLine("Color - Starting...");

            using var renderer = new OrbbecRenderer(title: "Color");

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                renderer.Close();
            };

            Pipeline? pipe = null;
            try
            {
                pipe = new Pipeline();
                using var config = new Config();

                // Match the color sensor anti-flicker setting to China's 50 Hz mains frequency.
                // Keep the default stream profile unchanged so this test isolates flicker behavior.
                using var device = pipe.GetDevice();
                const PropertyId powerLineFrequencyProperty =
                    PropertyId.OB_PROP_COLOR_POWER_LINE_FREQUENCY_INT;
                if (device.IsPropertySupported(powerLineFrequencyProperty,
                        PermissionType.OB_PERMISSION_READ_WRITE))
                {
                    device.SetIntProperty(powerLineFrequencyProperty,
                        (int)PowerLineFreqMode.OB_POWER_LINE_FREQ_MODE_50HZ);
                    Console.WriteLine("Color anti-flicker: 50 Hz");
                }
                else
                {
                    Console.WriteLine("Warning: Color 50 Hz anti-flicker setting is not supported by this device.");
                }

                // Use an explicit 30 FPS MJPEG profile for the 50 Hz flicker test.
                using var colorProfiles =
                    pipe.GetStreamProfileList(SensorType.OB_SENSOR_COLOR);
                using var requestedColorProfile = colorProfiles.GetVideoStreamProfile(
                    1280, 720, Format.OB_FORMAT_MJPG, 30);
                config.EnableStream(requestedColorProfile);
                pipe.Start(config);

                // Let auto exposure settle under the 50 Hz anti-flicker setting, then lock
                // the resulting exposure. This prevents visible brightness pumping.
                ColorFrame? settledColorFrame = null;
                for (int i = 0; i < 30; i++)
                {
                    using var warmupFrames = pipe.WaitForFrames(200);
                    if (warmupFrames == null) continue;

                    settledColorFrame?.Dispose();
                    settledColorFrame = warmupFrames.GetColorFrame();
                }

                if (settledColorFrame != null)
                {
                    using var activeProfile = settledColorFrame.GetStreamProfile();
                    using var activeVideoProfile = activeProfile.As<VideoStreamProfile>();
                    Console.WriteLine($"Active color profile: {activeVideoProfile.GetWidth()}x" +
                                      $"{activeVideoProfile.GetHeight()} @ {activeVideoProfile.GetFPS()} FPS, " +
                                      $"{activeProfile.GetFormat()}");
                    settledColorFrame.Dispose();
                }

                const PropertyId autoExposureProperty =
                    PropertyId.OB_PROP_COLOR_AUTO_EXPOSURE_BOOL;
                const PropertyId exposureProperty = PropertyId.OB_PROP_COLOR_EXPOSURE_INT;
                if (device.IsPropertySupported(autoExposureProperty,
                        PermissionType.OB_PERMISSION_READ_WRITE) &&
                    device.IsPropertySupported(exposureProperty,
                        PermissionType.OB_PERMISSION_READ_WRITE))
                {
                    var exposureRange = device.GetIntPropertyRange(exposureProperty);
                    int settledExposure = device.GetIntProperty(exposureProperty);
                    const int flickerSafeExposure = 50; // 10 ms in UVC 100 us units.
                    int lockedExposure = Math.Clamp(flickerSafeExposure,
                        exposureRange.min, exposureRange.max);
                    device.SetBoolProperty(autoExposureProperty, false);
                    device.SetIntProperty(exposureProperty, lockedExposure);
                    Console.WriteLine($"Color exposure range: min={exposureRange.min}, " +
                                      $"max={exposureRange.max}, step={exposureRange.step}, " +
                                      $"default={exposureRange.def}");
                    Console.WriteLine($"Color auto-exposure settled at: {settledExposure}");
                    Console.WriteLine($"Color exposure locked at: {lockedExposure} (10 ms)");
                }
                else
                {
                    Console.WriteLine("Warning: Color exposure lock is not supported by this device.");
                }

                int colorTextureIndex = renderer.AddVideoFrame();
                renderer.Closing += (e) =>
                {
                    Console.WriteLine("Window closing, stopping...");
                    _isRunning = false;
                };

                _ = Task.Run(() => StartStream(pipe, renderer, colorTextureIndex));

                renderer.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex.Message}");
            }
            finally
            {
                pipe?.Stop();
                Console.WriteLine("Color sample exited.");
            }
        }

        private static void StartStream(Pipeline pipeline, OrbbecRenderer renderer, int colorTextureIndex)
        {
            try
            {
                while (_isRunning)
                {
                    using var frameSet = pipeline.WaitForFrames(100);
                    if (frameSet == null) continue;

                    using var colorFrame = frameSet.GetColorFrame();

                    if (colorFrame != null)
                    {
                        byte[] data = new byte[colorFrame.GetDataSize()];
                        colorFrame.CopyData(ref data);
                        // Pass the original frame to support formats requiring Filter conversion like MJPG
                        renderer.UpdateVideoFrame(colorTextureIndex, (int)colorFrame.GetWidth(),
                            (int)colorFrame.GetHeight(), colorFrame.GetFormat(), data, colorFrame);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Start stream error: {ex.Message}");
            }
        }
    }
}
