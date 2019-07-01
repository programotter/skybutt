using Buttplug.Client;
using Buttplug.Core;
using Buttplug.Core.Messages;
using Buttplug.Client.Connectors.WebsocketConnector;
using LanguageExt;
using static LanguageExt.Prelude;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.IO;

namespace skybutt
{
    internal class Program
    {
        private static async Task WaitForKey()
        {
            Console.WriteLine("Press any key to continue.");
            while (!Console.KeyAvailable)
            {
                await Task.Delay(1);
            }
            Console.ReadKey(true);
        }

        private static async Task RunExample(string logFile)
        {
            // Now that we've seen all of the different parts of Buttplug, let's
            // put them together in a small program.
            //
            // This program will:
            // - Create an embedded (or possibly websocket) connector
            // - Scan, this time using real Managers, so we'll see devices
            //   (assuming you have them hooked up)
            // - List the connected devices for the user
            // - Let the user select a device, and trigger some sort of event on
            //   that device (vibration, thrusting, etc...).

            // As usual, we start off with our connector setup. We really don't
            // need access to the connector this time, so we can just pass the
            // created connector directly to the client.
            //var client = new ButtplugClient("skybutt Client",
            //    new ButtplugEmbeddedConnector("Example Server"));

            // If you want to use a websocket client and talk to a websocket
            // server instead, uncomment the following line and comment the one
            // above out. Note you will need to turn off TLS/SSL on the server.

            var client = new ButtplugClient("skybutt client", new
                    ButtplugWebsocketConnector(new Uri("ws://localhost:12345/buttplug")));

            await client.ConnectAsync();

            // At this point, if you want to see everything that's happening,
            // uncomment this block to turn on logging. Warning, it might be
            // pretty spammy.

            // void HandleLogMessage(object aObj, LogEventArgs aArgs) {
            // Console.WriteLine($"LOG: {aArgs.Message.LogMessage}"); }
            // client.Log += HandleLogMessage; await client.RequestLogAsync(ButtplugLogLevel.Debug);

            // Now we scan for devices. Since we didn't add any Subtype Managers
            // yet, this will go out and find them for us. They'll be reported in
            // the logs as they are found.
            //
            // We'll scan for devices, and print any time we find one.
            void HandleDeviceAdded(object aObj, DeviceAddedEventArgs aArgs)
            {
                Console.WriteLine($"Device connected: {aArgs.Device.Name}");
            }

            client.DeviceAdded += HandleDeviceAdded;

            void HandleDeviceRemoved(object aObj, DeviceRemovedEventArgs aArgs)
            {
                Console.WriteLine($"Device connected: {aArgs.Device.Name}");
            }

            client.DeviceRemoved += HandleDeviceRemoved;

            // The structure here is gonna get a little weird now, because I'm
            // using method scoped functions. We'll be defining our scanning
            // function first, then running it just to find any devices up front.
            // Then we'll define our command sender. Finally, with all of that
            // done, we'll end up in our main menu

            // Here's the scanning part. Pretty simple, just scan until the user
            // hits a button. Any time a new device is found, print it so the
            // user knows we found it.
            async Task ScanForDevices()
            {
                Console.WriteLine("Scanning for devices until key is pressed.");
                Console.WriteLine("Found devices will be printed to console.");
                await client.StartScanningAsync();
                await WaitForKey();

                // Stop scanning now, 'cause we don't want new devices popping up anymore.
                await client.StopScanningAsync();
            }

            // Scan for devices before we get to the main menu.
            await ScanForDevices();

            // Now we define the device control menus. After we've scanned for
            // devices, the user can use this menu to select a device, then
            // select an action for that device to take.
            async Task ControlDevice()
            {
                // Controlling a device has 2 steps: selecting the device to
                // control, and choosing which command to send. We'll just list
                // the devices the client has available, then search the device
                // message capabilities once that's done to figure out what we
                // can send. Note that this is using the Device Index, which is
                // assigned by the device manager and may not be sequential
                // (which is why we can't just use an array index).

                // Of course, if we don't have any devices yet, that's not gonna work.
                if (!client.Devices.Any())
                {
                    Console.WriteLine("No devices available. Please scan for a device.");
                    return;
                }

                var options = new List<uint>();

                foreach (var dev in client.Devices)
                {
                    Console.WriteLine($"{dev.Index}. {dev.Name}");
                    options.Add(dev.Index);
                }
                uint deviceChoice;
                if (options.Length() == 1)
                {
                    deviceChoice = client.Devices.Head().Index;
                } else
                {
                    Console.WriteLine("Choose a device: ");
                    if (!uint.TryParse(Console.ReadLine(), out deviceChoice) ||
                        !options.Contains(deviceChoice))
                    {
                        Console.WriteLine("Invalid choice");
                        return;
                    }
                }

                var device = client.Devices.First(dev => dev.Index == deviceChoice);

                Console.WriteLine("Watching Controller Rumble log file");
                await WatchLogFileAsync(logFile, client, device);
            }

            // And finally, we arrive at the main menu. We give the user the
            // choice to scan for more devices (in case they forgot to turn them
            // on earlier or whatever), run a command on a device, or just quit.
            while (true)
            {
                Console.WriteLine("1. Scan For More Devices\n2. Control Devices\n3. Quit\nChoose an option: ");
                if (!uint.TryParse(Console.ReadLine(), out var choice) ||
                    (choice == 0 || choice > 3))
                {
                    Console.WriteLine("Invalid choice, try again.");
                    continue;
                }

                switch (choice)
                {
                    case 1:
                        await ScanForDevices();
                        continue;
                    case 2:
                        await ControlDevice();
                        continue;
                    case 3:
                        return;

                    default:

                        // Due to the check above, we'll never hit this, but eh.
                        continue;
                }
            }
        }

