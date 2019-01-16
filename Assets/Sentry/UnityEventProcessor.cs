using System;
using System.Collections.Generic;
using Sentry.Extensibility;
using Sentry.Protocol;
using UnityEngine;
using DeviceOrientation = Sentry.Protocol.DeviceOrientation;

namespace Sentry.Unity
{
    public class UnityEventProcessor : ISentryEventProcessor
    {
        public SentryEvent Process(SentryEvent @event)
        {
            // Add some Unity specific context:

            var version = "0.0.1-alpha";
            @event.Sdk.AddPackage("github:sentry.unity", version);
            @event.Sdk.Name = "sentry.unity";
            @event.Sdk.Version = version;

            @event.Contexts.OperatingSystem.Name = SystemInfo.operatingSystem;

            @event.Contexts.Device.Name = SystemInfo.deviceName;
#pragma warning disable RECS0018 // Value is exact when expressing no battery level
            if (SystemInfo.batteryLevel != -1.0)
#pragma warning restore RECS0018
            {
                @event.Contexts.Device.BatteryLevel = (short?)(SystemInfo.batteryLevel * 100);
            }

            @event.Release = Application.version;

            // This is the approximate amount of system memory in megabytes.
            // This function is not supported on Windows Store Apps and will always return 0.
            @event.Contexts.Device.MemorySize = SystemInfo.systemMemorySize;

            @event.Contexts.Device.Timezone = TimeZoneInfo.Local;

            @event.Contexts.App.StartTime = DateTimeOffset.UtcNow.AddSeconds(-Time.realtimeSinceStartup);

            @event.SetExtra("unity:processorCount", SystemInfo.processorCount.ToString());
            @event.SetExtra("unity:supportsVibration", SystemInfo.supportsVibration.ToString());
            @event.SetExtra("unity:installMode", Application.installMode.ToString());

            // TODO: Will move to raw_description once parsing is done in Sentry
            @event.Contexts.OperatingSystem.Name = SystemInfo.operatingSystem;

            switch (Input.deviceOrientation)
            {
                case UnityEngine.DeviceOrientation.Portrait:
                case UnityEngine.DeviceOrientation.PortraitUpsideDown:
                    @event.Contexts.Device.Orientation = DeviceOrientation.Portrait;
                    break;
                case UnityEngine.DeviceOrientation.LandscapeLeft:
                case UnityEngine.DeviceOrientation.LandscapeRight:
                    @event.Contexts.Device.Orientation = DeviceOrientation.Landscape;
                    break;
                case UnityEngine.DeviceOrientation.FaceUp:
                case UnityEngine.DeviceOrientation.FaceDown:
                    // TODO: Add to protocol?
                    break;
            }

            var model = SystemInfo.deviceModel;
            if (model != SystemInfo.unsupportedIdentifier
                // Returned by the editor
                && model != "System Product Name (System manufacturer)")
            {
                @event.Contexts.Device.Model = model;
            }

            //device.DeviceType = SystemInfo.deviceType.ToString();
            //device.CpuDescription = SystemInfo.processorType;
            //device.BatteryStatus = SystemInfo.batteryStatus.ToString();

            @event.SetExtra("unity:batteryStatus", SystemInfo.batteryStatus.ToString());
            @event.SetExtra("unity:deviceType", SystemInfo.deviceType.ToString());
            @event.SetExtra("unity:processorType", SystemInfo.processorType);

            // This is the approximate amount of system memory in megabytes.
            // This function is not supported on Windows Store Apps and will always return 0.
            if (SystemInfo.systemMemorySize != 0)
            {
                @event.Contexts.Device.MemorySize = SystemInfo.systemMemorySize * 1048576L; // Sentry device mem is in Bytes
            }

            var gpu = new Gpu
            {
                Id = SystemInfo.graphicsDeviceID,
                Name = SystemInfo.graphicsDeviceName,
                VendorId = SystemInfo.graphicsDeviceVendorID,
                VendorName = SystemInfo.graphicsDeviceVendor,
                MemorySize = SystemInfo.graphicsMemorySize,
                MultiThreadedRendering = SystemInfo.graphicsMultiThreaded,
                NpotSupport = SystemInfo.npotSupport.ToString(),
                Version = SystemInfo.graphicsDeviceVersion,
                ApiType = SystemInfo.graphicsDeviceType.ToString()
            };

            // TODO: Hack until Context has a property GPU
            @event.Contexts.TryAdd("gpu", gpu);

            @event.Contexts.App.StartTime = DateTimeOffset.UtcNow
                // NOTE: Time API requires main thread
                .AddSeconds(-Time.realtimeSinceStartup);

            if (Debug.isDebugBuild)
            {
                @event.Contexts.App.BuildType = "debug";
            }
            else
            {
                @event.Contexts.App.BuildType = "release";
            }

#if UNITY_EDITOR
            @event.Contexts.Device.Simulator = true;
#else
            @event.Contexts.Device.Simulator = false;
#endif

            return @event;
        }
    }

