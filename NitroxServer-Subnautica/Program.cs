﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NitroxModel.Core;
using NitroxModel.DataStructures.GameLogic;
using NitroxModel.DataStructures.Util;
using NitroxModel.Discovery;
using NitroxModel.Helper;
using NitroxModel.Logger;
using NitroxModel_Subnautica.Helper;
using NitroxServer;
using LibZeroTier;
using NitroxServer.ConsoleCommands.Processor;

namespace NitroxServer_Subnautica
{
    public class Program
    {
        private static readonly Dictionary<string, Assembly> resolvedAssemblyCache = new Dictionary<string, Assembly>();
        private static Lazy<string> gameInstallDir;
        private static string privateServerIdPath = Path.Combine(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? ""), "private_server");

        // Prevents Garbage Collection freeing this callback's memory. Causing an exception to occur for this handle.
        private static readonly ConsoleEventDelegate consoleCtrlCheckDelegate = ConsoleEventCallback;

        private static async Task Main(string[] args)
        {
            // The thread that writers to console is paused while selecting text in console. So console writer needs to be async.
            Log.Setup(asyncConsoleWriter: true, isConsoleApp: true);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;

            ConfigureCultureInfo();

            AppMutex.Hold(() =>
            {
                Log.Info("Waiting for 30 seconds on other Nitrox servers to initialize before starting..");
            }, 30000);

            // private netoworking
            ZeroTierAPI PrivateNetwork = null;
            if (args.Length > 0)
            {
                if (args[0].Equals("zerotier"))
                {
                    PrivateNetwork = new ZeroTierAPI( new API_Settings() { Web_API_Key = "AOVr7MaXugibaWm8kmRXOegCH84NBRnv", Internal_Id = "56e2eba4-db42-4082-9456-fe22051d54b8" }, new Network_Settings() { IP_Address_Prefix = "10.10.10", Network_Name = "Multiplayer Server", Network_Description = "Last Accessed: " + DateTime.UtcNow.Millisecond, Network_Privacy = Security.Public });
                    PrivateNetwork.NetworkChangeEvent += PrivateNetwork_NetworkChangeEvent;
                    PrivateNetwork.LogNetworkInfoEvent += PrivateNetwork_LogNetworkInfoEvent;
                    bool HasRun = false;
                    if (File.Exists(privateServerIdPath))
                    {
                        string contents = File.ReadAllLines(privateServerIdPath)[0];
                        if (await PrivateNetwork.IsValidNetwork(contents))
                        {
                            PrivateNetwork.StartServerAsync(contents).Wait();
                            HasRun = true;
                        }
                        else
                        {
                            Log.Info("[ZeroTier] [Web-API] Invalid Network Id, Creating New A Network Id!");
                        }
                    }
                    if (!HasRun)
                    {
                        PrivateNetwork.StartServerAsync().Wait();
                    }
                    // update private server id
                    if (PrivateNetwork != null)
                    {
                        File.WriteAllLines(privateServerIdPath, new string[] { PrivateNetwork.Network_Settings.Network_Id, "Activated" });
                    }
                }
            }
            // =================

            Server server;
            try
            {
                // Allow game path to be given as command argument
                if (args.Length > 0 && Directory.Exists(args[0]) && File.Exists(Path.Combine(args[0], "Subnautica.exe")))
                {
                    string gameDir = Path.GetFullPath(args[0]);
                    Log.Info($"Using game files from: {gameDir}");
                    gameInstallDir = new Lazy<string>(() => gameDir);
                }
                else
                {
                    gameInstallDir = new Lazy<string>(() =>
                    {
                        string gameDir = GameInstallationFinder.Instance.FindGame();
                        Log.Info($"Using game files from: {gameDir}");
                        return gameDir;
                    });
                }

                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomainOnAssemblyResolve;

                Map.Main = new SubnauticaMap();

                NitroxServiceLocator.InitializeDependencyContainer(new SubnauticaServerAutoFacRegistrar());
                NitroxServiceLocator.BeginNewLifetimeScope();

                server = NitroxServiceLocator.LocateService<Server>();
                await WaitForAvailablePortAsync(server.Port);
                if (!server.Start())
                {
                    throw new Exception("Unable to start server.");
                }
                Log.Info("Server is waiting for players!");

                CatchExitEvent();
            }
            finally
            {
                // Allow other servers to start initializing.
                AppMutex.Release();
            }

            Log.Info("To get help for commands, run help in console or /help in chatbox\n");
            ConsoleCommandProcessor cmdProcessor = NitroxServiceLocator.LocateService<ConsoleCommandProcessor>();
            while (server.IsRunning)
            {
                cmdProcessor.ProcessCommand(Console.ReadLine(), Optional.Empty, Perms.CONSOLE);
            }

            // private netoworking
            if (PrivateNetwork != null)
            {
                // update description for deleting after 136 hours of no use (4 days)
                PrivateNetwork.UpdateNetworkDescription(PrivateNetwork.Network_Settings.Network_Id, PrivateNetwork.Network_Settings.Network_Name, "Last Accessed: " + DateTime.UtcNow.Millisecond).Wait();
                // stop server
                PrivateNetwork.StopServerAsync(false).Wait();
                // update server usage status
                File.WriteAllLines(privateServerIdPath, new string[] { PrivateNetwork.Network_Settings.Network_Id, "Deactivated" });
            }
            // =================

            // Wait 2 seconds after close to view the output cause it closes to fast to see
            Task.Delay(2000).Wait();
        }