        static async Task<ButtplugClientDevice> AttemptReconnect(ButtplugClient client, ButtplugClientDevice device)
        {
            Console.WriteLine("Attempting to reconnect device " + device.Name);
            var wh = new EventWaitHandle(false, EventResetMode.ManualReset);
            client.ScanningFinished += (s, e) => wh.Set();
            await client.StartScanningAsync();
            wh.WaitOne();
            var deviceOption = client.Devices.Find(d => d.Name == device.Name);
            return await deviceOption.Match((ButtplugClientDevice d) => Task.FromResult(d), async () =>
                {
                    await Task.Delay(500);
                    return await AttemptReconnect(client, device);
                });
        }

        static async Task WatchLogFileAsync(string filename, ButtplugClient client, ButtplugClientDevice device)
        {
            var wh = new AutoResetEvent(false);
            var fsw = new FileSystemWatcher(".");
            fsw.Filter = filename;
            fsw.EnableRaisingEvents = true;
            fsw.Changed += (s, e) => wh.Set();

            var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(0, SeekOrigin.End);

            // Watch the file
            double currentSetting = 0;
            using (var sr = new StreamReader(fs))
            {
                while (true)
                {
                    // Reset position for new file
                    if (fs.Position > fs.Length)
                        fs.Seek(0, SeekOrigin.Begin);

                    string s = sr.ReadLine();
                    if (s != null && (s.Contains("JNVaginal") || s.Contains("JNAnal")))
                    {
                        Either<Exception, VibrateCommand> vlOrE = ParseVibrateLine(s);
                        Console.WriteLine("[RumbleLog] " + vlOrE);
                        await vlOrE.Match(async vl =>
                        {
                            try
                            {
                                if (vl is VibrateStart)
                                {
                                    currentSetting = await HandleVibrateStart(device, vl as VibrateStart, currentSetting);
                                }
                                else if (vl is VibrateStop)
                                {
                                    await device.SendVibrateCmd(0);
                                    currentSetting = 0;
                                }
                            }
                            catch (ButtplugDeviceException)
                            {
                                Console.WriteLine("Device disconnected.");
                                device = await AttemptReconnect(client, device);
                            }
                        }, async e => Console.WriteLine(e));
                    }
                    else
                    {
                        wh.WaitOne(10);
                    }
                }
            }

            // TODO end loop
            //wh.Close();
        }

        private static async Task<double> HandleVibrateStart(ButtplugClientDevice device, VibrateStart vs, double currentSetting)
        {
            await device.SendVibrateCmd(vs.strength);
            // TODO handle intervals
            return await vs.time.MatchAsync(async time =>
                {
                    await Task.Delay(time);
                    await device.SendVibrateCmd(currentSetting);
                    return currentSetting;
                }, () => vs.strength);
        }

        static Either<Exception, VibrateCommand> ParseVibrateLine(string s)
        {
            Arr<string> parts = new Arr<string>(s.ToLower().Split(' '));
            if (parts.Contains("start"))
            {
                Map<string, string> dict = new Map<string, string>(parts.Filter(s_ => s_.Contains("=")).Map(p =>
                    {
                        string[] pp = p.Split('=');
                        return (pp.First(), pp.Last());
                    }));
                try
                {
                    return Right<VibrateCommand>(new VibrateStart(
                        dict["type"], dict.Find("time").Map(Double.Parse), dict.Find("interval").Map(Double.Parse), Double.Parse(dict["strength"])));
                }
                catch (Exception e)
                {
                    return Left(e);
                }
            }
            else if (parts.Contains("stop"))
                return Right<VibrateCommand>(new VibrateStop());
            else
                return Right<VibrateCommand>(new VibrateNone());
        }

        interface VibrateCommand {}

        class VibrateStart : VibrateCommand
        {
            private const double StrengthFactor = 100;
            public string type;
            public Option<TimeSpan> time;
            public Option<TimeSpan> interval;
            public double strength;

            public VibrateStart(string type, Option<double> time, Option<double> interval, double strength)
            {
                this.type = type;
                this.time =  time.Bind(t => t == -1 ? None : Some(TimeSpan.FromSeconds(t)));
                this.interval = interval.Bind(t => t == -1 ? None : Some(TimeSpan.FromSeconds(t)));
                this.strength = strength / StrengthFactor;
            }

            public override string ToString()
            {
                return "VibrateStart(type: " + type + ", time: " + time + ", strength: " + strength + ")";
            }
        }

        class VibrateStop : VibrateCommand {}

        class VibrateNone : VibrateCommand {}

        // Since not everyone is probably going to want to run under C# 7.1+,
        // we'll use a non-async Main and call to a Wait()'d task. C# 8 can't
        // come soon enough.
        private static void Main()
        {
            string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Skyrim", "Logs", "Script", "User", "Controller Rumble.0.log");
            // Setup a client, and wait until everything is done before exiting.
            RunExample(logFile).Wait();
        }
    }
}