    public class UnityEventExceptionProcessor : ISentryEventExceptionProcessor
    {
        public void Process(Exception exception, SentryEvent sentryEvent)
        {
            if (exception is UnityLogException ule)
            {
                sentryEvent.SentryExceptions = new[] { GetException(ule.LogString, ule.LogStackTrace) };
            }
        }

        private static SentryException GetException(string condition, string stackTrace)
        {
            var frames = new List<SentryStackFrame>();
            var exc = condition.Split(new char[] { ':' }, 2);
            var excType = exc[0];
            var excValue = exc[1].Substring(1); // strip the space
            var stackList = stackTrace.Split('\n');

            // The format is as follows:
            // Module.Class.Method[.Invoke] (arguments) (at filename:lineno)
            // where :lineno is optional, will be ommitted in builds
            for (var i = 0; i < stackList.Length; i++)
            {
                string functionName;
                string filename;
                int lineNo;

                var item = stackList[i];
                if (item == string.Empty)
                {
                    continue;
                }

                var closingParen = item.IndexOf(')');

                if (closingParen == -1)
                {
                    functionName = item;
                    lineNo = -1;
                    filename = string.Empty;
                }
                else
                {
                    try
                    {
                        functionName = item.Substring(0, closingParen + 1);
                        if (item.Substring(closingParen + 1, 5) != " (at ")
                        {
                            // we did something wrong, failed the check
                            Debug.Log("failed parsing " + item);
                            functionName = item;
                            lineNo = -1;
                            filename = string.Empty;
                        }
                        else
                        {
                            var colon = item.LastIndexOf(':', item.Length - 1, item.Length - closingParen);
                            if (closingParen == item.Length - 1)
                            {
                                filename = string.Empty;
                                lineNo = -1;
                            }
                            else if (colon == -1)
                            {
                                filename = item.Substring(closingParen + 6, item.Length - closingParen - 7);
                                lineNo = -1;
                            }
                            else
                            {
                                filename = item.Substring(closingParen + 6, colon - closingParen - 6);
                                lineNo = Convert.ToInt32(item.Substring(colon + 1, item.Length - 2 - colon));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        functionName = item;
                        lineNo = -1;
                        filename = string.Empty; // we have no clue
                    }
                }

                frames.Add(new SentryStackFrame
                {
                    FileName = filename,
                    Function = functionName,
                    LineNumber = lineNo,
                    InApp = functionName != null
                        && !functionName.StartsWith("UnityEngine", StringComparison.Ordinal)
                        && !functionName.StartsWith("System", StringComparison.Ordinal)
                });
            }

            frames.Reverse();

            var stacktrace = new SentryStackTrace();
            foreach (var frame in frames)
            {
                stacktrace.Frames.Add(frame);
            }

            return new SentryException
            {
                Stacktrace = stacktrace,
                Type = excType,
                Value = excValue
            };
        }
    }
}