        private static async Task WaitForAvailablePortAsync(int port, int timeoutInSeconds = 30)
        {
            void PrintPortWarn(int timeRemaining)
            {
                Log.Warn($"Port {port} UDP is already in use. Retrying for {timeRemaining} seconds until it is available..");
            }

            Validate.IsTrue(timeoutInSeconds >= 5, "Timeout must be at least 5 seconds.");

            DateTimeOffset time = DateTimeOffset.UtcNow;
            bool first = true;
            using CancellationTokenSource source = new CancellationTokenSource(timeoutInSeconds * 1000);
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);

            try
            {
                while (true)
                {
                    source.Token.ThrowIfCancellationRequested();
                    try
                    {
                        socket.Bind(new IPEndPoint(IPAddress.Any, port));
                        break;
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.AddressAlreadyInUse)
                        {
                            throw;
                        }

                        if (first)
                        {
                            first = false;
                            PrintPortWarn(timeoutInSeconds);
                        }
                        else if (Environment.UserInteractive)
                        {
                            Console.CursorTop--;
                            Console.CursorLeft = 0;
                            PrintPortWarn(timeoutInSeconds - (DateTimeOffset.UtcNow - time).Seconds);
                        }
                        await Task.Delay(500, source.Token);
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                Log.Error(ex, "Port availability timeout reached.");
                throw;
            }
        }

        private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error(ex);
            }
            if (!Environment.UserInteractive || Console.In == StreamReader.Null)
            {
                return;
            }

            Console.WriteLine("Press L to open log file before closing. Press any other key to close . . .");
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.L)
            {
                Log.Info($"Opening log file at: {Log.FileName}..");
                string fileOpenerProgram = Environment.OSVersion.Platform switch
                {
                    PlatformID.MacOSX => "open",
                    PlatformID.Unix => "xdg-open",
                    _ => "explorer"
                };
                Process.Start(fileOpenerProgram, Log.FileName);
            }

            Environment.Exit(1);
        }

        private static Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string dllFileName = args.Name.Split(',')[0];
            if (!dllFileName.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase))
            {
                dllFileName += ".dll";
            }

            // Load DLLs where this program (exe) is located
            string dllPath = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? "", "lib", dllFileName);
            // Prefer to use Newtonsoft dll from game instead of our own due to protobuf issues. TODO: Remove when we do our own deserialization of game data instead of using the game's protobuf.
            if (dllPath.IndexOf("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) >= 0 || !File.Exists(dllPath))
            {
                // Try find game managed libraries
                dllPath = Path.Combine(gameInstallDir.Value, "Subnautica_Data", "Managed", dllFileName);
            }

            // Return cached assembly
            if (resolvedAssemblyCache.TryGetValue(dllPath, out Assembly val))
            {
                return val;
            }

            // Read assemblies as bytes as to not lock the file so that Nitrox can patch assemblies while server is running.
            using (FileStream stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (MemoryStream mstream = new MemoryStream())
            {
                stream.CopyTo(mstream);
                Assembly assembly = Assembly.Load(mstream.ToArray());
                resolvedAssemblyCache[dllPath] = assembly;
                return assembly;
            }
        }

        /**
         * Internal subnautica files are setup using US english number formats and dates.  To ensure
         * that we parse all of these appropriately, we will set the default cultureInfo to en-US.
         * This must best done for any thread that is spun up and needs to read from files (unless
         * we were to migrate to 4.5.)  Failure to set the context can result in very strange behaviour
         * throughout the entire application.  This originally manifested itself as a duplicate spawning
         * issue for players in Europe.  This was due to incorrect parsing of probability tables.
         */
        private static void ConfigureCultureInfo()
        {
            CultureInfo cultureInfo = new CultureInfo("en-US");
            
            // Although we loaded the en-US cultureInfo, let's make sure to set these incase the
            // default was overriden by the user.
            cultureInfo.NumberFormat.NumberDecimalSeparator = ".";
            cultureInfo.NumberFormat.NumberGroupSeparator = ",";

            Thread.CurrentThread.CurrentCulture = cultureInfo;
            Thread.CurrentThread.CurrentUICulture = cultureInfo;
            CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
            CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
        }

        private static void CatchExitEvent()
        {
            // Catch Exit Event
            PlatformID platid = Environment.OSVersion.Platform;

            // using *nix signal system to catch Ctrl+C
            if (platid == PlatformID.Unix || platid == PlatformID.MacOSX || platid == PlatformID.Win32NT || (int)platid == 128) // mono = 128
            {
                Console.CancelKeyPress += OnCtrlCPressed;
            }

            // better catch using WinAPI. This will handled process kill
            if (platid == PlatformID.Win32NT)
            {
                SetConsoleCtrlHandler(consoleCtrlCheckDelegate, true);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2) // close
            {
                StopAndExitServer();
            }
            return false;
        }

        private static void OnCtrlCPressed(object sender, ConsoleCancelEventArgs e)
        {
            StopAndExitServer();
        }

        private static void StopAndExitServer()
        {
            Log.Info("Exiting ...");
            Server.Instance.Stop();
            Environment.Exit(0);
        }

        // See: https://docs.microsoft.com/en-us/windows/console/setconsolectrlhandler
        private delegate bool ConsoleEventDelegate(int eventType);

        private static void PrivateNetwork_LogNetworkInfoEvent(object sender, string e)
        {
            Log.Info(e);
        }

        private static void PrivateNetwork_NetworkChangeEvent(object sender, ZeroTierAPI.NetworkChangedEventArgs e)
        {
            StatusChange[] NonImportantChanges = new StatusChange[] { StatusChange.Routes, StatusChange.MulticastSubscriptions, StatusChange.BroadcastEnabled, StatusChange.NetworkName, StatusChange.Bridge, StatusChange.AssignedAddresses, StatusChange.NetconfRevision };
            if (Array.IndexOf(NonImportantChanges, e.Change) < 0)
            {
                Log.Warn("[ZeroTier] [" + FormatStatusChange(e.Property) + "] " + e.Value);
            }
        }
        private static string FormatStatusChange(string PropertyName)
        {
            bool LastLetterWasLowerCase = false;
            int CharacterIndexCounter = 0;
            foreach(char Character in PropertyName)
            {
                string StrCharacter = Character.ToString();
                if (StrCharacter.ToUpper() == StrCharacter && LastLetterWasLowerCase && !StrCharacter.Equals(" "))
                {
                    PropertyName = PropertyName.Insert(CharacterIndexCounter, " ");
                    CharacterIndexCounter += 2;
                    LastLetterWasLowerCase = false;
                }
                else
                    CharacterIndexCounter++;
                if (StrCharacter.ToUpper() != StrCharacter)
                    LastLetterWasLowerCase = true;
                else
                    LastLetterWasLowerCase = false;
            }
            return PropertyName;
        }
    }
}
